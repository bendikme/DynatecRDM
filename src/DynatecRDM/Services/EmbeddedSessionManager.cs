using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Rdp;
using DynatecRDM.Resources;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;

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

    /// <summary>How often the connecting screen's clock moves on.</summary>
    private const int ProgressTickMs = 200;

    /// <summary>
    /// How long, after a timed-out attempt is disconnected, to wait for the control to report it
    /// before acting as if it had.
    /// </summary>
    private const int TimeoutDisconnectGraceMs = 5_000;

    /// <summary>The port an RD Gateway answers on unless its name says otherwise.</summary>
    private const int GatewayPort = 443;

    /// <summary>
    /// The least time an attempt through an RD Gateway gets. A gateway with multi-factor sign-in
    /// holds the connection while the user approves it on their phone - up to a minute - and
    /// nothing on this PC shows that wait, so the clock cannot stop for it.
    /// </summary>
    private const int GatewayMinTimeoutSeconds = 90;

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
                // much later drop gets the full allowance again. Only while it IS up: between
                // attempts ConnectedSinceUtc still holds the login before the drop, and forgiving
                // then would reset the count on every tick - "attempt 1 of 10" for ever, and a
                // server that never answers retried without end.
                if (runtime.Session.ReconnectAttempts > 0
                    && runtime.SessionUp
                    && runtime.Session.State == SessionState.Connected
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

    /// <inheritdoc />
    public string? LastLaunchProblem { get; private set; }

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
        LastLaunchProblem = null;

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

        // A window still showing why the last attempt failed is tried again, rather than a second
        // window opening beside it - when this launch asks for the same thing. One from a
        // multi-config, or with overrides of its own, needs its own placement, sign-in and group, so
        // the old window closes (as the failure it was) and a new one opens.
        if (FindFailed(connection.Id) is { } failed)
        {
            var sameLaunch = multiConfigId is null
                && failed.Session.MultiConfigId is null
                && failed.CredentialOverride == credentialOverride
                && displayOverride is null
                && extraProperties is not { Count: > 0 };
            if (sameLaunch)
            {
                AppLog.Info($"'{connection.Name}' is showing a failed attempt; trying it again.");
                Focus(failed.Session.Id);
                await RetryAsync(failed).ConfigureAwait(false);
                return failed.Session;
            }

            // Its old group mates may be the very sessions this launch is joining: leave them be.
            failed.CloseTogether = false;
            failed.Session.UserInitiatedClose = true;
            Finish(failed, SessionState.Failed, failed.FailureReason);
        }

        // Before any window: without the Remote Desktop control, or the app's bridge to it, there is
        // nothing to show a connecting screen in. The user is told what is missing instead.
        DependencyProblem? missing = null;
        await OnUiAsync(() => missing = DependencyCheck.CheckEmbeddedClient(), ct).ConfigureAwait(false);
        if (missing is not null)
        {
            AppLog.Warn($"'{connection.Name}' was not launched: {missing.Kind}.");
            LastLaunchProblem = missing.Message;
            return null;
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
                runtime.Connection = connection;
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
                // The connecting screen is in place before the window first paints, so it never
                // shows up empty.
                BeginAttempt(runtime, connection);
                window.Show();
                session.WindowHandle = window.Handle;   // a real HWND: snapshots and focus reuse it
                session.State = SessionState.Connecting;
                // Publish before Connect: COM can synchronously report a failure or close the window.
                _runtime[session.Id] = runtime;
                _sessions[session.Id] = session;
                SessionStarted?.Invoke(this, session);
                if (runtime.Closed) return;
                try
                {
                    window.Start(connection, plan, credential, runtime.ExtraProperties, Cfg.SessionBarEnabled);
                    ReportUnsupported(session, window);
                }
                catch (Exception ex)
                {
                    // The window is there: say what went wrong in it, and let the user try again.
                    AppLog.Error($"Could not start the embedded session for '{connection.Name}'.", ex);
                    ShowFailure(runtime, Strings.Connect_Heading_Failed, DescribeStartFailure(ex));
                }
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
            LastLaunchProblem = DependencyCheck.FromException(ex)?.Message;
            Finish(runtime, SessionState.Failed, DescribeStartFailure(ex));
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
        // Closing a window that shows why it failed does not turn the failure into a normal end.
        if (runtime.ShowingFailure) Finish(runtime, SessionState.Failed, runtime.FailureReason);
        else Finish(runtime, SessionState.Disconnected);
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
        runtime.Connection = connection;
        await DoReconnectAsync(runtime, connection, ct).ConfigureAwait(false);
    }

    /// <summary>"Try again" on a failed window: the same window and control, a fresh attempt.</summary>
    private async Task RetryAsync(Runtime runtime)
    {
        if (runtime.Closed) return;

        // The connection may have been edited since - that is often why the user tries again.
        RdpConnection? connection = null;
        try { connection = await _store.GetConnectionAsync(runtime.Session.ConnectionId).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn($"Could not reload '{runtime.Session.DisplayName}' before trying again.", ex); }
        connection ??= runtime.Connection;
        if (connection is null || runtime.Closed) return;

        runtime.Connection = connection;
        runtime.Session.ReconnectAttempts = 0;
        await DoReconnectAsync(runtime, connection, CancellationToken.None).ConfigureAwait(false);
    }

    private Runtime? FindFailed(Guid connectionId)
    {
        foreach (var pair in _runtime)
        {
            var runtime = pair.Value;
            if (runtime.ShowingFailure && !runtime.Closed && runtime.Session.ConnectionId == connectionId) return runtime;
        }
        return null;
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

        window.ReceivedServerKey += (_, _) => runtime.Progress?.OnServerKey();
        window.AuthenticationWarning += (_, shown) =>
        {
            runtime.CertificatePromptUp = shown;
            runtime.Progress?.SetWaitingForUser(ConnectionProgressViewModel.Prompt.Certificate, shown);
        };
        window.Connected += (_, _) =>
        {
            // Connected, not signed in yet: from here on the remote side shows its own progress
            // (the welcome screen, a sign-in prompt), so the session takes over the window.
            // A connection that completes after its attempt was given up - the control had not yet
            // acted on the timeout's Disconnect - is taken as the success it is: nothing may go on
            // saying it failed, or every later drop would be ignored.
            if (runtime.ShowingFailure)
            {
                AppLog.Info($"'{session.DisplayName}' connected after its attempt was given up; keeping the session.");
                runtime.ShowingFailure = false;
                runtime.FailureReason = null;
                RaiseOnUi(() => session.LastError = null);
            }
            runtime.AttemptActive = false;
            runtime.SessionUp = true;
            runtime.Progress?.OnConnected();
            HideProgress(runtime);
            SetState(runtime, SessionState.Connecting);
        };
        window.LoginComplete += (_, _) =>
        {
            // Signed in: a disconnect from here on is not the timeout's, and the reason the last
            // attempt ended is history.
            runtime.TimedOut = false;
            runtime.LastReason = null;
            runtime.ConnectedOnce.TrySetResult(true);
            SetState(runtime, SessionState.Connected);
            runtime.ConnectedSinceUtc = DateTime.UtcNow;

            // The attempt count is NOT cleared here. A server that accepts the logon and then drops
            // the session would otherwise reset the counter on every attempt and reconnect for ever.
            // It is cleared only once a session has survived a while - see InspectSessionsAsync.
            _ = CaptureSoonAsync(runtime);
        };
        // The connection as last started: a Retry or Reconnect reloads it, perhaps edited.
        window.Disconnected += (_, info) => OnDisconnected(runtime, runtime.Connection ?? connection, info);
        window.FormClosed += (_, _) =>
        {
            // The user closed the window itself: treat it as a deliberate close - or, when it was
            // showing why the connection failed, as the failure it was.
            if (runtime.Closed) return;
            session.UserInitiatedClose = true;
            if (runtime.ShowingFailure) Finish(runtime, SessionState.Failed, runtime.FailureReason);
            else Finish(runtime, SessionState.Disconnected);
        };
    }

    private void OnDisconnected(Runtime runtime, RdpConnection connection, RdpDisconnectInfo info)
    {
        var session = runtime.Session;
        AppLog.Info($"RDP disconnected '{session.DisplayName}': reason={info.Code}, kind={info.Kind}, "
            + $"state={session.State}, requestedClose={session.UserInitiatedClose}, timedOut={runtime.TimedOut}, detail={info.Message}");
        runtime.ConnectedOnce.TrySetResult(false);
        if (runtime.Closed || session.UserInitiatedClose) return;

        // A reconnect disconnects on purpose. Without this the deliberate teardown would be read as
        // a dropped link and end the session we are in the middle of bringing back.
        if (runtime.SuppressDisconnect) return;

        // Already given up and saying so: a late word from the control changes nothing.
        if (runtime.ShowingFailure) return;

        // Only a disconnect that ends something counts: the attempt under way, or the session that
        // was up. One the control reports late - after a timed-out attempt was already handled -
        // would otherwise be counted as a second failure.
        if (!runtime.AttemptActive && !runtime.SessionUp) return;
        runtime.SessionUp = false;

        // A timed-out attempt is disconnected by us, which the control reports as a local,
        // deliberate disconnect. It was not: the attempt ran out of time.
        var timedOut = runtime.TimedOut;
        runtime.TimedOut = false;
        runtime.AttemptActive = false;
        runtime.Probe?.Cancel();

        var kind = timedOut ? RdpDisconnectKind.ConnectionLost : info.Kind;
        var reason = timedOut ? DescribeTimeout(runtime, connection) : info.Message;
        var everConnected = runtime.ConnectedSinceUtc != default;

        // A session that was up and was ended cleanly - signed out, or closed from the other end -
        // simply ends, as it always has.
        if (everConnected && kind == RdpDisconnectKind.UserInitiated)
        {
            Finish(runtime, SessionState.Disconnected);
            return;
        }

        // Only a session that has been up is brought back after a drop. A connection that never got
        // that far is not retried behind the user's back: the window says what went wrong, and
        // offers to try again - as the classic Remote Desktop client does.
        var canReconnect = everConnected
            && kind == RdpDisconnectKind.ConnectionLost
            && session.AutoReconnect
            && Cfg.WatchdogEnabled   // the watchdog switch governs this path too, as it does the external one
            && (session.MaxReconnectAttempts <= 0 || session.ReconnectAttempts < session.MaxReconnectAttempts);

        if (!canReconnect)
        {
            // Always carry a reason: a bare "Failed" tells the user nothing.
            var message = reason ?? Strings.Connect_Error_Unknown;
            if (everConnected && kind == RdpDisconnectKind.ConnectionLost && session.MaxReconnectAttempts > 0
                && session.ReconnectAttempts >= session.MaxReconnectAttempts)
            {
                message = UiLanguage.Format(Strings.Connect_Error_GaveUp, session.ReconnectAttempts) + " " + message;
            }
            ShowFailure(runtime, everConnected ? Strings.Connect_Heading_LostFailed : Strings.Connect_Heading_Failed, message);
            return;
        }

        session.ReconnectAttempts++;
        runtime.LastReason = reason;
        SetState(runtime, SessionState.Reconnecting);

        // The countdown runs on the connecting screen; when it is over - or the user chooses to
        // connect now - DoReconnectAsync starts the next attempt.
        var progress = EnsureProgress(runtime, connection);
        progress.BeginCountdown(
            TimeSpan.FromSeconds(Math.Max(1, session.ReconnectDelaySeconds)),
            session.ReconnectAttempts,
            session.MaxReconnectAttempts,
            reason);
        ShowProgress(runtime);
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
            bool stopped;
            try
            {
                stopped = await EnsureDisconnectedAsync(runtime).ConfigureAwait(false);
            }
            finally { runtime.SuppressDisconnect = false; }
            if (runtime.Closed) return;
            if (!stopped)
            {
                OnUi(() => ShowFailure(runtime, Strings.Connect_Heading_Failed, Strings.Connect_Error_StillConnected));
                return;
            }

            await OnUiAsync(() =>
            {
                if (runtime.Closed || runtime.Window is null) return;
                SetState(runtime, SessionState.Connecting);
                BeginAttempt(runtime, connection);
                runtime.Window.Start(connection, plan, credential, runtime.ExtraProperties, Cfg.SessionBarEnabled);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reconnect '{runtime.Session.DisplayName}'.", ex);
            OnUi(() => ShowFailure(runtime, Strings.Connect_Heading_Failed, DescribeStartFailure(ex)));
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
        // Not only "connected": an attempt that timed out can still be connecting, and the control
        // refuses a new target and Connect() until it is all the way down.
        var busy = false;
        OnUi(() => busy = runtime.Window?.Host.ConnectionState is 1 or 2);
        if (!busy) return true;

        OnUi(() => runtime.Window?.Host.Disconnect());

        for (var attempt = 0; attempt < 40; attempt++)   // up to about four seconds
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (runtime.Closed) return false;

            var stillBusy = false;
            OnUi(() => stillBusy = runtime.Window?.Host.ConnectionState is 1 or 2);
            if (!stillBusy) return true;
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
            StopProgress(runtime);
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
        // Every window of the group, a failed one still saying why included - not only the live ones.
        if (!HasOpenWindows(multiConfigId)) return;
        if (!_closingMultis.TryAdd(multiConfigId, 0)) return;

        _ = Task.Run(async () =>
        {
            try { await CloseMultiAsync(multiConfigId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Closing the remaining sessions of a multi-config failed.", ex); }
            finally { _closingMultis.TryRemove(multiConfigId, out _); }
        });
    }

    private bool HasOpenWindows(Guid multiConfigId)
    {
        foreach (var pair in _runtime)
            if (!pair.Value.Closed && pair.Value.Session.MultiConfigId == multiConfigId) return true;
        return false;
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
        // The connecting screen is not what the session looked like; keep the last real picture.
        if (runtime.OverlayVisible) return;
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
                var runtime = pair.Value;
                runtime.Session.UserInitiatedClose = true;
                // A window still saying why it failed ends as that failure, not as a normal close.
                if (runtime.ShowingFailure) Finish(runtime, SessionState.Failed, runtime.FailureReason);
                else Finish(runtime, SessionState.Disconnected);
            }
            catch { }
        }
        _runtime.Clear();
        _sessions.Clear();
    }

    // ---------------------------------------------------------------- the connecting screen

    private int ConnectTimeoutSeconds => Math.Clamp(Cfg.ConnectTimeoutSeconds, 10, 600);

    /// <summary>The time an attempt of this connection gets - more when it may go through a gateway.</summary>
    private int AttemptTimeoutSeconds(Runtime runtime, RdpConnection connection)
    {
        var mayUseGateway = RdpControlConfigurator.OverridesGateway(connection.CustomProperties, runtime.ExtraProperties)
            || RdpControlConfigurator.EffectiveGatewayMethod(connection.Gateway) != GatewayUsageMethod.DoNotUse;
        return mayUseGateway ? Math.Max(ConnectTimeoutSeconds, GatewayMinTimeoutSeconds) : ConnectTimeoutSeconds;
    }

    /// <summary>
    /// Starts an attempt on the connecting screen: the steps reset, the clock starts, the screen
    /// covers the window, and the app's own address and port check runs alongside the control.
    /// Call on the UI thread, just before <see cref="RdpSessionWindow.Start"/>.
    /// </summary>
    private void BeginAttempt(Runtime runtime, RdpConnection connection)
    {
        runtime.AttemptId++;
        runtime.AttemptActive = true;
        runtime.SessionUp = false;
        runtime.TimedOut = false;
        runtime.ShowingFailure = false;
        runtime.FailureReason = null;
        runtime.CertificatePromptUp = false;
        runtime.SignInPromptUp = false;
        runtime.AttemptTimeoutSeconds = AttemptTimeoutSeconds(runtime, connection);
        // Each attempt can be waited for: a multi-config launch that retries a failed window waits
        // for this attempt, not for the one that already failed. One still being waited for stays.
        if (runtime.ConnectedOnce.Task.IsCompleted)
            runtime.ConnectedOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var progress = EnsureProgress(runtime, connection);
        var session = runtime.Session;
        var reconnecting = runtime.ConnectedSinceUtc != default;
        progress.BeginAttempt(
            TimeSpan.FromSeconds(runtime.AttemptTimeoutSeconds),
            reconnecting,
            session.ReconnectAttempts,
            session.MaxReconnectAttempts,
            reconnecting ? runtime.LastReason : null);
        ShowProgress(runtime);
        StartProbe(runtime, connection);
    }

    /// <summary>
    /// The screen for this session, made once - or again when the connection now points somewhere
    /// else, since the steps name the host and port.
    /// </summary>
    private ConnectionProgressViewModel EnsureProgress(Runtime runtime, RdpConnection connection)
    {
        var target = DescribeTarget(connection, runtime.ExtraProperties);
        var probe = ProbeTargetOf(connection, runtime.ExtraProperties);
        var key = $"{target}|{probe}";
        if (runtime.Progress is { } existing && string.Equals(runtime.ProgressKey, key, StringComparison.Ordinal))
            return existing;

        var vm = new ConnectionProgressViewModel(runtime.Session.DisplayName, target, probe);
        vm.TimedOut += (_, _) => OnAttemptTimedOut(runtime);
        vm.CountdownElapsed += (_, _) =>
        {
            if (!runtime.Closed && runtime.Connection is { } current)
                _ = DoReconnectAsync(runtime, current, CancellationToken.None);
        };
        vm.CancelRequested += (_, _) => _ = CloseAsync(runtime.Session.Id);
        vm.RetryRequested += (_, _) => _ = RetryAsync(runtime);
        vm.CloseRequested += (_, _) => _ = CloseAsync(runtime.Session.Id);

        runtime.Progress = vm;
        runtime.ProgressKey = key;
        if (runtime.ProgressView is null) runtime.ProgressView = new ConnectionProgressView();
        runtime.ProgressView.DataContext = vm;

        if (runtime.ProgressTimer is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(ProgressTickMs),
            };
            timer.Tick += (_, _) => OnProgressTick(runtime);
            runtime.ProgressTimer = timer;
        }
        return vm;
    }

    private static void ShowProgress(Runtime runtime)
    {
        if (runtime.Window is not { IsDisposed: false } window || runtime.ProgressView is null) return;
        window.ShowOverlay(runtime.ProgressView);
        runtime.OverlayVisible = true;
        runtime.ProgressTimer?.Start();
    }

    private static void HideProgress(Runtime runtime)
    {
        runtime.ProgressTimer?.Stop();
        runtime.Probe?.Cancel();
        runtime.OverlayVisible = false;
        if (runtime.Window is { IsDisposed: false } window) window.HideOverlay();
    }

    /// <summary>Everything the screen keeps running, stopped for good: the session is over.</summary>
    private static void StopProgress(Runtime runtime)
    {
        runtime.ProgressTimer?.Stop();
        runtime.Probe?.Cancel();
        runtime.OverlayVisible = false;
        if (runtime.ProgressView is { } view) view.DataContext = null;
    }

    /// <summary>
    /// Finding the address and reaching the port, checked by the app itself so the screen can show
    /// them as they happen. Only a report: the control decides whether the connection works.
    /// </summary>
    private void StartProbe(Runtime runtime, RdpConnection connection)
    {
        runtime.Probe?.Cancel();
        if (ProbeTargetOf(connection, runtime.ExtraProperties) is not { } target) return;

        var cts = new CancellationTokenSource();
        runtime.Probe = cts;
        var attempt = runtime.AttemptId;
        var portTimeout = TimeSpan.FromSeconds(Math.Min(10, runtime.AttemptTimeoutSeconds));

        _ = Task.Run(() => ConnectionProbe.RunAsync(target.Host, target.Port, portTimeout, (step, detail) =>
            RaiseOnUi(() =>
            {
                if (runtime.Closed || runtime.AttemptId != attempt || !runtime.AttemptActive) return;
                runtime.Progress?.OnProbe(step, detail);
            }), cts.Token));
    }

    private void OnProgressTick(Runtime runtime)
    {
        if (runtime.Closed || runtime.Progress is not { } progress)
        {
            runtime.ProgressTimer?.Stop();
            return;
        }

        // The control's sign-in prompt has no event of its own; while it is up the control keeps
        // the window disabled, which is what is looked for here. The clock stops meanwhile, so
        // typing a password never runs an attempt out of time.
        // One of the app's own dialogs disables every window on the thread as well, this one
        // included, so while one is open a disabled window says nothing about the control: what
        // was seen before it opened stands. (Only WPF's ShowDialog sets IsThreadModal - the
        // control's own prompts run a native modal loop that never touches it.)
        if (runtime.AttemptActive && runtime.Window is { IsDisposed: false } window
            && !System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            var signIn = window.IsWaitingForUser && !runtime.CertificatePromptUp;
            if (signIn != runtime.SignInPromptUp)
            {
                runtime.SignInPromptUp = signIn;
                progress.SetWaitingForUser(ConnectionProgressViewModel.Prompt.SignIn, signIn);
            }
        }

        progress.Tick();
    }

    /// <summary>
    /// The attempt used up its time. The control is told to stop, and its disconnect - reported as a
    /// local one - is read as the timeout it is. Should the control not report back, the timeout is
    /// acted on anyway, so the screen can never wait for ever.
    /// </summary>
    private void OnAttemptTimedOut(Runtime runtime)
    {
        if (runtime.Closed || !runtime.AttemptActive || runtime.Connection is not { } connection) return;

        runtime.TimedOut = true;
        var attempt = runtime.AttemptId;
        AppLog.Info($"'{runtime.Session.DisplayName}' did not connect within {runtime.AttemptTimeoutSeconds} s; giving the attempt up.");

        try { runtime.Window?.Host.Disconnect(); }
        catch (Exception ex) { AppLog.Debug_($"Stopping the timed-out attempt failed: {ex.Message}"); }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeoutDisconnectGraceMs).ConfigureAwait(false);
            OnUi(() =>
            {
                if (runtime.Closed || runtime.AttemptId != attempt || !runtime.AttemptActive || !runtime.TimedOut) return;
                AppLog.Warn($"'{runtime.Session.DisplayName}': the control did not report the timed-out attempt ending.");
                OnDisconnected(runtime, connection, new RdpDisconnectInfo(RdpDisconnectKind.ConnectionLost, 0, null));
            });
        });
    }

    /// <summary>
    /// The connection is given up. The window stays, saying why and offering to try again, until
    /// the user closes it - a failure that closes its own window leaves nothing to read.
    /// </summary>
    private void ShowFailure(Runtime runtime, string heading, string message)
    {
        if (runtime.Closed) return;

        runtime.ShowingFailure = true;
        runtime.FailureReason = message;
        // Shown as "last attempt" when the user tries again.
        runtime.LastReason = message;
        runtime.AttemptActive = false;
        runtime.SessionUp = false;
        runtime.TimedOut = false;
        runtime.Probe?.Cancel();
        // Nothing is on its way: a multi-config launch waiting for this one moves on.
        runtime.ConnectedOnce.TrySetResult(false);

        var session = runtime.Session;
        AppLog.Warn($"'{session.DisplayName}' could not be connected: {message}");

        if (runtime.Connection is { } connection) EnsureProgress(runtime, connection);
        runtime.Progress?.Fail(heading, message);
        ShowProgress(runtime);
        runtime.ProgressTimer?.Stop();

        RaiseOnUi(() => session.LastError = message);
        SetState(runtime, SessionState.Failed);
    }

    /// <summary>
    /// What the address and port steps check: the host itself, or the gateway a connection always
    /// goes through - as the control is told, so both check the same place. Null when that cannot be
    /// known in advance - a gateway used only for some networks, or one set by raw .rdp lines - and
    /// the screen then shows one "connecting" step instead of the two.
    /// </summary>
    private static (string Host, int Port, bool Gateway)? ProbeTargetOf(
        RdpConnection connection, IReadOnlyDictionary<string, string>? extraProperties)
    {
        if (RdpControlConfigurator.OverridesGateway(connection.CustomProperties, extraProperties)) return null;

        var gateway = connection.Gateway;
        switch (RdpControlConfigurator.EffectiveGatewayMethod(gateway))
        {
            case GatewayUsageMethod.DoNotUse:
            {
                // A port typed into the host field wins over the Port setting, as it does for the control.
                RdpControlConfigurator.SplitHostPort(connection.Host, connection.Port, out var host, out var port);
                return host.Length == 0 ? null : (host, port, false);
            }

            case GatewayUsageMethod.AlwaysUse:
            {
                // A gateway answers on HTTPS unless its name carries a port of its own.
                var name = gateway.HostName!.Trim();
                var (gatewayHost, embedded) = RdpConnectivity.Normalize(name, 0);
                var hasPort = !name.EndsWith(']') && !string.Equals(gatewayHost, name, StringComparison.OrdinalIgnoreCase);
                return gatewayHost.Length == 0 ? null : (gatewayHost, hasPort ? embedded : GatewayPort, true);
            }

            default:
                // Through the gateway only away from the local network (which is also what "always"
                // with the local bypass becomes), or as Windows decides.
                return null;
        }
    }

    private static string DescribeTarget(RdpConnection connection, IReadOnlyDictionary<string, string>? extraProperties) =>
        ProbeTargetOf(connection, extraProperties) is { Gateway: true }
            ? UiLanguage.Format(Strings.Connect_ViaGateway, connection.FullAddress, connection.Gateway.HostName!.Trim())
            : connection.FullAddress;

    /// <summary>
    /// Why an attempt ran out of time. "No answer" only when nothing answered: once the port or the
    /// server itself did, the attempt stalled later on - often on a sign-in approved elsewhere.
    /// </summary>
    private static string DescribeTimeout(Runtime runtime, RdpConnection connection)
    {
        var progress = runtime.Progress;
        var serverAnswered = progress?.ServerAnswered == true;
        // Until the remote computer itself answers, the gateway is the one being waited for.
        var who = ProbeTargetOf(connection, runtime.ExtraProperties) is { Gateway: true } gateway && !serverAnswered
            ? gateway.Host
            : connection.FullAddress;
        var text = progress?.Answered == true ? Strings.Connect_TimedOut_Stalled : Strings.Connect_TimedOut;
        return UiLanguage.Format(text, who, runtime.AttemptTimeoutSeconds);
    }

    /// <summary>
    /// What to tell the user when a session could not even be started. A missing Remote Desktop
    /// component is something they can do something about, so it says what to install or do.
    /// </summary>
    internal static string DescribeStartFailure(Exception ex) =>
        DependencyCheck.FromException(ex)?.Message ?? ex.Message;

    /// <summary>Per-session bookkeeping the bindable session model must not carry.</summary>
    private sealed class Runtime(RdpSession session)
    {
        public readonly RdpSession Session = session;
        /// <summary>The current attempt's outcome; replaced when a new attempt starts after it is known.</summary>
        public volatile TaskCompletionSource<bool> ConnectedOnce =
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

        /// <summary>The connection as last launched, for trying again.</summary>
        public RdpConnection? Connection;

        // The connecting screen. Everything below is touched on the UI thread only, apart from
        // the two volatile flags, which the maintenance timer and CloseAsync read.
        public ConnectionProgressViewModel? Progress;
        public ConnectionProgressView? ProgressView;
        public DispatcherTimer? ProgressTimer;
        public CancellationTokenSource? Probe;

        /// <summary>Counts attempts, so a late timer or probe result from an old one is ignored.</summary>
        public int AttemptId;

        /// <summary>Between Connect() and the control's answer - connected, or disconnected.</summary>
        public bool AttemptActive;

        /// <summary>
        /// Between the control reporting connected and the disconnect that ends it. With
        /// <see cref="AttemptActive"/> this tells a disconnect that matters from a late duplicate.
        /// Also read by the maintenance timer, hence volatile.
        /// </summary>
        public volatile bool SessionUp;

        /// <summary>The address and checks the screen was made for; different ones get a new screen.</summary>
        public string? ProgressKey;

        /// <summary>The time the current attempt gets, in seconds.</summary>
        public int AttemptTimeoutSeconds;

        /// <summary>This attempt was given up for taking too long; its disconnect is ours.</summary>
        public bool TimedOut;

        public bool CertificatePromptUp;
        public bool SignInPromptUp;

        /// <summary>Why the last attempt ended, shown with the next one.</summary>
        public string? LastReason;

        public volatile bool ShowingFailure;
        public string? FailureReason;
        public volatile bool OverlayVisible;
    }
}
