using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Owns every running mstsc process: launching, window discovery, placement, snapshots and the
/// reconnect watchdog. A single timer services all sessions, and nothing here touches the disk or
/// the credential vault from the UI thread.
/// </summary>
public sealed class SessionManager : ISessionManager, IDisposable
{
    private const int WindowPollMs = 100;
    private const int WindowWaitMs = 30_000;
    private const int MultiWindowWaitMs = 6_000;
    private const int FirstSnapshotDelayMs = 1_500;
    private const int CloseGraceMs = 3_000;
    private const int KillGraceMs = 2_000;
    private const int SurvivalResetSeconds = 60;
    private const int SnapshotParallelism = 4;
    private const int OrphanSweepSeconds = 120;

    /// <summary>
    /// Reserved key used to carry a multi-config label through <see cref="LaunchAsync"/>, which has no
    /// parameter for it. It is stripped before the properties reach the .rdp writer.
    /// </summary>
    private const string DisplayNameKey = "dynatec:displayname";

    private static readonly string MstscPath = ResolveMstsc();

    private static readonly string[] RdpWindowClasses =
    {
        "TscShellContainerClass",
        "TSSHELLWND",
        "RAIL_WINDOW",
    };

    private readonly IDataStore _store;
    private readonly IRdpFileBuilder _builder;
    private readonly ISecretProtector _protector;
    private readonly IWindowsCredentialService _credentials;
    private readonly IMonitorService _monitors;
    private readonly IWindowPlacementService _placement;
    private readonly ISnapshotService _snapshots;
    private readonly Func<AppSettings> _settings;
    private readonly AppSettings _fallbackSettings = new();

    private readonly ConcurrentDictionary<Guid, RdpSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, SessionRuntime> _runtime = new();
    private readonly ConcurrentDictionary<Guid, byte> _closingMultis = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _watchdog;

    private DateTime _lastOrphanSweepUtc = DateTime.UtcNow;
    private int _watchdogArmed;
    private volatile bool _disposed;

    public SessionManager(
        IDataStore store,
        IRdpFileBuilder builder,
        ISecretProtector protector,
        IWindowsCredentialService credentials,
        IMonitorService monitors,
        IWindowPlacementService placement,
        ISnapshotService snapshots,
        Func<AppSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);

        _store = store;
        _builder = builder;
        _protector = protector;
        _credentials = credentials;
        _monitors = monitors;
        _placement = placement;
        _snapshots = snapshots;
        _settings = settings;

        _watchdog = new System.Threading.Timer(OnWatchdogTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        get
        {
            try { return _settings() ?? _fallbackSettings; }
            catch { return _fallbackSettings; }
        }
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

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            AppLog.Error($"'{connection.Name}' has no host and cannot be launched.");
            return null;
        }

        var display = (displayOverride ?? connection.Display).Clone();

        string? nameOverride = null;
        if (extraProperties is { Count: > 0 } && extraProperties.TryGetValue(DisplayNameKey, out var label))
        {
            nameOverride = label;
            extraProperties = new Dictionary<string, string>(extraProperties, StringComparer.OrdinalIgnoreCase);
            extraProperties.Remove(DisplayNameKey);
            if (extraProperties.Count == 0) extraProperties = null;
        }

        Process process;
        string rdpPath;
        try
        {
            (process, rdpPath) = await StartMstscAsync(connection, display, credentialOverride, extraProperties, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not start Remote Desktop for '{connection.Name}'.", ex);
            return null;
        }

        var session = new RdpSession
        {
            ConnectionId = connection.Id,
            MultiConfigId = multiConfigId,
            DisplayName = nameOverride ?? connection.Name,
            Host = connection.Host,
            Display = display,
            RdpFilePath = rdpPath,
            ProcessId = process.Id,
            State = SessionState.Launching,
            AutoReconnect = connection.AutoReconnect,
            MaxReconnectAttempts = connection.MaxReconnectAttempts,
            ReconnectDelaySeconds = connection.ReconnectDelaySeconds,
            StartedUtc = DateTime.UtcNow,
        };

        var runtime = new SessionRuntime(session)
        {
            Process = process,
            RdpPath = rdpPath,
            CredentialOverride = credentialOverride,
            ExtraProperties = extraProperties,
        };

        _runtime[session.Id] = runtime;
        _sessions[session.Id] = session;
        ArmWatchdog();

        RaiseOnUi(() => SessionStarted?.Invoke(this, session));
        AppLog.Info($"Launched '{connection.Name}' ({connection.FullAddress}) as pid {process.Id}.");

        _ = Task.Run(() => AttachWindowAsync(runtime, connection.Id));
        return session;
    }

    public async Task<IReadOnlyList<RdpSession>> LaunchMultiAsync(MultiConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var started = new List<RdpSession>(config.Items.Count);
        if (config.Items.Count == 0) return started;

        if (config.InitialDelayMs > 0)
            await Task.Delay(config.InitialDelayMs, ct).ConfigureAwait(false);

        var items = new List<MultiConfigItem>(config.Items.Count);
        foreach (var item in config.Items)
            if (item.Enabled) items.Add(item);
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

            // The label has to be on the session before it is published, so it travels with the launch.
            if (!string.IsNullOrWhiteSpace(item.DisplayNameOverride))
            {
                extra ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                extra[DisplayNameKey] = item.DisplayNameOverride!;
            }

            var session = await LaunchAsync(connection, display, item.CredentialSetIdOverride, config.Id, extra, ct)
                .ConfigureAwait(false);
            if (session is null) continue;

            if (item.AutoReconnectOverride.HasValue)
                session.AutoReconnect = item.AutoReconnectOverride.Value;

            started.Add(session);

            if (!_runtime.TryGetValue(session.Id, out var runtime)) continue;
            runtime.CloseTogether = config.CloseTogether;

            if (config.Sequential)
            {
                await WaitForWindowAsync(runtime, MultiWindowWaitMs, ct).ConfigureAwait(false);
                if (item.DelayMs > 0)
                    await Task.Delay(item.DelayMs, ct).ConfigureAwait(false);
            }
            else
            {
                parallelWaits!.Add(WaitForWindowAsync(runtime, MultiWindowWaitMs, ct));
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

    public async Task CloseAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_runtime.TryGetValue(sessionId, out var runtime)) return;

        // Stand the watchdog down before anything touches the process.
        runtime.Session.UserInitiatedClose = true;
        runtime.Closed = true;

        await TerminateAsync(runtime, ct).ConfigureAwait(false);
        Finish(runtime, SessionState.Disconnected);
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

        var session = runtime.Session;
        var connection = await _store.GetConnectionAsync(session.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null)
        {
            AppLog.Warn($"Cannot reconnect '{session.DisplayName}': the connection was deleted.");
            return;
        }

        // A replacement session carries the label and the per-item overrides of the one it replaces.
        var extras = runtime.ExtraProperties;
        if (!string.Equals(session.DisplayName, connection.Name, StringComparison.Ordinal))
        {
            extras = extras is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(extras, StringComparer.OrdinalIgnoreCase);
            extras[DisplayNameKey] = session.DisplayName;
        }

        var autoReconnect = session.AutoReconnect;
        var closeTogether = runtime.CloseTogether;

        // Not a user close - the session comes straight back - but the watchdog must not race us,
        // and ending it must not take the rest of its multi-config down with it.
        runtime.CloseTogether = false;
        runtime.Closed = true;
        await TerminateAsync(runtime, ct).ConfigureAwait(false);
        Finish(runtime, SessionState.Disconnected);

        var replacement = await LaunchAsync(connection, session.Display, runtime.CredentialOverride,
            session.MultiConfigId, extras, ct).ConfigureAwait(false);
        if (replacement is null) return;

        replacement.AutoReconnect = autoReconnect;
        if (closeTogether && _runtime.TryGetValue(replacement.Id, out var next)) next.CloseTogether = true;
    }

    public void Focus(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        try { _placement.FocusWindow(hwnd); }
        catch (Exception ex) { AppLog.Warn($"Could not focus '{session.DisplayName}'.", ex); }
    }

    public RdpSession? FindByConnection(Guid connectionId)
    {
        RdpSession? best = null;
        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (session.ConnectionId != connectionId || !session.IsActive) continue;
            if (best is null || session.StartedUtc < best.StartedUtc) best = session;
        }
        return best;
    }

    public bool IsMultiConfigRunning(Guid multiConfigId)
    {
        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (session.MultiConfigId == multiConfigId && session.IsActive) return true;
        }
        return false;
    }

    public async Task RefreshSnapshotsAsync(CancellationToken ct = default)
    {
        if (!Cfg.EnableSnapshots) return;

        List<SessionRuntime>? targets = null;
        foreach (var pair in _runtime)
        {
            var runtime = pair.Value;
            if (runtime.Closed || !IsSnapshotCandidate(runtime)) continue;
            (targets ??= new List<SessionRuntime>()).Add(runtime);
        }
        if (targets is null) return;

        // WhenAll completes only once every task is done, so the gate outlives its last Release.
        using var gate = new SemaphoreSlim(SnapshotParallelism, SnapshotParallelism);
        var tasks = new Task[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            tasks[i] = CaptureGatedAsync(gate, targets[i], ct);

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Snapshot refresh failed.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        try { _watchdog.Dispose(); } catch { }

        // Cancelled but not disposed: attach and reconnect tasks still read this token, and a disposed
        // source turns every one of those reads into an ObjectDisposedException on a background thread.
        try { _cts.Cancel(); } catch { }

        // Live sessions deliberately survive: closing the manager must not drop the user's desktops.
    }

    private async Task<(Process Process, string RdpPath)> StartMstscAsync(
        RdpConnection connection,
        DisplaySettings display,
        Guid? credentialOverride,
        Dictionary<string, string>? extraProperties,
        CancellationToken ct)
    {
        var credentialId = credentialOverride ?? connection.CredentialSetId;

        CredentialSet? credential = null;
        if (credentialId.HasValue)
        {
            credential = await _store.GetCredentialSetAsync(credentialId.Value, ct).ConfigureAwait(false);
            if (credential is null)
                AppLog.Warn($"Credential set {credentialId.Value} used by '{connection.Name}' no longer exists.");
        }

        string? password = null;
        if (credential is { ProtectedPassword.Length: > 0 })
        {
            try { password = _protector.Unprotect(credential.ProtectedPassword); }
            catch (Exception ex) { AppLog.Warn($"Could not decrypt the password of '{credential.Name}'.", ex); }
        }

        var delivery = connection.CredentialDelivery;
        var wantsVault = delivery is CredentialDelivery.WindowsVault or CredentialDelivery.Both;
        var wantsEmbedded = delivery is CredentialDelivery.EmbeddedInRdpFile or CredentialDelivery.Both;
        var hasPassword = !string.IsNullOrEmpty(password);

        if (wantsVault && credential is not null && hasPassword)
        {
            var host = connection.Host;
            var user = credential.QualifiedUsername;
            var secret = password!;
            var method = Cfg.VaultWriteMethod;

            // mstsc reads the vault as it starts, so the entry has to exist before the process does.
            await Task.Run(() =>
            {
                try
                {
                    if (!_credentials.SaveCredential(host, user, secret, method))
                        AppLog.Warn($"The vault entry TERMSRV/{host} was not written.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Writing the vault entry TERMSRV/{host} failed.", ex);
                }
            }, ct).ConfigureAwait(false);
        }

        var context = new RdpBuildContext
        {
            Display = display,
            Credential = credential,
            PlainPassword = wantsEmbedded && hasPassword ? password : null,
            EmbedPassword = wantsEmbedded && hasPassword,
            ExtraProperties = extraProperties,
            Monitors = _monitors.GetMonitors(),
        };

        var rdpPath = await Task.Run(() => _builder.WriteToTempFile(connection, context), ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(MstscPath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.SystemDirectory,
        };
        startInfo.ArgumentList.Add(rdpPath);
        if (connection.Security.AdministrativeSession) startInfo.ArgumentList.Add("/admin");
        if (connection.Security.PublicMode) startInfo.ArgumentList.Add("/public");

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start mstsc.exe.");
        }
        catch
        {
            // Nothing will ever own this file, and it can hold an encrypted password.
            if (Cfg.ShredRdpFiles) _ = Task.Run(() => ShredFile(rdpPath));
            throw;
        }

        return (process, rdpPath);
    }

    private async Task AttachWindowAsync(SessionRuntime runtime, Guid connectionId)
    {
        var session = runtime.Session;
        var token = _cts.Token;

        // Held for the whole discovery phase: session state is published asynchronously, so the
        // watchdog cannot use it to tell "starting up" from "dropped".
        Interlocked.Exchange(ref runtime.Attaching, 1);

        try
        {
            SetState(runtime, SessionState.Connecting);

            var hwnd = IntPtr.Zero;
            var deadline = Environment.TickCount64 + WindowWaitMs;
            var exited = false;

            while (Environment.TickCount64 < deadline && !token.IsCancellationRequested && !runtime.Closed)
            {
                var process = runtime.Process;
                if (process is null) break;

                // The process object, never session.ProcessId: that one is published to the UI
                // thread asynchronously and still holds the previous pid during a reconnect.
                hwnd = FindSessionWindow(PidOf(process));
                if (hwnd != IntPtr.Zero) break;

                exited = HasExited(process);
                if (exited) break;

                await Task.Delay(WindowPollMs, token).ConfigureAwait(false);
            }

            if (runtime.Closed || token.IsCancellationRequested) return;

            if (hwnd == IntPtr.Zero && !exited)
            {
                // mstsc builds its UI asynchronously; MainWindowHandle is the last resort.
                var process = runtime.Process;
                if (process is not null && !HasExited(process))
                {
                    try
                    {
                        process.Refresh();
                        var fallback = process.MainWindowHandle;
                        if (fallback != IntPtr.Zero && Win32.IsWindow(fallback)) hwnd = fallback;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug_($"MainWindowHandle unavailable for '{session.DisplayName}': {ex.Message}");
                    }
                }
            }

            if (hwnd == IntPtr.Zero)
            {
                var reason = exited
                    ? "Remote Desktop Connection closed before a session window appeared."
                    : "Timed out waiting for the Remote Desktop session window.";
                AppLog.Warn($"'{session.DisplayName}': {reason}");

                var config = Cfg;
                var canRetry = !session.UserInitiatedClose
                    && session.AutoReconnect
                    && config.WatchdogEnabled
                    && (session.MaxReconnectAttempts <= 0
                        || session.ReconnectAttempts < session.MaxReconnectAttempts);

                if (!canRetry)
                {
                    var exhausted = session.AutoReconnect
                        && config.WatchdogEnabled
                        && !session.UserInitiatedClose
                        && session.ReconnectAttempts > 0;

                    if (exhausted)
                    {
                        var givenUp = $"Gave up reconnecting after {session.ReconnectAttempts} attempts. {reason}";
                        AppLog.Warn($"'{session.DisplayName}': {givenUp}");
                        Finish(runtime, SessionState.Failed, givenUp);
                    }
                    else
                    {
                        Finish(runtime, SessionState.Failed, reason);
                    }

                    return;
                }

                // A session that never came up is still worth retrying when the user asked for a
                // watchdog: the far end is usually a server that is still booting. Leaving it in
                // Reconnecting hands it to the watchdog, which applies the same attempt limit as a
                // dropped session and kills the windowless mstsc first. The retry is not started
                // here because this method's finally clears Attaching, which would race it.
                RaiseOnUi(() => session.LastError = reason);
                SetState(runtime, SessionState.Reconnecting);
                return;
            }

            var window = hwnd;
            RaiseOnUi(() =>
            {
                session.WindowHandle = window;
                session.LastError = null;
            });

            runtime.LastAliveStampUtc = DateTime.UtcNow;
            SetState(runtime, SessionState.Connected);
            runtime.WindowReady.TrySetResult(true);
            TryShredRdpFile(runtime);

            try { await _placement.ApplyAsync(window, session.Display, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn($"Placement of '{session.DisplayName}' failed.", ex); }

            // The window exists and has been placed: the watchdog owns the session from here.
            Interlocked.Exchange(ref runtime.Attaching, 0);

            try { await _store.RecordLaunchAsync(connectionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn($"Could not record the launch of '{session.DisplayName}'.", ex); }

            // Let the remote desktop paint before the first thumbnail is taken.
            await Task.Delay(FirstSnapshotDelayMs, token).ConfigureAwait(false);
            await CaptureSnapshotAsync(runtime, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Attaching to the window of '{session.DisplayName}' failed.", ex);
            Finish(runtime, SessionState.Failed, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref runtime.Attaching, 0);
            runtime.WindowReady.TrySetResult(session.WindowHandle != IntPtr.Zero);
            TryShredRdpFile(runtime);
        }
    }

    private static bool IsRdpWindowClass(string className)
    {
        for (var i = 0; i < RdpWindowClasses.Length; i++)
            if (string.Equals(className, RdpWindowClasses[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IntPtr FindSessionWindow(int processId)
    {
        if (processId <= 0) return IntPtr.Zero;

        var owner = (uint)processId;
        var best = IntPtr.Zero;
        long bestArea = 0;

        Win32.EnumWindows(hwnd =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != owner) return true;
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsWindowCloaked(hwnd)) return true;
            if (!IsRdpWindowClass(Win32.GetClassName(hwnd))) return true;
            if (!Win32.GetWindowRect(hwnd, out var rect)) return true;

            var area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return true;
        });

        return best;
    }

    private static async Task WaitForWindowAsync(SessionRuntime runtime, int capMs, CancellationToken ct)
    {
        var ready = runtime.WindowReady.Task;
        if (ready.IsCompleted) return;

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(capMs, cap.Token);
        var winner = await Task.WhenAny(ready, timeout).ConfigureAwait(false);
        if (!ReferenceEquals(winner, timeout))
        {
            try { cap.Cancel(); } catch { }
        }
    }

    private void OnWatchdogTick(object? state)
    {
        Interlocked.Exchange(ref _watchdogArmed, 0);
        if (_disposed) return;
        _ = RunWatchdogTickAsync();
    }

    private async Task RunWatchdogTickAsync()
    {
        try
        {
            await InspectSessionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("The reconnect watchdog threw.", ex);
        }
        finally
        {
            ArmWatchdog();
        }
    }

    private void ArmWatchdog()
    {
        if (_disposed || _runtime.IsEmpty) return;
        if (Interlocked.CompareExchange(ref _watchdogArmed, 1, 0) != 0) return;

        var seconds = Math.Clamp(Cfg.WatchdogPollSeconds, 1, 300);
        try
        {
            _watchdog.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _watchdogArmed, 0);
        }
    }

    private Task InspectSessionsAsync()
    {
        var config = Cfg;
        var now = DateTime.UtcNow;
        var snapshotsWanted = config.EnableSnapshots && config.SnapshotIntervalSeconds > 0;

        foreach (var pair in _runtime)
        {
            var runtime = pair.Value;
            var session = runtime.Session;

            // The launch task owns a session until it has produced and placed a window.
            if (runtime.Closed || runtime.Restarting != 0 || runtime.Attaching != 0) continue;
            if (!session.IsActive) continue;
            if (session.State is SessionState.Launching or SessionState.Connecting) continue;

            if (IsAlive(runtime))
            {
                if (session.ReconnectAttempts > 0 &&
                    (now - runtime.LastAliveStampUtc).TotalSeconds >= SurvivalResetSeconds)
                {
                    RaiseOnUi(() => session.ReconnectAttempts = 0);
                }

                if (snapshotsWanted &&
                    (now - runtime.LastSnapshotAttemptUtc).TotalSeconds >= config.SnapshotIntervalSeconds &&
                    IsSnapshotCandidate(runtime))
                {
                    _ = CaptureSnapshotAsync(runtime, _cts.Token);
                }

                continue;
            }

            HandleDeadSession(runtime, config);
        }

        if ((now - _lastOrphanSweepUtc).TotalSeconds >= OrphanSweepSeconds)
        {
            _lastOrphanSweepUtc = now;
            SweepOrphanSnapshots();
        }

        return Task.CompletedTask;
    }

    /// <summary>Drops snapshot files left behind by sessions that no longer exist.</summary>
    private void SweepOrphanSnapshots()
    {
        var live = new List<Guid>(_sessions.Count);
        foreach (var pair in _sessions) live.Add(pair.Key);

        _ = Task.Run(() =>
        {
            try { _snapshots.CleanupOrphans(live); }
            catch (Exception ex) { AppLog.Warn("Sweeping orphaned snapshots failed.", ex); }
        });
    }

    private bool IsAlive(SessionRuntime runtime)
    {
        var process = runtime.Process;
        if (process is null || HasExited(process)) return false;

        var pid = PidOf(process);
        var session = runtime.Session;

        // Ownership as well as existence: window handles are recycled, and a stale one that now
        // belongs to someone else would keep a dead session looking alive forever.
        var hwnd = session.WindowHandle;
        if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd) && OwnedBy(hwnd, pid)) return true;

        // mstsc can rebuild its shell window during its own internal reconnect.
        var found = FindSessionWindow(pid);
        if (found == IntPtr.Zero) return false;

        RaiseOnUi(() => session.WindowHandle = found);
        return true;
    }

    private static bool OwnedBy(IntPtr hwnd, int processId)
    {
        if (processId <= 0) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var owner);
        return owner == (uint)processId;
    }

    private void HandleDeadSession(SessionRuntime runtime, AppSettings config)
    {
        var session = runtime.Session;

        if (session.UserInitiatedClose || !session.AutoReconnect || !config.WatchdogEnabled)
        {
            AppLog.Info($"'{session.DisplayName}' ended.");
            Finish(runtime, SessionState.Disconnected);
            return;
        }

        if (session.MaxReconnectAttempts > 0 && session.ReconnectAttempts >= session.MaxReconnectAttempts)
        {
            var reason = $"Gave up reconnecting after {session.ReconnectAttempts} attempts.";
            AppLog.Warn($"'{session.DisplayName}': {reason}");
            Finish(runtime, SessionState.Failed, reason);
            return;
        }

        if (Interlocked.Exchange(ref runtime.Restarting, 1) == 1) return;
        _ = Task.Run(() => ReconnectLoopAsync(runtime));
    }

    private async Task ReconnectLoopAsync(SessionRuntime runtime)
    {
        var session = runtime.Session;
        var token = _cts.Token;
        var handedOff = false;

        try
        {
            RaiseOnUi(() => session.ReconnectAttempts++);
            SetState(runtime, SessionState.Reconnecting);

            // A windowless mstsc still holding the session would fight the replacement for it.
            await KillProcessAsync(runtime).ConfigureAwait(false);

            var delay = Math.Clamp(session.ReconnectDelaySeconds, 1, 3600);
            await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            if (runtime.Closed || token.IsCancellationRequested) return;

            var connection = await _store.GetConnectionAsync(session.ConnectionId, token).ConfigureAwait(false);
            if (connection is null)
            {
                Finish(runtime, SessionState.Failed, "The connection was deleted.");
                return;
            }

            var (process, rdpPath) = await StartMstscAsync(connection, session.Display, runtime.CredentialOverride,
                runtime.ExtraProperties, token).ConfigureAwait(false);

            runtime.Process = process;
            runtime.RdpPath = rdpPath;

            if (runtime.Closed || token.IsCancellationRequested)
            {
                // Closed while we were starting: the replacement must not outlive the session.
                await KillProcessAsync(runtime).ConfigureAwait(false);
                TryShredRdpFile(runtime);
                return;
            }

            // Read once: by the time the UI thread runs this, the process object may be gone.
            var pid = PidOf(process);
            RaiseOnUi(() =>
            {
                session.ProcessId = pid;
                session.RdpFilePath = rdpPath;
                session.WindowHandle = IntPtr.Zero;
            });

            AppLog.Info($"Reconnecting '{session.DisplayName}' (attempt {session.ReconnectAttempts}) as pid {pid}.");

            // Hand the session to the normal attach path before clearing the guard, so the watchdog
            // cannot decide it is dead while its new window is still appearing.
            Interlocked.Exchange(ref runtime.Attaching, 1);
            handedOff = true;
            Interlocked.Exchange(ref runtime.Restarting, 0);
            await AttachWindowAsync(runtime, connection.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Reconnecting '{session.DisplayName}' failed.", ex);
            RaiseOnUi(() => session.LastError = ex.Message);
        }
        finally
        {
            if (!handedOff) Interlocked.Exchange(ref runtime.Restarting, 0);
        }
    }

    private async Task TerminateAsync(SessionRuntime runtime, CancellationToken ct)
    {
        var hwnd = runtime.Session.WindowHandle;
        if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd))
        {
            try { Win32.PostCloseMessage(hwnd); }
            catch (Exception ex) { AppLog.Debug_($"WM_CLOSE failed: {ex.Message}"); }
        }

        var process = runtime.Process;
        if (process is null) return;

        try
        {
            using (var grace = new CancellationTokenSource(CloseGraceMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(grace.Token, ct))
            {
                try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromMilliseconds(KillGraceMs)).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not close '{runtime.Session.DisplayName}' cleanly.", ex);
        }
    }

    private void Finish(SessionRuntime runtime, SessionState state, string? error = null)
    {
        if (Interlocked.Exchange(ref runtime.Finished, 1) == 1) return;

        var session = runtime.Session;
        runtime.Closed = true;

        _sessions.TryRemove(session.Id, out _);
        _runtime.TryRemove(session.Id, out _);

        session.EndedUtc = DateTime.UtcNow;
        TryShredRdpFile(runtime);
        DisposeProcess(runtime);

        _ = Task.Run(() =>
        {
            try { _snapshots.Remove(session); }
            catch (Exception ex) { AppLog.Warn($"Could not remove the snapshot of '{session.DisplayName}'.", ex); }
        });

        runtime.WindowReady.TrySetResult(false);

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

    /// <summary>Takes the rest of a multi-config down once one of its sessions has ended.</summary>
    private void CloseMultiConfigSiblings(Guid multiConfigId)
    {
        if (!IsMultiConfigRunning(multiConfigId)) return;

        // The siblings end while we are closing them; the guard keeps that from recursing.
        if (!_closingMultis.TryAdd(multiConfigId, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await CloseMultiAsync(multiConfigId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Closing the remaining sessions of a multi-config failed.", ex);
            }
            finally
            {
                _closingMultis.TryRemove(multiConfigId, out _);
            }
        });
    }

    private void SetState(SessionRuntime runtime, SessionState state)
    {
        if (runtime.Closed) return;

        var session = runtime.Session;
        if (session.State == state) return;

        RaiseOnUi(() =>
        {
            if (session.State == state) return;
            session.State = state;
            SessionStateChanged?.Invoke(this, session);
        });
    }

    private static bool IsSnapshotCandidate(SessionRuntime runtime)
    {
        var session = runtime.Session;
        if (!session.IsActive) return false;

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero) return false;

        return Win32.IsWindow(hwnd) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd);
    }

    private async Task CaptureGatedAsync(SemaphoreSlim gate, SessionRuntime runtime, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await CaptureSnapshotAsync(runtime, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task CaptureSnapshotAsync(SessionRuntime runtime, CancellationToken ct)
    {
        if (!Cfg.EnableSnapshots) return;
        if (!IsSnapshotCandidate(runtime)) return;
        if (Interlocked.Exchange(ref runtime.SnapshotBusy, 1) == 1) return;

        var session = runtime.Session;
        runtime.LastSnapshotAttemptUtc = DateTime.UtcNow;

        try
        {
            var path = await _snapshots.CaptureAsync(session, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(path) && !runtime.Closed)
            {
                var captured = DateTime.UtcNow;
                RaiseOnUi(() =>
                {
                    session.SnapshotPath = path;
                    session.LastSnapshotUtc = captured;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Snapshot of '{session.DisplayName}' failed.", ex);
        }
        finally
        {
            runtime.LastSnapshotAttemptUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref runtime.SnapshotBusy, 0);
        }
    }

    private void TryShredRdpFile(SessionRuntime runtime)
    {
        if (!Cfg.ShredRdpFiles) return;

        var path = Interlocked.Exchange(ref runtime.RdpPath, null);
        if (path is not { Length: > 0 }) return;

        var doomed = path;
        _ = Task.Run(() => ShredFile(doomed));
    }

    private static void ShredFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            // The file can carry the DPAPI password blob, so the bytes go before the entry does.
            // A failed overwrite must still end in a delete, hence the inner catch.
            var length = info.Length;
            if (length > 0 && length <= 1024 * 1024)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    var zeros = new byte[(int)length];
                    stream.Write(zeros, 0, zeros.Length);
                    stream.Flush(true);
                }
                catch (Exception ex)
                {
                    AppLog.Debug_($"Could not overwrite the temporary .rdp file '{path}': {ex.Message}");
                }
            }

            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Could not remove the temporary .rdp file '{path}': {ex.Message}");
        }
    }

    private static void DisposeProcess(SessionRuntime runtime)
    {
        var process = Interlocked.Exchange(ref runtime.Process, null);
        if (process is null) return;
        try { process.Dispose(); } catch { }
    }

    /// <summary>Ends the tracked process if it is still running, then releases it.</summary>
    private static async Task KillProcessAsync(SessionRuntime runtime)
    {
        var process = Interlocked.Exchange(ref runtime.Process, null);
        if (process is null) return;

        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromMilliseconds(KillGraceMs)).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Could not end the stale Remote Desktop process: {ex.Message}");
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static int PidOf(Process process)
    {
        try { return process.Id; }
        catch { return 0; }
    }

    private static string ResolveMstsc()
    {
        try
        {
            // Always the system copy: a hijacked PATH must not be able to substitute a binary.
            var path = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
            if (File.Exists(path)) return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not resolve mstsc.exe from the system directory.", ex);
        }
        return "mstsc.exe";
    }

    private static void RaiseOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SafeRun(action);
            return;
        }

        try { _ = dispatcher.InvokeAsync(() => SafeRun(action)); }
        catch (Exception ex) { AppLog.Warn("Could not marshal a session update to the UI thread.", ex); }
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error("A session update handler threw.", ex); }
    }

    /// <summary>Per-session bookkeeping the bindable session model must not carry.</summary>
    private sealed class SessionRuntime(RdpSession session)
    {
        public readonly RdpSession Session = session;

        public readonly TaskCompletionSource<bool> WindowReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Process? Process;
        public string? RdpPath;
        public Guid? CredentialOverride;
        public Dictionary<string, string>? ExtraProperties;

        /// <summary>The owning multi-config wants every one of its sessions to end together.</summary>
        public bool CloseTogether;

        public DateTime LastAliveStampUtc = DateTime.UtcNow;
        public DateTime LastSnapshotAttemptUtc = DateTime.MinValue;

        public volatile bool Closed;
        public int Attaching;
        public int Restarting;
        public int SnapshotBusy;
        public int Finished;
    }
}
