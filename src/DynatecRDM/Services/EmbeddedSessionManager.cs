using System.Collections.Concurrent;
using System.Drawing;
using DynatecRDM.Models;
using DynatecRDM.Rdp;

namespace DynatecRDM.Services;

/// <summary>
/// An <see cref="ISessionManager"/> that hosts the RDP ActiveX control in-process, in a window the
/// application owns (<see cref="RdpSessionWindow"/>), instead of launching an external
/// <c>mstsc</c> process. Because nothing opens an <c>.rdp</c> file, the April-2026
/// security warning never appears, and the remote resolution follows the window (true dynamic
/// resolution). The session window is an ordinary HWND, so placement and snapshots reuse the same
/// services as the external path.
///
/// State is event-driven off the control (no window-polling watchdog): the control reports connect,
/// sign-in and disconnect directly, and the reconnect policy runs from its disconnect reason.
/// </summary>
public sealed class EmbeddedSessionManager : ISessionManager, IDisposable
{
    private const string DisplayNameKey = "dynatec:displayname";
    private const int MultiConnectWaitMs = 8_000;

    /// <summary>How long a session must stay up before its reconnect attempts are forgiven.</summary>
    private const int SurvivalResetSeconds = 60;
    private const int OrphanSweepSeconds = 120;
    private const int MaintenanceTickMs = 5_000;

    private readonly IDataStore _store;
    private readonly ISecretProtector _protector;
    private readonly IMonitorService _monitors;
    private readonly IWindowPlacementService _placement;
    private readonly ISnapshotService _snapshots;
    private readonly Func<AppSettings> _settings;
    private readonly AppSettings _fallback = new();

    private readonly ConcurrentDictionary<Guid, RdpSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Runtime> _runtime = new();
    private readonly ConcurrentDictionary<Guid, byte> _closingMultis = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly System.Threading.Timer _maintenance;
    private DateTime _lastOrphanSweepUtc = DateTime.UtcNow;
    private int _maintenanceBusy;
    private volatile bool _disposed;

    public EmbeddedSessionManager(
        IDataStore store,
        ISecretProtector protector,
        IMonitorService monitors,
        IWindowPlacementService placement,
        ISnapshotService snapshots,
        Func<AppSettings> settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // One timer services every session: refreshing thumbnails, forgiving reconnect attempts once
        // a session has proved stable, and clearing snapshots left behind by sessions that ended.
        _maintenance = new System.Threading.Timer(
            OnMaintenanceTick, null, MaintenanceTickMs, MaintenanceTickMs);
    }

    private async void OnMaintenanceTick(object? state)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _maintenanceBusy, 1) == 1) return;

        try
        {
            var now = DateTime.UtcNow;
            var config = Cfg;
            var snapshotsWanted = config.EnableSnapshots && config.SnapshotIntervalSeconds > 0;

            foreach (var pair in _runtime)
            {
                var runtime = pair.Value;
                if (runtime.Closed || !runtime.Session.IsActive) continue;

                // A session that has stayed up is no longer "retrying": forgive the attempts so a
                // much later drop gets the full allowance again.
                if (runtime.Session.ReconnectAttempts > 0
                    && runtime.ConnectedSinceUtc != default
                    && (now - runtime.ConnectedSinceUtc).TotalSeconds >= SurvivalResetSeconds)
                {
                    runtime.Session.ReconnectAttempts = 0;
                }

                if (snapshotsWanted
                    && (now - runtime.LastSnapshotAttemptUtc).TotalSeconds >= config.SnapshotIntervalSeconds)
                {
                    runtime.LastSnapshotAttemptUtc = now;
                    await CaptureSnapshotAsync(runtime, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if ((now - _lastOrphanSweepUtc).TotalSeconds >= OrphanSweepSeconds)
            {
                _lastOrphanSweepUtc = now;
                var live = new List<Guid>(_sessions.Count);
                foreach (var pair in _sessions) live.Add(pair.Key);
                try { _snapshots.CleanupOrphans(live); }
                catch (Exception ex) { AppLog.Warn("Clearing orphaned session snapshots failed.", ex); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("A session maintenance pass failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _maintenanceBusy, 0);
        }
    }

    public event EventHandler<RdpSession>? SessionStarted;
    public event EventHandler<RdpSession>? SessionStateChanged;
    public event EventHandler<RdpSession>? SessionEnded;

    public IReadOnlyList<RdpSession> Sessions
    {
        get
        {
            var list = new List<RdpSession>(_sessions.Count);
            foreach (var pair in _sessions) list.Add(pair.Value);
            list.Sort(static (a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
            return list;
        }
    }

    private AppSettings Cfg
    {
        get { try { return _settings() ?? _fallback; } catch { return _fallback; } }
    }

    public async Task<RdpSession?> LaunchAsync(
        RdpConnection connection,
        DisplaySettings? displayOverride = null,
        Guid? credentialOverride = null,
        Guid? multiConfigId = null,
        Dictionary<string, string>? extraProperties = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await LaunchCoreAsync(connection, displayOverride, credentialOverride, multiConfigId, extraProperties, ct)
                .ConfigureAwait(false);
        }
        finally { _launchGate.Release(); }
    }

    private async Task<RdpSession?> LaunchCoreAsync(
        RdpConnection connection, DisplaySettings? displayOverride, Guid? credentialOverride,
        Guid? multiConfigId, Dictionary<string, string>? extraProperties, CancellationToken ct)
    {

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            AppLog.Error($"'{connection.Name}' has no host and cannot be launched.");
            return null;
        }

        // One session per connection: bring the existing one forward instead of opening a second.
        if (FindByConnection(connection.Id) is { } running)
        {
            AppLog.Info($"'{connection.Name}' is already connected; focusing that session.");
            Focus(running.Id);
            return running;
        }

        var display = (displayOverride ?? connection.Display).Clone();

        string? nameOverride = null;
        if (extraProperties is { Count: > 0 } && extraProperties.TryGetValue(DisplayNameKey, out var label))
            nameOverride = label;

        // Credential resolution touches the database and DPAPI; keep it off the UI thread.
        var (credentialSet, plainPassword) = await ResolveCredentialAsync(connection, credentialOverride, ct)
            .ConfigureAwait(false);

        var layout = DisplayLayout.Resolve(display, _monitors.GetMonitors());
        var plan = BuildPlan(display, layout);
        var credential = new RdpCredential(credentialSet, plainPassword);

        var session = new RdpSession
        {
            ConnectionId = connection.Id,
            MultiConfigId = multiConfigId,
            DisplayName = nameOverride ?? connection.Name,
            Host = connection.Host,
            Display = display,
            ProcessId = Environment.ProcessId,
            State = SessionState.Launching,
            AutoReconnect = connection.AutoReconnect,
            MaxReconnectAttempts = connection.MaxReconnectAttempts,
            ReconnectDelaySeconds = connection.ReconnectDelaySeconds,
            StartedUtc = DateTime.UtcNow,
        };

        var runtime = new Runtime(session)
        {
            CredentialOverride = credentialOverride,
            ExtraProperties = extraProperties is { Count: > 0 }
                ? new Dictionary<string, string>(extraProperties, StringComparer.OrdinalIgnoreCase)
                : null,
        };

        try
        {
            await OnUiAsync(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var window = new RdpSessionWindow(session.DisplayName);
                runtime.Window = window;
                // A full-screen entry on the window's own system menu, as the built-in client has.
                window.EnableFullScreenMenu(
                    Resources.Strings.Session_Menu_FullScreen,
                    Resources.Strings.Session_Menu_ExitFullScreen);
                WireWindow(runtime, connection);

                window.AlwaysOnTop = display.AlwaysOnTop;
                window.PlaceAt(
                    ToRectangle(layout),
                    layout.IsFullScreen,
                    // winposstr's rectangle is where the session sits when it is not full screen.
                    new Rectangle(layout.WindowRect.Left, layout.WindowRect.Top, layout.WindowRect.Width, layout.WindowRect.Height),
                    // ShowCommand 3 is "open maximised", which the layout works out for us.
                    maximized: layout.ShowCommand == 3);
                window.Show();
                session.WindowHandle = window.Handle;   // a real HWND: snapshots and focus reuse it
                session.State = SessionState.Connecting;
                // Publish before Connect: COM can synchronously report a failure or close the window.
                _runtime[session.Id] = runtime;
                _sessions[session.Id] = session;
                SessionStarted?.Invoke(this, session);
                if (runtime.Closed) return;
                window.Start(connection, plan, credential, runtime.ExtraProperties, Cfg.SessionBarEnabled);
                ReportUnsupported(session, window);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(runtime, SessionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not start the embedded session for '{connection.Name}'.", ex);
            Finish(runtime, SessionState.Failed, ex.Message);
            return null;
        }

        if (runtime.Closed) return null;
        AppLog.Info($"Launched '{connection.Name}' ({connection.FullAddress}) in an embedded window.");

        // Launch count and last-connected are history: if this is skipped the numbers simply stop,
        // and nothing can reconstruct them afterwards.
        var connectionId = connection.Id;
        _ = Task.Run(async () =>
        {
            try { await _store.RecordLaunchAsync(connectionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn($"Could not record the launch of '{session.DisplayName}'.", ex); }
        });

        return session;
    }

    public async Task<IReadOnlyList<RdpSession>> LaunchMultiAsync(MultiConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var started = new List<RdpSession>(config.Items.Count);
        if (config.Items.Count == 0) return started;

        if (config.InitialDelayMs > 0) await Task.Delay(config.InitialDelayMs, ct).ConfigureAwait(false);

        var items = new List<MultiConfigItem>();
        foreach (var item in config.Items) if (item.Enabled) items.Add(item);
        items.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        var parallelWaits = config.Sequential ? null : new List<Task>(items.Count);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var connection = await _store.GetConnectionAsync(item.ConnectionId, ct).ConfigureAwait(false);
            if (connection is null)
            {
                AppLog.Warn($"Multi-config '{config.Name}': connection {item.ConnectionId} no longer exists - skipped.");
                continue;
            }

            var display = item.Display.HasAny ? item.Display.ApplyTo(connection.Display) : connection.Display.Clone();

            Dictionary<string, string>? extra = null;
            if (item.CustomPropertyOverrides.Count > 0)
                extra = new Dictionary<string, string>(item.CustomPropertyOverrides, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(item.DisplayNameOverride))
            {
                extra ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                extra[DisplayNameKey] = item.DisplayNameOverride!;
            }

            var session = await LaunchAsync(connection, display, item.CredentialSetIdOverride, config.Id, extra, ct)
                .ConfigureAwait(false);
            if (session is null) continue;

            if (item.AutoReconnectOverride.HasValue) session.AutoReconnect = item.AutoReconnectOverride.Value;
            started.Add(session);

            if (!_runtime.TryGetValue(session.Id, out var runtime)) continue;
            runtime.CloseTogether = config.CloseTogether;

            if (config.Sequential)
            {
                await WaitForConnectAsync(runtime, MultiConnectWaitMs, ct).ConfigureAwait(false);
                if (item.DelayMs > 0) await Task.Delay(item.DelayMs, ct).ConfigureAwait(false);
            }
            else
            {
                parallelWaits!.Add(WaitForConnectAsync(runtime, MultiConnectWaitMs, ct));
            }
        }

        if (parallelWaits is { Count: > 0 })
        {
            try { await Task.WhenAll(parallelWaits).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        try { await _store.RecordMultiConfigLaunchAsync(config.Id, ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"Could not record the launch of multi-config '{config.Name}'.", ex); }

        return started;
    }

    public Task CloseAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_runtime.TryGetValue(sessionId, out var runtime)) return Task.CompletedTask;

        runtime.Session.UserInitiatedClose = true;
        runtime.Closed = true;
        Finish(runtime, SessionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task CloseMultiAsync(Guid multiConfigId, CancellationToken ct = default)
    {
        List<Task>? tasks = null;
        foreach (var pair in _runtime)
        {
            if (pair.Value.Session.MultiConfigId != multiConfigId) continue;
            (tasks ??= new List<Task>()).Add(CloseAsync(pair.Key, ct));
        }
        return tasks is null ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    public async Task ReconnectAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_runtime.TryGetValue(sessionId, out var runtime)) return;
        if (runtime.Closed) return;

        var connection = await _store.GetConnectionAsync(runtime.Session.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            AppLog.Warn($"Cannot reconnect '{runtime.Session.DisplayName}': the connection was deleted.");
            return;
        }

        runtime.Session.ReconnectAttempts = 0;
        await DoReconnectAsync(runtime, connection, ct).ConfigureAwait(false);
    }

    public void Focus(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        var hwnd = session.WindowHandle;
        if (hwnd != IntPtr.Zero) _placement.FocusWindow(hwnd);
    }

    /// <summary>
    /// Minimises a session's own window.
    ///
    /// The session bar's buttons normally work by clicking the buttons on Remote Desktop's native
    /// connection bar - which an embedded session deliberately does not have, since the app draws its
    /// own chrome. The bar asks here instead, and this simply acts on the window the app owns.
    /// </summary>
    public bool TryMinimize(Guid sessionId)
    {
        if (!_runtime.TryGetValue(sessionId, out var runtime)) return false;

        var done = false;
        OnUi(() =>
        {
            var window = runtime.Window;
            if (window is null || window.IsDisposed) return;
            // Always-on-top has to go first, or a full-screen window comes straight back up.
            // Drop out of the topmost band only long enough for the minimise to take; the window
            // restores its own always-on-top state when it comes back.
            window.MinimizeSession();
            done = true;
        });
        return done;
    }

    /// <summary>Takes a session in or out of full screen. Returns false when it has no live window.</summary>
    public bool TrySetFullScreen(Guid sessionId, bool fullScreen)
    {
        if (!_runtime.TryGetValue(sessionId, out var runtime)) return false;

        var done = false;
        OnUi(() =>
        {
            var window = runtime.Window;
            if (window is null || window.IsDisposed) return;
            if (window.IsFullScreen != fullScreen) window.ToggleFullScreen();
            done = true;
        });
        return done;
    }

    internal void SetSessionBarEnabled(bool enabled)
    {
        OnUi(() =>
        {
            foreach (var runtime in _runtime.Values)
            {
                if (runtime.Closed || runtime.Window is not { IsDisposed: false } window) continue;
                try { RdpControlConfigurator.SetConnectionBarVisible(window.Host.Control, !enabled); }
                catch (Exception ex) { AppLog.Warn("Could not change the native session bar visibility.", ex); }
            }
        });
    }

    public RdpSession? FindByConnection(Guid connectionId)
    {
        foreach (var pair in _sessions)
            if (pair.Value.ConnectionId == connectionId && pair.Value.IsActive) return pair.Value;
        return null;
    }

    public bool IsMultiConfigRunning(Guid multiConfigId)
    {
        foreach (var pair in _sessions)
            if (pair.Value.MultiConfigId == multiConfigId && pair.Value.IsActive) return true;
        return false;
    }

    public async Task RefreshSnapshotsAsync(CancellationToken ct = default)
    {
        if (!Cfg.EnableSnapshots) return;

        foreach (var pair in _runtime)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = pair.Value;
            if (runtime.Closed || !runtime.Session.IsActive) continue;
            await CaptureSnapshotAsync(runtime, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- internals

    private void WireWindow(Runtime runtime, RdpConnection connection)
    {
        var window = runtime.Window!;
        var session = runtime.Session;

        window.HandleCreated += (_, _) => session.WindowHandle = window.Handle;
        window.HandleDestroyed += (_, _) => session.WindowHandle = IntPtr.Zero;
        window.FormClosing += (_, e) =>
        {
            if (!e.Cancel) session.UserInitiatedClose = true;
        };

        window.Connected += (_, _) => SetState(runtime, SessionState.Connecting);
        window.LoginComplete += (_, _) =>
        {
            runtime.ConnectedOnce.TrySetResult(true);
            SetState(runtime, SessionState.Connected);
            runtime.ConnectedSinceUtc = DateTime.UtcNow;

            // The attempt count is NOT cleared here. A server that accepts the logon and then drops
            // the session would otherwise reset the counter on every attempt and reconnect for ever.
            // It is cleared only once a session has survived a while - see InspectSessionsAsync.
            _ = CaptureSoonAsync(runtime);
        };
        window.Disconnected += (_, info) => OnDisconnected(runtime, connection, info);
        window.FormClosed += (_, _) =>
        {
            // The user closed the window itself: treat it as a deliberate close.
            if (!runtime.Closed) { session.UserInitiatedClose = true; Finish(runtime, SessionState.Disconnected); }
        };
    }

    private void OnDisconnected(Runtime runtime, RdpConnection connection, RdpDisconnectInfo info)
    {
        var session = runtime.Session;
        AppLog.Info($"RDP disconnected '{session.DisplayName}': reason={info.Code}, kind={info.Kind}, "
            + $"state={session.State}, requestedClose={session.UserInitiatedClose}, detail={info.Message}");
        runtime.ConnectedOnce.TrySetResult(false);
        if (runtime.Closed || session.UserInitiatedClose) return;

        // A reconnect disconnects on purpose. Without this the deliberate teardown would be read as
        // a dropped link and end the session we are in the middle of bringing back.
        if (runtime.SuppressDisconnect) return;

        var canReconnect = info.Kind == RdpDisconnectKind.ConnectionLost
            && session.AutoReconnect
            && Cfg.WatchdogEnabled   // the watchdog switch governs this path too, as it does the external one
            && (session.MaxReconnectAttempts <= 0 || session.ReconnectAttempts < session.MaxReconnectAttempts);

        if (!canReconnect)
        {
            // Reason 1 also covers local startup errors. A connection which never logged in is
            // not a clean user disconnect when no close was requested.
            var state = info.Kind == RdpDisconnectKind.UserInitiated && runtime.ConnectedSinceUtc != default
                ? SessionState.Disconnected : SessionState.Failed;
            // Always carry a reason: a bare "Failed" tells the user nothing.
            var reason = info.Message ?? (info.Kind == RdpDisconnectKind.LogonFailed
                ? Resources.Strings.Session_State_Failed
                : null);
            Finish(runtime, state, reason);
            return;
        }

        session.ReconnectAttempts++;
        SetState(runtime, SessionState.Reconnecting);
        _ = ReconnectAfterDelayAsync(runtime, connection);
    }

    private async Task ReconnectAfterDelayAsync(Runtime runtime, RdpConnection connection)
    {
        var delay = Math.Max(1, runtime.Session.ReconnectDelaySeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false); }
        catch { return; }
        if (runtime.Closed) return;
        await DoReconnectAsync(runtime, connection, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DoReconnectAsync(Runtime runtime, RdpConnection connection, CancellationToken ct)
    {
        // One reconnect at a time, and never two from the same drop.
        if (Interlocked.Exchange(ref runtime.Reconnecting, 1) == 1) return;
        try
        {
            var (set, password) = await ResolveCredentialAsync(connection, runtime.CredentialOverride, ct).ConfigureAwait(false);
            var display = runtime.Session.Display;
            var layout = DisplayLayout.Resolve(display, _monitors.GetMonitors());
            var plan = BuildPlan(display, layout);
            var credential = new RdpCredential(set, password);

            // A session that is still up has to be taken down first. The control refuses both the
            // server property and Connect() while it is connected, and that failure used to be
            // swallowed after the state had already been moved to "Connecting" - leaving a perfectly
            // healthy session displaying "Connecting" for ever. This is the normal path for the
            // Reconnect button, which is offered on a live session.
            runtime.SuppressDisconnect = true;
            try
            {
                if (!await EnsureDisconnectedAsync(runtime).ConfigureAwait(false)) return;
            }
            finally { runtime.SuppressDisconnect = false; }
            if (runtime.Closed) return;

            await OnUiAsync(() =>
            {
                if (runtime.Closed || runtime.Window is null) return;
                SetState(runtime, SessionState.Connecting);
                runtime.Window.Start(connection, plan, credential, runtime.ExtraProperties, Cfg.SessionBarEnabled);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reconnect '{runtime.Session.DisplayName}'.", ex);
            Finish(runtime, SessionState.Failed, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref runtime.Reconnecting, 0);
        }
    }

    /// <summary>
    /// Takes a live session down so it can be started again, and waits for the control to actually
    /// report itself disconnected. Returns false when it never did, in which case the session is
    /// left exactly as it was rather than being half torn down.
    /// </summary>
    private async Task<bool> EnsureDisconnectedAsync(Runtime runtime)
    {
        var connected = false;
        OnUi(() => connected = runtime.Window?.Host.IsConnected == true);
        if (!connected) return true;

        OnUi(() => runtime.Window?.Host.Disconnect());

        for (var attempt = 0; attempt < 40; attempt++)   // up to about four seconds
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (runtime.Closed) return false;

            var stillConnected = false;
            OnUi(() => stillConnected = runtime.Window?.Host.IsConnected == true);
            if (!stillConnected) return true;
        }

        AppLog.Warn($"'{runtime.Session.DisplayName}' did not disconnect in time to be reconnected.");
        return false;
    }

    /// <summary>
    /// Says so when a connection pins raw .rdp settings the hosted control cannot express. The .rdp
    /// file could carry anything; the control only has the members it has. Warning keeps a dropped
    /// setting visible instead of letting the session quietly behave differently from the file.
    /// </summary>
    private static void ReportUnsupported(RdpSession session, Rdp.RdpSessionWindow window)
    {
        var unsupported = window.UnsupportedSettings;
        if (unsupported.Count == 0) return;

        AppLog.Warn(
            $"'{session.DisplayName}' pins {unsupported.Count} .rdp setting(s) the in-process client " +
            $"cannot apply, so they were not used: {string.Join(", ", unsupported)}.");
    }

    private void Finish(Runtime runtime, SessionState state, string? error = null)
    {
        if (Interlocked.Exchange(ref runtime.Finished, 1) == 1) return;

        var session = runtime.Session;
        runtime.Closed = true;
        _sessions.TryRemove(session.Id, out _);
        _runtime.TryRemove(session.Id, out _);
        session.EndedUtc = DateTime.UtcNow;
        runtime.ConnectedOnce.TrySetResult(false);

        _ = Task.Run(() =>
        {
            try { _snapshots.Remove(session); }
            catch (Exception ex) { AppLog.Warn($"Could not remove the snapshot of '{session.DisplayName}'.", ex); }
        });

        OnUi(() =>
        {
            try { runtime.Window?.Close(); } catch { }
            try { runtime.Window?.Dispose(); } catch { }
            runtime.Window = null;
        });

        RaiseOnUi(() =>
        {
            if (error is not null) session.LastError = error;
            session.SnapshotPath = null;
            session.WindowHandle = IntPtr.Zero;
            session.State = state;
            SessionStateChanged?.Invoke(this, session);
            SessionEnded?.Invoke(this, session);
        });

        if (runtime.CloseTogether && session.MultiConfigId is { } multiConfigId)
            CloseMultiConfigSiblings(multiConfigId);
    }

    private void CloseMultiConfigSiblings(Guid multiConfigId)
    {
        if (!IsMultiConfigRunning(multiConfigId)) return;
        if (!_closingMultis.TryAdd(multiConfigId, 0)) return;

        _ = Task.Run(async () =>
        {
            try { await CloseMultiAsync(multiConfigId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Closing the remaining sessions of a multi-config failed.", ex); }
            finally { _closingMultis.TryRemove(multiConfigId, out _); }
        });
    }

    private void SetState(Runtime runtime, SessionState state)
    {
        if (runtime.Closed) return;
        var session = runtime.Session;
        RaiseOnUi(() =>
        {
            if (runtime.Closed || session.State == state) return;
            session.State = state;
            SessionStateChanged?.Invoke(this, session);
        });
    }

    private async Task CaptureSoonAsync(Runtime runtime)
    {
        try { await Task.Delay(1_500).ConfigureAwait(false); }
        catch { return; }
        await CaptureSnapshotAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CaptureSnapshotAsync(Runtime runtime, CancellationToken ct)
    {
        if (!Cfg.EnableSnapshots || runtime.Closed) return;
        if (Interlocked.Exchange(ref runtime.SnapshotBusy, 1) == 1) return;

        var session = runtime.Session;
        try
        {
            if (session.WindowHandle == IntPtr.Zero) return;
            var path = await _snapshots.CaptureAsync(session, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(path) && !runtime.Closed)
            {
                var captured = DateTime.UtcNow;
                RaiseOnUi(() => { session.SnapshotPath = path; session.LastSnapshotUtc = captured; });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Could not snapshot '{session.DisplayName}'.", ex); }
        finally { Interlocked.Exchange(ref runtime.SnapshotBusy, 0); }
    }

    private async Task<(CredentialSet? Set, string? Password)> ResolveCredentialAsync(
        RdpConnection connection, Guid? credentialOverride, CancellationToken ct)
    {
        var credentialId = credentialOverride ?? connection.CredentialSetId;
        if (!credentialId.HasValue) return (null, null);

        var set = await _store.GetCredentialSetAsync(credentialId.Value, ct).ConfigureAwait(false);
        if (set is null)
        {
            AppLog.Warn($"Credential set {credentialId.Value} used by '{connection.Name}' no longer exists.");
            return (null, null);
        }

        string? password = null;
        if (set.ProtectedPassword is { Length: > 0 })
        {
            try { password = _protector.Unprotect(set.ProtectedPassword); }
            catch (Exception ex) { AppLog.Warn($"Could not decrypt the password of '{set.Name}'.", ex); }
        }
        return (set, password);
    }

    private static RdpDisplayPlan BuildPlan(DisplaySettings display, DisplayLayout layout) => new(
        DesktopWidth: layout.DesktopWidth,
        DesktopHeight: layout.DesktopHeight,
        ColorDepth: NormalizeColorDepth(display.ColorDepth),
        UseMultimon: layout.UsesMultimon,
        Resize: layout.Resize,
        DesktopScaleFactor: display.DesktopScaleFactor,
        DeviceScaleFactor: display.DeviceScaleFactor)
    {
        // The displays the session should cover, in the connection's own order - the first of them
        // becomes the session's primary. Empty means every display, which is what multimon does.
        SelectedMonitorIds = layout.MstscIds,
    };

    private static int NormalizeColorDepth(int bpp) => bpp is 8 or 15 or 16 or 24 or 32 ? bpp : 32;

    private static Rectangle ToRectangle(DisplayLayout layout)
    {
        var area = layout.IsFullScreen ? (layout.FullScreenArea ?? layout.WindowRect) : layout.WindowRect;
        if (area.Width < DisplayLayout.MinDesktopEdge || area.Height < DisplayLayout.MinDesktopEdge)
            return new Rectangle(200, 120, 1280, 800);
        return new Rectangle(area.Left, area.Top, area.Width, area.Height);
    }

    private async Task WaitForConnectAsync(Runtime runtime, int timeoutMs, CancellationToken ct)
    {
        try { await runtime.ConnectedOnce.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { SafeRun(action); return; }
        try { dispatcher.Invoke(() => SafeRun(action)); }
        catch (Exception ex) { AppLog.Warn("Could not run a session action on the UI thread.", ex); }
    }

    private static Task OnUiAsync(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, ct).Task;
    }

    private static void RaiseOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { SafeRun(action); return; }
        try { _ = dispatcher.InvokeAsync(() => SafeRun(action)); }
        catch (Exception ex) { AppLog.Warn("Could not marshal a session update to the UI thread.", ex); }
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error("A session update handler threw.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _maintenance.Dispose(); } catch { }
        foreach (var pair in _runtime)
        {
            try
            {
                pair.Value.Session.UserInitiatedClose = true;
                Finish(pair.Value, SessionState.Disconnected);
            }
            catch { }
        }
        _runtime.Clear();
        _sessions.Clear();
    }

    /// <summary>Per-session bookkeeping the bindable session model must not carry.</summary>
    private sealed class Runtime(RdpSession session)
    {
        public readonly RdpSession Session = session;
        public readonly TaskCompletionSource<bool> ConnectedOnce =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RdpSessionWindow? Window;
        public Guid? CredentialOverride;
        public Dictionary<string, string>? ExtraProperties;
        public bool CloseTogether;

        public volatile bool Closed;
        public int SnapshotBusy;
        public int Finished;

        /// <summary>When the session last finished signing in, for the survival rule.</summary>
        public DateTime ConnectedSinceUtc;

        /// <summary>When a snapshot was last attempted, so the cadence is honoured.</summary>
        public DateTime LastSnapshotAttemptUtc = DateTime.MinValue;

        /// <summary>Set while a reconnect is tearing the session down and bringing it back.</summary>
        public int Reconnecting;
        public volatile bool SuppressDisconnect;
    }
}
