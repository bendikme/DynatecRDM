using System.Windows.Threading;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;

namespace DynatecRDM.Services;

/// <summary>
/// The session bar: a strip that slides down from the top edge of a full-screen session - or of a
/// session window without a frame, which has no title bar of its own - and switches to any other
/// running connection with one click, so several full-screen sessions on one monitor behave like
/// tabs of a single window. It also carries what Remote Desktop's own bar
/// offered - minimise, leave full screen, disconnect - because that bar is kept out of the way while
/// this one is on: left out of the sessions it starts, and made invisible in any session that
/// shows one anyway. Otherwise the two would fight over the same edge.
///
/// Nothing runs while no session is in front: a foreground hook arms a short cursor poll only
/// while one is, and the window itself is not created until the bar is first needed.
/// </summary>
public sealed class SessionBarService : ISessionBarHost, IDisposable
{
    /// <summary>Cursor poll near the top edge and while the bar is out; the only work done between reveals.</summary>
    private const int FastPollMs = 40;

    /// <summary>Cursor poll while the pointer is nowhere near a top edge.</summary>
    private const int SlowPollMs = 110;

    /// <summary>
    /// Height of the reveal strip in DIPs. Scale with the monitor so reaching for the bar does
    /// not require hitting one physical pixel on a high-DPI display.
    /// </summary>
    private const int EdgeBand = 12;

    /// <summary>Within this many device pixels of a top edge the poll speeds up, so the reveal feels immediate.</summary>
    private const int NearEdge = 48;

    /// <summary>How long the pointer has to rest on the edge, so a fling at a remote tab strip does not open it.</summary>
    private const int RevealDwellMs = 120;

    /// <summary>How long the pointer may stray from the bar before it goes away.</summary>
    private const int HideGraceMs = 450;

    /// <summary>Device pixels around the bar that still count as being on it.</summary>
    private const int KeepAliveMargin = 12;

    /// <summary>A session that has just come up shows the bar briefly, so it is clear where the bar lives.</summary>
    private const int PeekDelayMs = 1500;
    private const int PeekMs = 2500;

    /// <summary>Remote Desktop lifts its window as it activates, so a switch re-raises the bar once more.</summary>
    private const int RaiseAgainMs = 200;

    /// <summary>
    /// How long Remote Desktop gets to leave full screen before a failure is logged. It resizes the
    /// remote desktop to the window as part of the switch, which takes a moment on a slow link.
    /// </summary>
    private const int FullScreenCheckMs = 1500;

    // The connection bar's right-hand buttons are always minimise, restore, close. Its Restore
    // button is the one thing that reliably leaves full screen from outside mstsc - measured against
    // a live session, where an injected Ctrl+Alt+Break and a full-screen WM_SYSCOMMAND were both
    // ignored, and it works even though the app switches the bar off in the .rdp file.
    private const int BarMinimizeButton = 0;
    private const int BarRestoreButton = 1;

    /// <summary>The window class of Remote Desktop's own connection bar.</summary>
    private const string NativeBarClass = "BBarWindowClass";

    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly SessionBarViewModel _viewModel;
    private readonly DispatcherTimer _poll;

    // Held in fields: the hooks call back through these delegates, and a collected one takes the
    // process down.
    private readonly Win32.WinEventProc _foregroundProc;
    private readonly Win32.WinEventProc _shownProc;

    /// <summary>Remote Desktop connection bars made invisible, with the style to give back.</summary>
    private readonly Dictionary<IntPtr, (uint ProcessId, long Style)> _hiddenNativeBars = new();

    private SessionBarWindow? _window;

    /// <summary>What the bar hangs from while it is out, or null.</summary>
    private BarSurface? _barSurface;
    private IntPtr _foregroundHook;
    private IntPtr _shownHook;
    private bool _enabled;
    private bool _disposed;
    private bool _windowFailed;
    private bool _pollFaulted;

    private long _edgeSince;
    private long _awaySince;

    /// <summary>A reveal that did not happen was written down for this stay on the edge - see <see cref="LogMissedReveal"/>.</summary>
    private bool _missLogged;

    /// <summary>When a reveal's dwell starting over was last written to the log - see <see cref="LogDwellRestart"/>.</summary>
    private long _restartLoggedAt;

    /// <summary>
    /// Since when the pointer has rested on the top edge of a monitor a session fills with no bar
    /// out, and whether that stay was written down - see <see cref="TrackStarvedReveal"/>.
    /// </summary>
    private long _bandSince;
    private bool _starvedLogged;
    private long _peekUntil;
    private Guid? _edgeSession;
    private PixelRect? _edgeArea;

    /// <summary>
    /// The frameless window whose top strip may reveal the bar: one the pointer has been inside,
    /// below the strip. See <see cref="ArmWindowStrip"/>.
    /// </summary>
    private PixelRect? _armedArea;

    /// <summary>
    /// What the bar hangs from: the monitor a full-screen session fills, or a frameless session
    /// window's own top edge. The bar is centred on <see cref="Area"/>; <see cref="Monitor"/> gives
    /// the scale.
    /// </summary>
    private readonly record struct BarSurface(MonitorInfo Monitor, PixelRect Area, bool IsWindow);

    public SessionBarService(AppServices services, IAppShell shell)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _viewModel = new SessionBarViewModel(this, () => _services.Settings.ConfirmSessionClose);

        _poll = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FastPollMs),
        };
        _poll.Tick += OnPoll;

        _foregroundProc = OnForegroundChanged;
        _shownProc = OnWindowShown;

        _services.SettingsChanged += OnSettingsChanged;
        _services.Sessions.SessionStarted += OnSessionsChanged;
        _services.Sessions.SessionStateChanged += OnSessionStateChanged;
        _services.Sessions.SessionEnded += OnSessionsChanged;

        SetEnabled(_services.Settings.SessionBarEnabled);
    }

    private bool IsShown => _window?.IsRevealed == true;

    // ------------------------------------------------------------------ actions

    public void SwitchTo(RdpSession session)
    {
        if (!session.IsActive) return;
        try
        {
            _services.Sessions.Focus(session.Id);
            // Focus can fail or target a window on a different display. Never leave action buttons
            // controlling a session behind the desktop the bar is actually covering.
            if (!ReferenceEquals(SessionFor(Win32.GetForegroundWindow()), session) || !IsUnderBar(session))
            {
                HideBar(animate: false);
                return;
            }
            _viewModel.SetCurrent(session.Id);
            _window?.KeepOnTop();
            After(RaiseAgainMs, () => _window?.KeepOnTop());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Switching to '{session.DisplayName}' failed.", ex);
        }
    }

    public void Minimize(RdpSession session)
    {
        HideBar(animate: false);

        // An embedded session runs in a window this application owns and has no connection bar to
        // click, so it is minimised directly.
        if (_services.Sessions is EmbeddedSessionManager embedded)
        {
            embedded.TryMinimize(session.Id);
            return;
        }

        // The connection bar's Minimize button first; a maximized-window minimise message as a
        // fallback, though mstsc disables it in full screen, which is exactly why the button exists.
        var pid = (uint)Math.Max(0, session.ProcessId);
        if (Win32.TryClickConnectionBarButton(pid, BarMinimizeButton)) return;
        if (!Win32.PostMinimize(session.WindowHandle))
            AppLog.Warn($"Could not minimise '{session.DisplayName}'.");
    }

    public void ExitFullScreen(RdpSession session)
    {
        HideBar(animate: false);

        // An embedded session owns its window, so it leaves full screen directly rather than through
        // a connection bar it does not have.
        if (_services.Sessions is EmbeddedSessionManager embedded)
        {
            embedded.TrySetFullScreen(session.Id, false);
            return;
        }

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        var pid = (uint)Math.Max(0, session.ProcessId);
        if (!Win32.TryClickConnectionBarButton(pid, BarRestoreButton))
        {
            AppLog.Warn($"Could not reach the connection bar to take '{session.DisplayName}' out of full screen.");
            return;
        }

        After(FullScreenCheckMs, () =>
        {
            if (!session.IsActive || !TryGetFilledMonitor(session, out _)) return;

            // A multimon session keeps its whole full-screen layout in the window, which can
            // still cover the monitor; a one-monitor session is never written that way.
            var layout = DisplayLayout.Resolve(session.Display, _services.Monitors.GetMonitors());
            AppLog.Warn(layout.UsesMultimon
                ? $"'{session.DisplayName}' is a multi-monitor session and still covers its monitor after " +
                  "leaving full screen; Remote Desktop keeps the full layout in the window."
                : $"'{session.DisplayName}' is still full screen after its connection bar Restore button was clicked.");
        });
    }

    public void EnterFullScreen(RdpSession session)
    {
        HideBar(animate: false);
        if (_services.Sessions is EmbeddedSessionManager embedded) embedded.TrySetFullScreen(session.Id, true);
    }

    public void ShowFrame(RdpSession session)
    {
        HideBar(animate: false);

        // The toggle would hide a frame already shown, so only a window without one is asked.
        if (session.Frameless != true || _services.Sessions is not EmbeddedSessionManager embedded) return;
        if (embedded.TryToggleFrame(session.Id)) _services.Sessions.Focus(session.Id);
    }

    public void Disconnect(RdpSession session)
    {
        HideBar(animate: true);
        _ = CloseAsync(session);
    }

    public void LaunchAnother()
    {
        HideBar(animate: false);
        _shell.ShowQuickLaunch();
    }

    public void OpenManager()
    {
        HideBar(animate: false);
        _shell.ShowMain();
    }

    /// <summary>
    /// Puts a new UI language on screen. The bar's text is fixed when its window is built, so the
    /// window is dropped and built again the next time the bar comes down.
    /// </summary>
    public void ReloadText()
    {
        if (_disposed) return;

        HideBar(animate: false);
        _window?.ForceClose();
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _services.SettingsChanged -= OnSettingsChanged;
        _services.Sessions.SessionStarted -= OnSessionsChanged;
        _services.Sessions.SessionStateChanged -= OnSessionStateChanged;
        _services.Sessions.SessionEnded -= OnSessionsChanged;

        _poll.Stop();
        Unhook();
        _viewModel.ResetConfirm();

        // Sessions outlive the application; without this bar they need their own back. Not the ones
        // in this process: they close with it, and a control on its way out refuses the change.
        RestoreNativeBars(inProcess: false);

        _window?.ForceClose();
        _window = null;
    }

    // ------------------------------------------------------------------ switching on and off

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var enabled = settings.SessionBarEnabled;
        if (_dispatcher.CheckAccess()) SetEnabled(enabled);
        else _ = _dispatcher.BeginInvoke(() => SetEnabled(enabled));
    }

    private void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == _enabled) return;
        _enabled = enabled;
        if (_services.Sessions is EmbeddedSessionManager embedded) embedded.SetSessionBarEnabled(enabled);

        if (enabled)
        {
            _windowFailed = false;
            _foregroundHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _foregroundProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

            // Without the hook nothing says when a session comes to the front, so the poll runs
            // whenever a session exists instead. Busier, but the bar still works.
            if (_foregroundHook == IntPtr.Zero)
                AppLog.Warn("The foreground hook is unavailable; the session bar polls while sessions run.");

            // Remote Desktop's own bar is left out of sessions started from now on, but a session
            // that was already running - or one that ignores the setting - still has one.
            _shownHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero,
                _shownProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            if (_shownHook == IntPtr.Zero)
                AppLog.Warn("The window-shown hook is unavailable; Remote Desktop's own bar may still appear.");

            _viewModel.Sync(_services.Sessions.Sessions);
            HideNativeBars();
            UpdatePolling();
        }
        else
        {
            Unhook();
            _poll.Stop();
            HideBar(animate: false);
            RestoreNativeBars();
        }

        AppLog.Info($"Session bar {(enabled ? "enabled" : "disabled")}.");
    }

    private void Unhook()
    {
        var foreground = _foregroundHook;
        _foregroundHook = IntPtr.Zero;
        if (foreground != IntPtr.Zero) Win32.UnhookWinEvent(foreground);

        var shown = _shownHook;
        _shownHook = IntPtr.Zero;
        if (shown != IntPtr.Zero) Win32.UnhookWinEvent(shown);
    }

    // ------------------------------------------------------------------ Remote Desktop's own bar

    /// <summary>Catches Remote Desktop's connection bar the moment it is shown, whichever session it is in.</summary>
    private void OnWindowShown(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // This sees every window shown anywhere, so the cheap tests come first.
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        try
        {
            if (_disposed || !_enabled || _windowFailed) return;
            if (!IsSessionProcess(hwnd)) return;
            if (!string.Equals(Win32.GetClassName(hwnd), NativeBarClass, StringComparison.Ordinal)) return;

            HideNativeBar(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hiding Remote Desktop's connection bar failed.", ex);
        }
    }

    /// <summary>Finds the connection bars our sessions already have, shown or not.</summary>
    private void HideNativeBars()
    {
        if (_windowFailed) return;
        var pids = new HashSet<uint>();
        foreach (var tab in _viewModel.Tabs)
            if (tab.Session.ProcessId > 0 && tab.Session.ProcessId != Environment.ProcessId)
                pids.Add((uint)tab.Session.ProcessId);
        if (pids.Count == 0) return;

        var bars = new List<IntPtr>();
        try
        {
            Win32.EnumWindows(hwnd =>
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (pids.Contains(pid) && string.Equals(Win32.GetClassName(hwnd), NativeBarClass, StringComparison.Ordinal))
                    bars.Add(hwnd);
                return true;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Looking for Remote Desktop's connection bars failed.", ex);
        }

        foreach (var bar in bars) HideNativeBar(bar);
    }

    /// <summary>
    /// Makes the bar invisible and lets clicks through it, rather than hiding it: Remote Desktop
    /// shows its bar again whenever the pointer reaches the top edge, and a merely hidden window
    /// would flash each time before it could be hidden again. Hidden outright only if its
    /// transparency cannot be set.
    /// </summary>
    private void HideNativeBar(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return;
        var invisible = Win32.TryMakeInvisible(hwnd, out var previous);

        // Only the first style seen is the real one; later calls see our own additions.
        if (Win32.IsWindow(hwnd) && _hiddenNativeBars.TryAdd(hwnd, (pid, previous)))
        {
            AppLog.Debug_(invisible
                ? $"Remote Desktop's connection bar 0x{hwnd.ToInt64():X} is now invisible."
                : $"Remote Desktop's connection bar 0x{hwnd.ToInt64():X} refused transparency; hiding it instead.");
        }

        if (!invisible) Win32.ShowWindowAsync(hwnd, Win32.SW_HIDE);
    }

    private void RestoreNativeBars(bool inProcess = true)
    {
        if (inProcess && _services.Sessions is EmbeddedSessionManager embedded) embedded.SetSessionBarEnabled(false);
        foreach (var pair in _hiddenNativeBars)
        {
            Win32.GetWindowThreadProcessId(pair.Key, out var pid);
            if (pid == pair.Value.ProcessId && Win32.GetClassName(pair.Key) == NativeBarClass)
                Win32.RestoreVisibility(pair.Key, pair.Value.Style);
        }
        _hiddenNativeBars.Clear();
    }

    private bool IsSessionProcess(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;

        foreach (var tab in _viewModel.Tabs)
            if (tab.Session.ProcessId == (int)pid) return true;
        return false;
    }

    // ------------------------------------------------------------------ sessions

    private void OnSessionsChanged(object? sender, RdpSession session)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => OnSessionsChanged(sender, session));
            return;
        }
        if (!_enabled || _disposed) return;

        _viewModel.Sync(_services.Sessions.Sessions);
        if (IsShown && (_viewModel.Current is not { } current || !IsUnderBar(current))) HideBar(animate: false);
        UpdatePolling();

        // Bars of sessions that have gone are nothing to give back.
        if (_hiddenNativeBars.Count == 0) return;
        List<IntPtr>? gone = null;
        foreach (var pair in _hiddenNativeBars)
            if (!Win32.IsWindow(pair.Key)) (gone ??= new List<IntPtr>()).Add(pair.Key);
        if (gone is not null) foreach (var hwnd in gone) _hiddenNativeBars.Remove(hwnd);
    }

    private void OnSessionStateChanged(object? sender, RdpSession session)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, session));
            return;
        }
        OnSessionsChanged(sender, session);
        if (!_enabled || _disposed || session.State != SessionState.Connected) return;

        HideNativeBars();
        After(PeekDelayMs, () => Peek(session));
    }

    /// <summary>
    /// Shows the bar for a moment over a session that has just come up in front, full screen or in
    /// a window without a frame, so it is clear where the bar lives.
    /// </summary>
    private void Peek(RdpSession session)
    {
        if (!_enabled || IsShown || !session.IsActive) return;
        if (!ReferenceEquals(SessionFor(Win32.GetForegroundWindow()), session)) return;
        if (SurfaceOf(session) is not { } surface) return;

        ShowBar(surface, session, peek: true);
    }

    private async Task CloseAsync(RdpSession session)
    {
        try
        {
            await _services.Sessions.CloseAsync(session.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not disconnect '{session.DisplayName}'.", ex);
            _shell.Notify(AppIdentity.Name, UiLanguage.Format(Strings.SessionBar_Error_Disconnect, session.DisplayName, ex.Message), true);
        }
    }

    /// <summary>
    /// The session a window belongs to. Matched by process as well as by handle: the handle is
    /// published a moment after the window appears, and Remote Desktop's dialogs belong to it too.
    /// </summary>
    private RdpSession? SessionFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var ownProcess = Environment.ProcessId;

        // Callers pass whatever is under the pointer or in the foreground, which for a session
        // hosted in this process is the remote desktop control - a child of the session window.
        // Matching its top-level owner finds the session exactly, without the process test below.
        var root = Win32.GetRootWindow(hwnd);

        foreach (var tab in _viewModel.Tabs)
        {
            var session = tab.Session;
            if (!session.IsActive || pid == 0 || session.ProcessId != (int)pid) continue;
            if (session.WindowHandle != IntPtr.Zero
                && (session.WindowHandle == hwnd || session.WindowHandle == root)) return session;

            // The process test is only meaningful for an external client. An embedded session runs
            // inside THIS process, where it would match every window the application owns - the main
            // window included - and hand back the wrong session for all of them. The handle match
            // above already covers it, child windows included.
            if (session.ProcessId == ownProcess) continue;

            if (pid != 0 && session.ProcessId > 0 && session.ProcessId == (int)pid) return session;
        }
        return null;
    }

    // ------------------------------------------------------------------ tracking the pointer

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (_disposed || !_enabled) return;

            // Alt+Tab, a notification, the manager: the bar belongs to the session it opened over.
            if (IsShown)
            {
                if (SessionFor(hwnd) is not { } front || !IsUnderBar(front))
                {
                    HideBar(animate: false);
                }
                else if (_barSurface is { IsWindow: true })
                {
                    // A window kept on top rises above the bar as it activates; put the bar back over it,
                    // once more a moment later as SwitchTo does, in case it lifts itself again.
                    _window?.KeepOnTop();
                    After(RaiseAgainMs, () => { if (IsShown) _window?.KeepOnTop(); });
                }
            }

            UpdatePolling();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The session bar could not follow a change of foreground window.", ex);
        }
    }

    /// <summary>
    /// Runs the cursor poll only while it can matter: the bar is out, a session is in front (it may
    /// go full screen at any moment), or a session fills a monitor even though something else has
    /// the keyboard - with a session per monitor, the bar works on every one of them.
    /// </summary>
    private void UpdatePolling()
    {
        var wanted = _enabled && !_disposed && !_windowFailed && _viewModel.Tabs.Count > 0
            && (IsShown
                || _foregroundHook == IntPtr.Zero
                || SessionFor(Win32.GetForegroundWindow()) is not null
                || AnyFullScreen()
                || AnyInThisProcess());

        if (wanted == _poll.IsEnabled) return;

        if (wanted)
        {
            _edgeSince = 0;
            _poll.Start();
        }
        else
        {
            _poll.Stop();
        }
    }

    /// <summary>
    /// True when a session runs inside this process. Such a session is hosted in a window the app
    /// owns, so it can be taken full screen without any foreground change for the hook to see -
    /// which would otherwise leave the pointer poll disarmed until the user happened to click
    /// something, and the bar would never appear on hover.
    /// </summary>
    private bool AnyInThisProcess()
    {
        var ownProcess = Environment.ProcessId;
        foreach (var tab in _viewModel.Tabs)
            if (tab.Session.IsActive && tab.Session.ProcessId == ownProcess) return true;
        return false;
    }

    private bool AnyFullScreen()
    {
        foreach (var tab in _viewModel.Tabs)
            if (TryGetFilledMonitor(tab.Session, out _)) return true;
        return false;
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        try
        {
            Poll();
            _pollFaulted = false;
        }
        catch (Exception ex)
        {
            // Once per run of failures, not twenty-five times a second.
            if (!_pollFaulted) AppLog.Warn("The session bar could not follow the pointer.", ex);
            _pollFaulted = true;
        }
    }

    private void Poll()
    {
        if (!_enabled || _disposed)
        {
            _poll.Stop();
            return;
        }

        var now = Environment.TickCount64;
        if (!Win32.GetCursorPos(out var cursor)) return;

        // Which frameless window's top strip may reveal the bar is followed on every tick, bar out or
        // not: a pass through another window while the bar hangs elsewhere arms that window's strip,
        // and the pointer on the bar itself - no session's - disarms the one it came from.
        var tile = FramelessUnder(cursor);
        _armedArea = ArmWindowStrip(_armedArea, tile?.Surface.Area, cursor.Y,
            tile is { } under ? RevealBandHeight(under.Surface.Monitor) : 0,
            atScreenTop: tile is { } top && IsScreenTop(cursor.X, top.Surface.Area.Top));

        if (IsShown)
        {
            _bandSince = 0;
            _starvedLogged = false;
            _missLogged = false;
            SetPollInterval(FastPollMs);
            if (_viewModel.Current is not { } current || _barSurface is not { } surface || !IsUnderBar(current))
            {
                HideBar(animate: false);
                UpdatePolling();
                return;
            }

            // A switch from the bar brings another session forward; if it landed on this monitor it
            // is the one the bar now belongs to. One that came up on another monitor leaves it be.
            // Frameless windows can share one rectangle exactly - two "maximized, no frame" sessions
            // on a monitor do - so over a window the bar follows whichever is visibly on top, never
            // one hidden underneath; its buttons act on what the user sees.
            var owner = surface.IsWindow
                ? SessionOnTop(surface)
                : SessionFor(Win32.GetForegroundWindow()) is { } front && FillsMonitor(front, surface.Monitor) ? front : null;
            if (owner is not null && !ReferenceEquals(owner, _viewModel.Current))
            {
                _viewModel.SetCurrent(owner.Id);
                if (surface.IsWindow) _window?.KeepOnTop();
            }

            var atEdge = surface.IsWindow ? FramelessAtEdge(cursor)?.Session : SessionAtEdge(cursor, surface.Monitor);
            if (IsOnBar(cursor) || ReferenceEquals(atEdge, _viewModel.Current))
            {
                // Full-screen RDP can move above an already visible topmost bar without changing
                // the foreground window. Recover on hover; IsRevealed alone does not mean the
                // bar is actually on top. Only raise over an RDP session, never another app.
                var underPointer = SessionFor(Win32.WindowFromPoint(cursor));
                if (surface.IsWindow)
                {
                    // A window kept on top rises above the bar when it is clicked. Seen under the
                    // pointer, the bar is covered or about to be: put it back. Another session lying
                    // over the bar itself - one gone full screen, say - takes the edge: let go.
                    if (ReferenceEquals(underPointer, _viewModel.Current))
                    {
                        _window?.KeepOnTop();
                    }
                    else if (underPointer is not null && IsOverBarItself(cursor))
                    {
                        HideBar(animate: false);
                        UpdatePolling();
                        return;
                    }
                }
                else if (underPointer is { } coveredBy && FillsMonitor(coveredBy, surface.Monitor))
                {
                    _viewModel.SetCurrent(coveredBy.Id);
                    _window?.KeepOnTop();
                }

                // Touched: from here on the bar stays exactly as long as the pointer does.
                _awaySince = 0;
                _peekUntil = 0;
                return;
            }

            if (now < _peekUntil) return;

            if (_awaySince == 0)
            {
                _awaySince = now;
                return;
            }

            if (now - _awaySince < HideGraceMs) return;

            HideBar(animate: true);
            UpdatePolling();
            return;
        }

        var monitor = _services.Monitors.GetMonitorAt(cursor.X, cursor.Y);

        // A window without a frame has a top edge of its own, anywhere on the monitor.
        var nearTop = cursor.Y <= monitor.Top + NearEdge
            || (tile is { } near && cursor.Y <= near.Surface.Area.Top + NearEdge);
        SetPollInterval(nearTop ? FastPollMs : SlowPollMs);
        TrackStarvedReveal(cursor, monitor, now);

        RdpSession? target = null;
        BarSurface reveal = default;
        if (SessionAtEdge(cursor, monitor) is { } full)
        {
            target = full;
            reveal = new BarSurface(monitor, monitor.Bounds, IsWindow: false);
        }
        else if (tile is { } window && _armedArea == window.Surface.Area
                 && IsInRevealBand(cursor, window.Surface.Area, window.Surface.Monitor))
        {
            target = window.Session;
            reveal = window.Surface;
        }

        // A button held down is a drag inside the remote desktop, not a reach for the bar.
        var buttonDown = Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON);
        if (target is null || buttonDown)
        {
            LogMissedReveal(cursor, monitor, buttonDown, now);
            _edgeSince = 0;
            return;
        }

        if (_edgeSince == 0 || _edgeSession != target.Id || _edgeArea != reveal.Area)
        {
            if (_edgeSince != 0) LogDwellRestart(target, reveal, now);
            _edgeSince = now;
            _edgeSession = target.Id;
            _edgeArea = reveal.Area;
            return;
        }

        if (now - _edgeSince < RevealDwellMs) return;

        _edgeSince = 0;
        ShowBar(reveal, target, peek: false);
    }

    /// <summary>
    /// Whether a frameless window's top strip may reveal the bar. A monitor's top edge stops the
    /// pointer, so resting on it is a reach for the bar; a window's top edge is out in the open, and
    /// the pointer crosses it on its way in from the window above - over and over, with windows
    /// stacked. So the strip counts only once the pointer has been inside the window below it, as it
    /// is when someone reaches up for the edge: <paramref name="armed"/> is kept while the pointer
    /// stays in that window, set when it is below the strip, and dropped as soon as it leaves. A
    /// window whose top is the top of the screen (<paramref name="atScreenTop"/>) is the exception:
    /// the pointer stops there as it does for full screen, so resting there is a reach already.
    /// </summary>
    internal static PixelRect? ArmWindowStrip(PixelRect? armed, PixelRect? under, int cursorY, int band, bool atScreenTop = false)
    {
        if (under is not { } area) return null;
        if (atScreenTop || cursorY >= area.Top + band) return area;
        return armed == area ? armed : null;
    }

    /// <summary>Nothing lies just above this point of a top edge: the pointer stops there instead of crossing it.</summary>
    private bool IsScreenTop(int x, int top)
    {
        foreach (var m in _services.Monitors.GetMonitors())
            if (x >= m.Left && x < m.Right && top - 1 >= m.Top && top - 1 < m.Bottom) return false;
        return true;
    }

    /// <summary>
    /// The session visibly on top of a frameless surface, found just below the bar (which covers the
    /// edge itself), mid-width. Null when something else covers that spot.
    /// </summary>
    private RdpSession? SessionOnTop(BarSurface surface)
    {
        var y = surface.Area.Top + RevealBandHeight(surface.Monitor);
        if (_window is not null && _window.TryGetScreenRect(out var bar)) y = Math.Max(y, bar.Bottom + KeepAliveMargin);
        if (y >= surface.Area.Bottom) return null;

        var probe = new Win32.POINT { X = surface.Area.Left + (surface.Area.Width / 2), Y = y };
        return SessionFor(Win32.WindowFromPoint(probe)) is { } top && IsUnderBar(top) ? top : null;
    }

    /// <summary>The pointer is on the bar's own rectangle - not merely in the margin around it.</summary>
    private bool IsOverBarItself(Win32.POINT cursor) =>
        _window is not null && _window.TryGetScreenRect(out var rect)
        && cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

    /// <summary>
    /// The session under the pointer, when its window is on the desktop without a frame, with the
    /// surface its bar would hang from.
    /// </summary>
    private (RdpSession Session, BarSurface Surface)? FramelessUnder(Win32.POINT cursor)
    {
        if (SessionFor(Win32.WindowFromPoint(cursor)) is not { } session) return null;
        return FramelessSurface(session) is { } surface ? (session, surface) : null;
    }

    /// <summary>The frameless session whose top edge the pointer rests on, if any.</summary>
    private (RdpSession Session, BarSurface Surface)? FramelessAtEdge(Win32.POINT cursor) =>
        FramelessUnder(cursor) is { } tile && IsInRevealBand(cursor, tile.Surface.Area, tile.Surface.Monitor) ? tile : null;

    /// <summary>
    /// A session window of this app on the desktop without a frame: the bar hangs from its own top
    /// edge, as it does from a monitor's for full screen. Null for anything else.
    /// </summary>
    private BarSurface? FramelessSurface(RdpSession session)
    {
        if (session.Frameless != true || session.ProcessId != Environment.ProcessId || !session.IsActive) return null;

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return null;
        if (!Win32.TryGetClientScreenRect(hwnd, out var client) || client.Width <= 0 || client.Height <= 0) return null;

        var area = new PixelRect(client.Left, client.Top, client.Width, client.Height);
        var monitor = _services.Monitors.GetMonitorAt(client.Left + (client.Width / 2), client.Top);
        return new BarSurface(monitor, area, IsWindow: true);
    }

    /// <summary>What a session's bar would hang from: the monitor it fills in full screen, or its frameless window.</summary>
    private BarSurface? SurfaceOf(RdpSession session) =>
        TryGetFilledMonitor(session, out var monitor)
            ? new BarSurface(monitor, monitor.Bounds, IsWindow: false)
            : FramelessSurface(session);

    /// <summary>
    /// The session is still where the bar hangs: filling the bar's monitor, or the very frameless
    /// window the bar came down on - a window that moved, or got its frame back, leaves it.
    /// </summary>
    private bool IsUnderBar(RdpSession session) =>
        _barSurface is { } surface && (surface.IsWindow
            ? FramelessSurface(session) is { } own && own.Area == surface.Area
            : FillsMonitor(session, surface.Monitor));

    /// <summary>
    /// The session showing on <paramref name="monitor"/>, when the pointer rests on its top edge and
    /// the session fills it. Only the middle half of the edge counts - the corners belong to the
    /// remote desktop's own buttons. Dwell makes internal edges usable on stacked monitors too.
    /// Prefer the session actually under the pointer. Thin top-edge windows can intercept hit
    /// testing on the very first row; in that case also check just inside the foreground session.
    /// </summary>
    private RdpSession? SessionAtEdge(Win32.POINT cursor, MonitorInfo monitor)
    {
        if (!IsInRevealBand(cursor, monitor)) return null;

        var session = SessionFor(Win32.WindowFromPoint(cursor));
        if (session is not null) return FillsMonitor(session, monitor) ? session : null;

        // The mouse can be clamped to the top row while a thin native edge window owns that
        // pixel. Do not make the user move down to reach the RDP child window. Probe below the
        // reveal strip without moving the real pointer, and only accept the foreground session
        // if it fills this monitor and is actually visible at that probe. This avoids revealing
        // over a different foreground app or a larger window covering the remote desktop.
        var front = SessionFor(Win32.GetForegroundWindow());
        if (front is null || !FillsMonitor(front, monitor)) return null;

        var inside = new Win32.POINT { X = cursor.X, Y = monitor.Top + RevealBandHeight(monitor) };
        return ReferenceEquals(SessionFor(Win32.WindowFromPoint(inside)), front) ? front : null;
    }

    /// <summary>
    /// Diagnostics for a reveal that should have happened: the pointer rests in the top band of a
    /// monitor over the session that fills it, yet the bar stays away. One line per stay on the
    /// edge, naming what each check saw, so a failure met on the user's desk can be read from the
    /// log. Silent otherwise - also over a session another app covers, where staying away is right.
    /// </summary>
    private void LogMissedReveal(Win32.POINT cursor, MonitorInfo monitor, bool buttonDown, long now)
    {
        if (_missLogged || !IsInRevealBand(cursor, monitor)) return;

        if (ExpectedAtEdge(cursor, monitor) is not { } filling) return;
        _missLogged = true;

        try
        {
            string Name(RdpSession? session) => session is null ? "no session" : $"'{session.DisplayName}'";
            string Fills(RdpSession? session) => session is null ? "" : FillsMonitor(session, monitor) ? ", fills it" : ", does not fill it";

            var hit = Win32.WindowFromPoint(cursor);
            Win32.GetWindowThreadProcessId(hit, out var hitPid);
            var underPointer = SessionFor(hit);
            var front = SessionFor(Win32.GetForegroundWindow());
            var inside = new Win32.POINT { X = cursor.X, Y = monitor.Top + RevealBandHeight(monitor) };
            var below = SessionFor(Win32.WindowFromPoint(inside));

            AppLog.Debug_(
                $"Session bar not revealed at ({cursor.X},{cursor.Y}) on {monitor.DeviceName}, which {Name(filling)} fills. " +
                $"Under the pointer: 0x{hit.ToInt64():X} {Win32.GetClassName(hit)} (process {hitPid}) - {Name(underPointer)}{Fills(underPointer)}. " +
                $"Foreground: {Name(front)}{Fills(front)}. Just below the band: {Name(below)}. " +
                $"Button held: {buttonDown}. Bar window: {(_windowFailed ? "failed" : _window is null ? "not built" : "ready")}.");
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Describing a missed session bar reveal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostics for a dwell that keeps starting over: a session was found at the edge, but not
    /// the one - or not over the area - of the tick before, so the wait for the reveal began again.
    /// Once is a pointer moving between surfaces; the same line every second while it rests is why
    /// the bar never comes. One line a second at most.
    /// </summary>
    private void LogDwellRestart(RdpSession target, BarSurface reveal, long now)
    {
        if (now - _restartLoggedAt < MissLogIntervalMs) return;
        _restartLoggedAt = now;

        string Area(PixelRect? area) => area is { } a ? $"{a.Left},{a.Top} {a.Width}x{a.Height}" : "nothing";
        var previous = _services.Sessions.Sessions.FirstOrDefault(s => s.Id == _edgeSession);
        AppLog.Debug_(
            $"Session bar reveal started over after {now - _edgeSince} ms: it was waiting for " +
            $"'{previous?.DisplayName ?? _edgeSession?.ToString() ?? "no session"}' over {Area(_edgeArea)}, " +
            $"and found '{target.DisplayName}' over {Area(reveal.Area)} ({(reveal.IsWindow ? "its window's top edge" : "the monitor's top edge")}).");
    }

    /// <summary>
    /// Diagnostics for a reveal starved by anything at all: the pointer has stayed on the top edge
    /// over the session filling the monitor for <see cref="StarvedRevealMs"/> and still no bar.
    /// Written once per stay, with where the dwell stands, so a cause nobody has thought of still
    /// shows up. Leaving the edge - or the session - ends the stay for both diagnostics.
    /// </summary>
    private void TrackStarvedReveal(Win32.POINT cursor, MonitorInfo monitor, long now)
    {
        if (!IsInRevealBand(cursor, monitor) || ExpectedAtEdge(cursor, monitor) is not { } filling)
        {
            _bandSince = 0;
            _starvedLogged = false;
            _missLogged = false;
            return;
        }

        if (_bandSince == 0)
        {
            _bandSince = now;
            return;
        }
        if (_starvedLogged || now - _bandSince < StarvedRevealMs) return;
        _starvedLogged = true;

        AppLog.Debug_(
            $"Session bar still not revealed after {now - _bandSince} ms on the top edge of {monitor.DeviceName}, " +
            $"which '{filling.DisplayName}' fills. Dwell: " +
            (_edgeSince == 0 ? "not waiting." : $"waiting {now - _edgeSince} ms for {_edgeSession}.") +
            $" Bar window: {(_windowFailed ? "failed" : _window is null ? "not built" : "ready")}.");
    }

    /// <summary>
    /// The session a reveal is expected over: one that fills <paramref name="monitor"/> and is what
    /// the user has there - under the pointer, just below the edge band, or in front. A session
    /// covered by another app is none of these; the bar rightly stays away from it.
    /// </summary>
    private RdpSession? ExpectedAtEdge(Win32.POINT cursor, MonitorInfo monitor)
    {
        var below = new Win32.POINT { X = cursor.X, Y = monitor.Top + RevealBandHeight(monitor) };
        foreach (var hwnd in new[] { Win32.WindowFromPoint(cursor), Win32.WindowFromPoint(below), Win32.GetForegroundWindow() })
            if (SessionFor(hwnd) is { } session && FillsMonitor(session, monitor)) return session;
        return null;
    }

    private const int MissLogIntervalMs = 1000;
    private const int StarvedRevealMs = 2000;

    internal static bool IsInRevealBand(Win32.POINT cursor, MonitorInfo monitor) =>
        IsInRevealBand(cursor, monitor.Bounds, monitor);

    /// <summary>
    /// The reveal strip along the top of <paramref name="area"/> - a monitor, or a window without a
    /// frame - at <paramref name="monitor"/>'s scale. Only the middle half of the edge counts.
    /// </summary>
    internal static bool IsInRevealBand(Win32.POINT cursor, PixelRect area, MonitorInfo monitor)
    {
        var quarter = area.Width / 4;
        return cursor.Y >= area.Top && cursor.Y < area.Top + RevealBandHeight(monitor)
            && cursor.X >= area.Left + quarter && cursor.X < area.Right - quarter;
    }

    private static int RevealBandHeight(MonitorInfo monitor) =>
        (int)Math.Ceiling(EdgeBand * (monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0));

    private void SetPollInterval(int milliseconds)
    {
        var interval = TimeSpan.FromMilliseconds(milliseconds);
        if (_poll.Interval != interval) _poll.Interval = interval;
    }

    private bool TryGetFilledMonitor(RdpSession session, out MonitorInfo monitor)
    {
        monitor = _services.Monitors.GetByIndex(-1);

        var hwnd = session.WindowHandle;
        if (hwnd == IntPtr.Zero || !Win32.TryGetClientScreenRect(hwnd, out var client)) return false;

        monitor = _services.Monitors.GetMonitorAt(client.Left + (client.Width / 2), client.Top);
        return FillsMonitor(session, monitor);
    }

    /// <summary>
    /// Full screen means the picture covers the whole monitor, taskbar included. The client area
    /// is what is compared, so a maximised window with a title bar never passes for one. A window
    /// without a frame can cover a monitor too - sized to it, or maximized where the taskbar hides -
    /// so a session in this app counts only when its window really is in full screen: the bar's
    /// "leave full screen" would have nothing to leave. Such a window gets the bar all the same,
    /// hung from its own top edge - see <see cref="FramelessSurface"/>.
    /// </summary>
    private bool FillsMonitor(RdpSession session, MonitorInfo monitor)
    {
        var hwnd = session.WindowHandle;
        if (!session.IsActive || hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)
            || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != session.ProcessId) return false;
        if (!Win32.TryGetClientScreenRect(hwnd, out var client)) return false;

        if (!(client.Left <= monitor.Left && client.Top <= monitor.Top
            && client.Right >= monitor.Right && client.Bottom >= monitor.Bottom)) return false;

        return session.ProcessId != Environment.ProcessId
            || _services.Sessions is not EmbeddedSessionManager embedded
            || embedded.IsFullScreen(session.Id);
    }

    private bool IsOnBar(Win32.POINT cursor)
    {
        if (_window is null || !_window.TryGetScreenRect(out var rect)) return false;

        return cursor.X >= rect.Left - KeepAliveMargin && cursor.X < rect.Right + KeepAliveMargin
            && cursor.Y >= rect.Top - KeepAliveMargin && cursor.Y < rect.Bottom + KeepAliveMargin;
    }

    // ------------------------------------------------------------------ the window

    private void ShowBar(BarSurface surface, RdpSession current, bool peek)
    {
        var window = EnsureWindow();
        if (window is null) return;

        _viewModel.Sync(_services.Sessions.Sessions);
        _viewModel.SetCurrent(current.Id);
        _viewModel.ResetConfirm();
        _viewModel.IsOverWindow = surface.IsWindow;

        _awaySince = 0;
        _peekUntil = peek ? Environment.TickCount64 + PeekMs : 0;
        _barSurface = surface;

        if (!window.Reveal(surface.Monitor, surface.Area))
        {
            _windowFailed = true;
            HideBar(animate: false);
            RestoreNativeBars();
            _poll.Stop();
            return;
        }
        // A full-screen session window sits in the topmost band as well, so the bar re-asserts
        // itself above whatever is there each time it appears rather than trusting the z-order.
        window.KeepOnTop();
        UpdatePolling();
    }

    private void HideBar(bool animate)
    {
        _edgeSince = 0;
        _awaySince = 0;
        _peekUntil = 0;
        _barSurface = null;
        _viewModel.ResetConfirm();
        _viewModel.SetCurrent(null);
        _window?.Conceal(animate);
    }

    private SessionBarWindow? EnsureWindow()
    {
        if (_window is not null || _windowFailed) return _window;

        try
        {
            _window = new SessionBarWindow(_viewModel);
        }
        catch (Exception ex)
        {
            // Not retried on every reveal: a window that cannot be built once will not be the next time.
            _windowFailed = true;
            _poll.Stop();
            RestoreNativeBars();
            AppLog.Error("The session bar could not be created; it stays off until the next start.", ex);
        }

        return _window;
    }

    private void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed) return;

            try { action(); }
            catch (Exception ex) { AppLog.Warn("A delayed session bar step failed.", ex); }
        };
        timer.Start();
    }
}
