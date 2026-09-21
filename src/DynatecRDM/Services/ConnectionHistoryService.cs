using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Keeps each connection's history: one entry per session, written when it starts, when it first
/// connects and when it ends. It only listens to the session manager, so launching and the
/// watchdog know nothing about it.
///
/// The session events arrive on the UI thread. Writes go to the store one at a time, in order,
/// so the end of a session can never overtake its start.
/// </summary>
public sealed class ConnectionHistoryService : IDisposable
{
    private const int ShutdownWriteMs = 2000;

    private readonly ISessionManager _sessions;
    private readonly IDataStore _store;
    private readonly Dictionary<Guid, ConnectionLogEntry> _open = new();
    private readonly object _writeLock = new();

    private Task _writes = Task.CompletedTask;
    private bool _disposed;

    public ConnectionHistoryService(ISessionManager sessions, IDataStore store)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>A connection's history changed: a session of it started, connected or ended.</summary>
    public event EventHandler<Guid>? Changed;

    /// <summary>
    /// Closes the entries a previous run left open, then starts listening. Called once at startup,
    /// before any session can be launched.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var abandoned = await _store.CloseAbandonedLogEntriesAsync(ct).ConfigureAwait(false);
            if (abandoned > 0)
                AppLog.Info($"{abandoned} session(s) from a previous run never recorded an end; marked as unknown.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not tidy the connection history.", ex);
        }

        _sessions.SessionStarted += OnStarted;
        _sessions.SessionStateChanged += OnStateChanged;
        _sessions.SessionEnded += OnEnded;
    }

    /// <summary>A connection's most recent sessions, newest first. Never throws.</summary>
    public async Task<IReadOnlyList<ConnectionLogEntry>> GetAsync(Guid connectionId, int limit, CancellationToken ct = default)
    {
        try
        {
            return await _store.GetConnectionLogAsync(connectionId, limit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the connection history.", ex);
            return [];
        }
    }

    /// <summary>
    /// Removes a connection's finished sessions; a running one stays and is still recorded when it
    /// ends. Queued behind the writes already waiting, so a session that ended a moment before
    /// cannot land after the clear and reappear. Returns how many were removed; throws when the
    /// store could not do it, so the caller can say so.
    /// </summary>
    public async Task<int> ClearAsync(Guid connectionId)
    {
        if (_disposed) return 0;

        Task<int> clear;
        lock (_writeLock)
        {
            clear = _writes.ContinueWith(_ => _store.ClearConnectionLogAsync(connectionId), TaskScheduler.Default).Unwrap();

            // The queue itself never faults: a failed clear must not stop the writes behind it.
            _writes = clear.ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        var removed = await clear.ConfigureAwait(false);
        Changed?.Invoke(this, connectionId);
        return removed;
    }

    private void OnStarted(object? sender, RdpSession session)
    {
        if (_disposed || _open.ContainsKey(session.Id)) return;

        var entry = new ConnectionLogEntry
        {
            Id = session.Id,
            ConnectionId = session.ConnectionId,
            MultiConfigId = session.MultiConfigId,
            Host = session.Host,
            StartedUtc = session.StartedUtc,
            Outcome = SessionOutcome.Open,
        };

        _open[session.Id] = entry;
        Save(entry);
    }

    private void OnStateChanged(object? sender, RdpSession session)
    {
        if (_disposed || !_open.TryGetValue(session.Id, out var entry)) return;

        switch (session.State)
        {
            case SessionState.Connected when entry.ConnectedUtc is null:
                entry.ConnectedUtc = DateTime.UtcNow;
                Save(entry);
                break;

            // Counted on the way in: the session's own attempt counter resets once it is stable again.
            case SessionState.Reconnecting when entry.ConnectedUtc is not null:
                entry.Reconnects++;
                Save(entry);
                break;
        }
    }

    private void OnEnded(object? sender, RdpSession session)
    {
        if (_disposed || !_open.Remove(session.Id, out var entry)) return;

        entry.EndedUtc = session.EndedUtc ?? DateTime.UtcNow;
        entry.Outcome = session.State == SessionState.Failed
            ? entry.ConnectedUtc is null ? SessionOutcome.Failed : SessionOutcome.Dropped
            : SessionOutcome.Ended;
        entry.Error = entry.Outcome == SessionOutcome.Ended ? null : session.LastError;

        Save(entry);
    }

    /// <summary>Queues a copy of the entry, so later changes to it cannot race the write.</summary>
    private void Save(ConnectionLogEntry entry)
    {
        var copy = entry.Clone();
        lock (_writeLock)
        {
            _writes = _writes.ContinueWith(async _ =>
            {
                try
                {
                    await _store.SaveLogEntryAsync(copy).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not record a session of {copy.Host} in the history.", ex);
                    return;
                }

                Changed?.Invoke(this, copy.ConnectionId);
            }, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// Sessions outlive the app, so the ones still running are recorded as such, with the time
    /// the app stopped watching them. Waits briefly for the queue so nothing is lost on exit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _sessions.SessionStarted -= OnStarted;
        _sessions.SessionStateChanged -= OnStateChanged;
        _sessions.SessionEnded -= OnEnded;

        var now = DateTime.UtcNow;
        var failed = new Dictionary<Guid, RdpSession>();
        try
        {
            foreach (var session in _sessions.Sessions)
                if (session.State == SessionState.Failed) failed[session.Id] = session;
        }
        catch (Exception ex) { AppLog.Warn("Could not read the sessions still open at exit.", ex); }

        foreach (var (id, entry) in _open)
        {
            entry.EndedUtc = now;
            // A window still saying why its connection failed ended in that failure, not with the app.
            if (failed.TryGetValue(id, out var session))
            {
                entry.Outcome = entry.ConnectedUtc is null ? SessionOutcome.Failed : SessionOutcome.Dropped;
                entry.Error = session.LastError;
            }
            else entry.Outcome = SessionOutcome.AppClosed;
            Save(entry);
        }
        _open.Clear();
        _disposed = true;

        Task pending;
        lock (_writeLock) pending = _writes;

        try
        {
            if (!pending.Wait(ShutdownWriteMs))
                AppLog.Warn("The connection history was still being written when the app closed.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Finishing the connection history failed.", ex);
        }
    }
}
