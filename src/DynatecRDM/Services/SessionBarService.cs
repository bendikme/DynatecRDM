using System.Windows.Threading;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;

namespace DynatecRDM.Services;

/// <summary>
/// The session bar: a strip that slides down from the top edge of a full-screen session and
/// switches to any other running connection with one click, so several full-screen sessions on
/// one monitor behave like tabs of a single window. It also carries what Remote Desktop's own bar
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
    private MonitorInfo? _barMonitor;
    private IntPtr _foregroundHook;
    private IntPtr _shownHook;
    private bool _enabled;
    private bool _disposed;
    private bool _windowFailed;
    private bool _pollFaulted;

    private long _edgeSince;
    private long _awaySince;
    private long _peekUntil;
    private Guid? _edgeSession;
    private string? _edgeMonitor;

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
            if (!ReferenceEquals(SessionFor(Win32.GetForegroundWindow()), session)
                || _barMonitor is not { } monitor || !FillsMonitor(session, monitor))
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

        // Sessions outlive the application; without this bar they need their own back.
        RestoreNativeBars();

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

    private void RestoreNativeBars()
    {
        if (_services.Sessions is EmbeddedSessionManager embedded) embedded.SetSessionBarEnabled(false);
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
        if (IsShown && (_viewModel.Current is not { } current || _barMonitor is not { } monitor
            || !FillsMonitor(current, monitor))) HideBar(animate: false);
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

    /// <summary>Shows the bar for a moment over a session that has just come up in front, full screen.</summary>
    private void Peek(RdpSession session)
    {
        if (!_enabled || IsShown || !session.IsActive) return;
        if (!ReferenceEquals(SessionFor(Win32.GetForegroundWindow()), session)) return;
        if (!TryGetFilledMonitor(session, out var monitor)) return;

        ShowBar(monitor, session, peek: true);
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
            if (IsShown && (SessionFor(hwnd) is not { } front || _barMonitor is not { } monitor
                || !FillsMonitor(front, monitor))) HideBar(animate: false);

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

        if (IsShown)
        {
            SetPollInterval(FastPollMs);
            if (_viewModel.Current is not { } current || _barMonitor is not { } currentMonitor
                || !FillsMonitor(current, currentMonitor))
            {
                HideBar(animate: false);
                UpdatePolling();
                return;
            }

            // A switch from the bar brings another session forward; if it landed on this monitor it
            // is the one the bar now belongs to. One that came up on another monitor leaves it be.
            if (SessionFor(Win32.GetForegroundWindow()) is { } front
                && _barMonitor is { } barMonitor
                && FillsMonitor(front, barMonitor))
            {
                _viewModel.SetCurrent(front.Id);
            }

            if (IsOnBar(cursor) || ReferenceEquals(SessionAtEdge(cursor, currentMonitor), _viewModel.Current))
            {
                // Full-screen RDP can move above an already visible topmost bar without changing
                // the foreground window. Recover on hover; IsRevealed alone does not mean the
                // bar is actually on top. Only raise over an RDP session, never another app.
                if (SessionFor(Win32.WindowFromPoint(cursor)) is { } coveredBy
                    && FillsMonitor(coveredBy, currentMonitor))
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
        SetPollInterval(cursor.Y <= monitor.Top + NearEdge ? FastPollMs : SlowPollMs);

        // A button held down is a drag inside the remote desktop, not a reach for the bar.
        var target = SessionAtEdge(cursor, monitor);
        if (target is null || Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON))
        {
            _edgeSince = 0;
            return;
        }

        if (_edgeSince == 0 || _edgeSession != target.Id || _edgeMonitor != monitor.DeviceName)
        {
            _edgeSince = now;
            _edgeSession = target.Id;
            _edgeMonitor = monitor.DeviceName;
            return;
        }

        if (now - _edgeSince < RevealDwellMs) return;

        _edgeSince = 0;
        ShowBar(monitor, target, peek: false);
    }

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

    internal static bool IsInRevealBand(Win32.POINT cursor, MonitorInfo monitor)
    {
        var quarter = monitor.Width / 4;
        return cursor.Y >= monitor.Top && cursor.Y < monitor.Top + RevealBandHeight(monitor)
            && cursor.X >= monitor.Left + quarter && cursor.X < monitor.Right - quarter;
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
    /// is what is compared, so a maximised window with a title bar never passes for one.
    /// </summary>
    private static bool FillsMonitor(RdpSession session, MonitorInfo monitor)
    {
        var hwnd = session.WindowHandle;
        if (!session.IsActive || hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)
            || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != session.ProcessId) return false;
        if (!Win32.TryGetClientScreenRect(hwnd, out var client)) return false;

        return client.Left <= monitor.Left && client.Top <= monitor.Top
            && client.Right >= monitor.Right && client.Bottom >= monitor.Bottom;
    }

    private bool IsOnBar(Win32.POINT cursor)
    {
        if (_window is null || !_window.TryGetScreenRect(out var rect)) return false;

        return cursor.X >= rect.Left - KeepAliveMargin && cursor.X < rect.Right + KeepAliveMargin
            && cursor.Y >= rect.Top - KeepAliveMargin && cursor.Y < rect.Bottom + KeepAliveMargin;
    }

    // ------------------------------------------------------------------ the window

    private void ShowBar(MonitorInfo monitor, RdpSession current, bool peek)
    {
        var window = EnsureWindow();
        if (window is null) return;

        _viewModel.Sync(_services.Sessions.Sessions);
        _viewModel.SetCurrent(current.Id);
        _viewModel.ResetConfirm();

        _awaySince = 0;
        _peekUntil = peek ? Environment.TickCount64 + PeekMs : 0;
        _barMonitor = monitor;

        if (!window.Reveal(monitor))
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
        _barMonitor = null;
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
