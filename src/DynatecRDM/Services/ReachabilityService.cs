using System.Collections.Concurrent;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>Whether a connection's host answered the last time it was asked.</summary>
public enum Reachability
{
    /// <summary>Not checked yet, or not checkable (a running session, or reached through a gateway).</summary>
    Unknown = 0,
    /// <summary>Something accepted a connection on the Remote Desktop port.</summary>
    Responds = 1,
    /// <summary>Nothing answered on the port within the timeout.</summary>
    NoAnswer = 2,
}

/// <summary>The outcome of one check, and when and where it was made.</summary>
public sealed record ReachabilityResult(Reachability State, int Port, DateTime CheckedUtc);

/// <summary>
/// Tells whether hosts that are not connected would answer, so the app can say before a click
/// whether connecting is worth trying.
///
/// Windows does not answer ping by default, so this opens a TCP connection to the Remote Desktop
/// port and closes it again - the same first step mstsc takes, with nothing sent and nothing
/// logged in to. Hosts are only checked while someone is looking: the manager window or the quick
/// launch list asks for results through <see cref="Demand"/>, and the checks stop when both are
/// gone. Each host and port is asked once per round, however many connections share it.
/// </summary>
public sealed class ReachabilityService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FreshEnough = TimeSpan.FromSeconds(30);
    private const int TimeoutMs = 1500;
    private const int Parallel = 8;

    private readonly IDataStore _store;
    private readonly ISessionManager _sessions;
    private readonly ConcurrentDictionary<Guid, ReachabilityResult> _results = new();
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly object _gate = new();

    private int _demand;
    private Timer? _timer;
    private DateTime _lastRoundUtc = DateTime.MinValue;
    private bool _disposed;

    public ReachabilityService(IDataStore store, ISessionManager sessions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>Some results changed. Raised on a worker thread; listeners hop to their own.</summary>
    public event EventHandler? Changed;

    /// <summary>The latest result for a connection, or null when it has never been checked.</summary>
    public ReachabilityResult? Get(Guid connectionId) =>
        _results.TryGetValue(connectionId, out var result) ? result : null;

    /// <summary>
    /// Asks for results while the returned token is alive. The first demand checks right away
    /// unless the last round is recent; after that a round runs every minute until every token
    /// is disposed.
    /// </summary>
    public IDisposable Demand()
    {
        lock (_gate)
        {
            if (_disposed) return new Token(null);

            _demand++;
            if (_demand == 1)
            {
                var due = DateTime.UtcNow - _lastRoundUtc >= FreshEnough ? TimeSpan.Zero : Interval;
                _timer ??= new Timer(_ => _ = RunRoundAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(due, Interval);
            }
        }

        return new Token(this);
    }

    /// <summary>Checks one connection now, for "check now" or a freshly selected connection.</summary>
    public async Task CheckAsync(RdpConnection connection, CancellationToken ct = default)
    {
        if (connection is null || !IsCheckable(connection)) return;

        var port = PortOf(connection);
        var answered = await RdpConnectivity.IsReachableAsync(connection.Host, port, TimeoutMs, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        _results[connection.Id] = new ReachabilityResult(answered ? Reachability.Responds : Reachability.NoAnswer, port, DateTime.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when a result is recent enough that checking again would add nothing.</summary>
    public bool IsFresh(Guid connectionId) =>
        Get(connectionId) is { } result && DateTime.UtcNow - result.CheckedUtc < FreshEnough;

    private void Release()
    {
        lock (_gate)
        {
            if (_demand == 0) return;
            _demand--;
            if (_demand == 0) _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunRoundAsync()
    {
        if (_disposed || !await _round.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            IReadOnlyList<RdpConnection> connections;
            try
            {
                connections = await _store.GetConnectionsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Debug_($"Reachability round could not read the library: {ex.Message}");
                return;
            }

            // One probe per host and port, however many connections point there.
            var targets = new Dictionary<(string Host, int Port), List<Guid>>();
            foreach (var connection in connections)
            {
                if (!IsCheckable(connection)) continue;
                if (_sessions.FindByConnection(connection.Id) is { IsActive: true }) continue;

                var key = (connection.Host.Trim().ToLowerInvariant(), PortOf(connection));
                if (!targets.TryGetValue(key, out var ids)) targets[key] = ids = new List<Guid>();
                ids.Add(connection.Id);
            }

            // Forget connections that were deleted since.
            var known = connections.Select(c => c.Id).ToHashSet();
            foreach (var id in _results.Keys)
            {
                if (!known.Contains(id)) _results.TryRemove(id, out _);
            }

            using var limit = new SemaphoreSlim(Parallel);
            var probes = targets.Select(async pair =>
            {
                await limit.WaitAsync().ConfigureAwait(false);
                try
                {
                    var answered = await RdpConnectivity.IsReachableAsync(pair.Key.Host, pair.Key.Port, TimeoutMs).ConfigureAwait(false);
                    var result = new ReachabilityResult(answered ? Reachability.Responds : Reachability.NoAnswer, pair.Key.Port, DateTime.UtcNow);
                    foreach (var id in pair.Value) _results[id] = result;
                }
                finally
                {
                    limit.Release();
                }
            });
            await Task.WhenAll(probes).ConfigureAwait(false);

            _lastRoundUtc = DateTime.UtcNow;
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Reachability round failed: {ex.Message}");
        }
        finally
        {
            _round.Release();
        }
    }

    /// <summary>
    /// Only direct connections: through an RD Gateway the host itself is usually not reachable
    /// from here, so a direct check would report a working connection as dead.
    /// </summary>
    private static bool IsCheckable(RdpConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.Host) && connection.Gateway.UsageMethod == GatewayUsageMethod.DoNotUse;

    private static int PortOf(RdpConnection connection) =>
        connection.Port is > 0 and <= 65535 ? connection.Port : RdpConnectivity.DefaultRdpPort;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private sealed class Token : IDisposable
    {
        private ReachabilityService? _owner;

        public Token(ReachabilityService? owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
