using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Services;

namespace DynatecRDM.Rdp;

/// <summary>
/// A top-level window the application owns, hosting one <see cref="RdpControlHost"/>. This replaces
/// the external <c>mstsc</c>/<c>msrdc</c> window: because the app hosts the RDP control itself and
/// connects programmatically, there is no <c>.rdp</c> file launch and therefore no security warning,
/// and the app can renegotiate the remote resolution as the window resizes - true dynamic resolution,
/// which the external clients no longer let us drive.
///
/// The window is an ordinary HWND, so the existing <see cref="Services.IWindowPlacementService"/>
/// still positions and full-screens it, and snapshots can still capture it.
/// </summary>
public sealed class RdpSessionWindow : Form
{
    // Coalesce the flood of resize messages during a drag into one resolution change when it settles.
    private const int ResizeSettleMs = 250;

    private readonly RdpControlHost _host = new();
    private readonly System.Windows.Forms.Timer _resizeSettle;

    /// <summary>
    /// Covers the control while there is nothing to show yet - connecting, waiting to reconnect, or
    /// an error. It sits above the control rather than hiding it: the control only connects from a
    /// window that is really shown, and a hidden one never gets past "connecting".
    /// </summary>
    private readonly System.Windows.Forms.Integration.ElementHost _overlay;
    private ResizeBehavior _resize = ResizeBehavior.FollowWindow;

    /// <summary>What the connection asked for, for smart sizing to go back to when it is switched off.</summary>
    private ResizeBehavior _configuredResize = ResizeBehavior.FollowWindow;
    private int _desktopScaleFactor = 100;
    private int _deviceScaleFactor = 100;
    private bool _multimon;

    /// <summary>
    /// Where a multimon session sits in full screen - the box around its displays - and the displays
    /// themselves, to go back to after a spell in a window. Empty for a full-screen placement on one
    /// monitor, whatever the control was configured with.
    /// </summary>
    private Rectangle _fullScreenSpan;
    private Rectangle[] _spanScreens = Array.Empty<Rectangle>();

    /// <summary>
    /// A multimon session was given one display's size - it left full screen and followed its window,
    /// or started in one - so going full screen again has to spread it over the displays once more.
    /// </summary>
    private bool _singleDisplay;

    /// <summary>Full screen came back across the displays; the session is sent their layout.</summary>
    private bool _spreadPending;
    private bool _connected;

    private bool _fullScreen;
    private bool _inFullScreenChange;
    private Rectangle _windowedBounds;
    private FormBorderStyle _windowedBorder;
    private FormWindowState _windowedState;
    private Size _lastPushedSize = Size.Empty;
    private bool _alwaysOnTop;
    private bool _wasMinimized;

    /// <summary>
    /// The window has no frame while it is on the desktop - no title bar, no borders, only the
    /// session - so windows placed edge to edge meet without a seam. Full screen keeps it for the
    /// way back.
    /// </summary>
    private bool _frameless;

    /// <summary>
    /// The window was opened without a frame. It goes on lining up with the other session windows
    /// while its frame is shown for a moment to move or resize it.
    /// </summary>
    private bool _framelessIntent;

    /// <summary>While the frame is being switched, the one window rectangle every move in between is held to.</summary>
    private Rectangle? _pendingFrameRect;

    /// <summary>A move or resize under way that snaps to the other session windows, or null.</summary>
    private SnapDrag? _snapDrag;

    /// <summary>Every open session window, for one being moved to line up with. UI thread only.</summary>
    private static readonly List<RdpSessionWindow> OpenWindows = new();

    public RdpSessionWindow(string title)
    {
        Text = title;
        StartPosition = FormStartPosition.Manual;
        // A dark surround matches the app and hides the letterboxing before the session paints.
        BackColor = Color.FromArgb(18, 18, 18);
        Width = 1280;
        Height = 800;
        KeyPreview = false;
        ShowInTaskbar = true;
        Icon = LoadApplicationIcon();

        Controls.Add(_host.WinFormsControl);

        _overlay = new System.Windows.Forms.Integration.ElementHost
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Visible = false,
        };
        Controls.Add(_overlay);
        _overlay.BringToFront();

        _resizeSettle = new System.Windows.Forms.Timer { Interval = ResizeSettleMs };
        _resizeSettle.Tick += (_, _) =>
        {
            _resizeSettle.Stop();
            if (_spreadPending) SpreadOverDisplays();
            else PushResolution();
        };

        _host.Connecting += (_, _) => Connecting?.Invoke(this, EventArgs.Empty);
        _host.ReceivedServerKey += (_, _) => ReceivedServerKey?.Invoke(this, EventArgs.Empty);
        _host.AuthenticationWarning += (_, shown) => AuthenticationWarning?.Invoke(this, shown);
        _host.Connected += (_, _) => { _connected = true; Connected?.Invoke(this, EventArgs.Empty); };
        _host.LoginComplete += (_, _) =>
        {
            SyncControlFullScreenState();
            // A window resized while it was still connecting has a size the session was not given.
            // Only a real difference is pushed - see PushResolution.
            ScheduleResolutionPush();
            LoginComplete?.Invoke(this, EventArgs.Empty);
        };
        _host.Disconnected += (_, e) => { _connected = false; Disconnected?.Invoke(this, e); };

        // The session asks; this window answers. Ctrl+Alt+Break inside the session arrives here.
        _host.RequestGoFullScreen += (_, _) => EnterFullScreen();
        _host.RequestLeaveFullScreen += (_, _) =>
        {
            // Minimizing can make the control request windowed mode. Preserve the user's layout.
            if (WindowState != FormWindowState.Minimized) LeaveFullScreen();
        };
        _host.RequestContainerMinimize += (_, _) => MinimizeSession();

        OpenWindows.Add(this);
    }

    /// <summary>The hosted control, for the session manager to configure and connect.</summary>
    public RdpControlHost Host => _host;

    /// <summary>
    /// The application's own icon, so a session window is recognisably part of this app in the task
    /// bar and Alt+Tab. The shipped .ico is preferred because it carries every size; the icon bound
    /// into the executable is the fallback.
    /// </summary>
    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var shipped = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "dynatec.ico");
            if (System.IO.File.Exists(shipped)) return new Icon(shipped);
        }
        catch { }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        return null;   // the default window icon is still perfectly usable
    }

    /// <summary>
    /// Adds a full-screen entry to the window's own system menu (right-click the title bar, or
    /// Alt+Space), the way the built-in Remote Desktop window offers one. The captions are passed in
    /// so this window needs nothing from the resource assembly.
    /// </summary>
    public void EnableFullScreenMenu(string enterCaption, string exitCaption)
    {
        _menuEnterCaption = enterCaption;
        _menuExitCaption = exitCaption;
        _menuEnabled = true;
        EnsureSystemMenu();
    }

    /// <summary>
    /// Positions the window - before it is shown, or again when a set is launched while it is open.
    /// A full-screen session gets a borderless frame over <paramref name="bounds"/>; a windowed one
    /// gets a normal frame at it, or none at all when <paramref name="frameless"/>, and then the
    /// bounds are the session itself. The remembered windowed frame is set so a later
    /// <see cref="ToggleFullScreen"/> has somewhere to drop back to. A full-screen layout across
    /// several displays names them in <paramref name="spanScreens"/>, so full screen can go back to
    /// all of them later.
    /// </summary>
    public void PlaceAt(Rectangle bounds, bool fullScreen, Rectangle windowedBounds = default, bool maximized = false,
        bool frameless = false, IReadOnlyList<Rectangle>? spanScreens = null)
    {
        StartPosition = FormStartPosition.Manual;

        // Where a full-screen session lands when it is taken out of full screen. The caller works
        // this out from the connection's own layout; only fall back to a centred default when it
        // gave nothing usable.
        _windowedBounds = windowedBounds.Width >= DisplayLayoutMinEdge && windowedBounds.Height >= DisplayLayoutMinEdge
            ? windowedBounds
            : CentredDefault(bounds);

        // Set before the border style, which reads them back through CreateParams.
        _frameless = frameless;
        _framelessIntent = frameless;

        if (fullScreen)
        {
            _fullScreen = true;
            _spanScreens = spanScreens is { Count: > 1 } ? spanScreens.ToArray() : Array.Empty<Rectangle>();
            _fullScreenSpan = _spanScreens.Length > 1 ? bounds : Rectangle.Empty;
            _windowedBorder = FormBorderStyle.Sizable;
            // What leaving full screen goes back to: the layout's window, not a maximized one the
            // user left the session in before a set put it into place again.
            _windowedState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = bounds;
        }
        else
        {
            _fullScreen = false;
            _spreadPending = false;
            // A maximized window ignores new bounds, so one being put into place again comes down first.
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            FormBorderStyle = frameless ? FormBorderStyle.None : FormBorderStyle.Sizable;
            Bounds = bounds;
            // A connection asking for a maximised window gets one, rather than a window that merely
            // happens to be large. Without a frame the bounds already are the work area: a window
            // with no caption, maximized, would cover the taskbar as well.
            WindowState = maximized && !frameless ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        ApplyTopMost();
        ApplyFrameState();
        SyncControlFullScreenState();
        EnsureSystemMenu();

        // Put into place again while it runs - a set launched once more. Across its displays the
        // session's size is new to it the next time it leaves full screen; and it is given what the
        // window now is: spread over the displays, or the window's size.
        if (SpansDisplays) _lastPushedSize = Size.Empty;
        if (_connected) ScheduleResolutionPush();
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window is on the desktop without a frame.</summary>
    public bool IsFrameless => _frameless && !_fullScreen;

    /// <summary>
    /// The frame can be shown or hidden now: the window is on the desktop and neither full screen,
    /// minimized nor maximized.
    /// </summary>
    public bool CanToggleFrame => !_fullScreen && WindowState == FormWindowState.Normal;

    /// <summary>Raised when the window gains or loses its frame, or goes into or out of full screen.</summary>
    public event EventHandler? FrameChanged;

    /// <summary>
    /// Shows the frame of a window without one, or hides the frame of a window with one - to move or
    /// resize a frameless window, then hide it again. What can be seen of the window stays exactly
    /// where it is: shown, the frame grows into the window and the title bar takes its room from the
    /// session; hidden, the window becomes exactly what showed. So a window lined up with its
    /// neighbours stays lined up either way. Nothing is saved; the next launch opens as configured.
    /// </summary>
    public void ToggleFrame()
    {
        if (!CanToggleFrame || !IsHandleCreated) return;

        var visible = VisibleBounds();
        var showing = _frameless;   // going from no frame to a frame
        Rectangle target;
        if (showing)
        {
            // Only the borders Windows does not draw go outside what showed.
            var invisible = ScreenGeometry.InvisibleFrame(Win32.GetDpiForWindowSafe(Handle));
            target = Rectangle.FromLTRB(
                visible.Left - invisible.Left, visible.Top - invisible.Top,
                visible.Right + invisible.Right, visible.Bottom + invisible.Bottom);
        }
        else
        {
            target = visible;
        }

        _frameless = !showing;
        _pendingFrameRect = target;
        try
        {
            FormBorderStyle = showing ? FormBorderStyle.Sizable : FormBorderStyle.None;
        }
        finally
        {
            _pendingFrameRect = null;
        }
        ApplyFrameState();

        // The invisible borders above are Windows' usual ones; the frame now on screen says exactly.
        if (showing && Win32.TryGetExtendedFrameBounds(Handle, out var frame) && frame.Width > 0 && frame.Height > 0)
        {
            var shown = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            if (shown != visible)
            {
                var bounds = Bounds;
                Bounds = Rectangle.FromLTRB(
                    bounds.Left + visible.Left - shown.Left, bounds.Top + visible.Top - shown.Top,
                    bounds.Right + visible.Right - shown.Right, bounds.Bottom + visible.Bottom - shown.Bottom);
            }
        }

        EnsureSystemMenu();
        ScheduleResolutionPush();
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What can be seen of the window on screen: all of it without a frame, frame and inside with one.</summary>
    private Rectangle VisibleBounds()
    {
        var bounds = Bounds;
        if (IsFrameless || _fullScreen) return bounds;
        if (IsHandleCreated && Win32.TryGetExtendedFrameBounds(Handle, out var frame) && frame.Width > 0 && frame.Height > 0)
            return Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);

        var invisible = ScreenGeometry.InvisibleFrame(IsHandleCreated ? Win32.GetDpiForWindowSafe(Handle) : 96);
        return Rectangle.FromLTRB(
            bounds.Left + invisible.Left, bounds.Top + invisible.Top,
            bounds.Right - invisible.Right, bounds.Bottom - invisible.Bottom);
    }

    // ---------------------------------------------------------------- the frame, in Windows' terms

    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDefault = 0;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Without a frame WinForms gives the window no window menu and no minimize box, so a click on its
    /// taskbar button would not minimize it and Alt+Space would do nothing. Both are put back -
    /// neither draws anything without a title bar. The maximize box it does leave is taken away: a
    /// maximized window with no caption covers the taskbar too, and full screen is the way up.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_frameless && !_fullScreen && FormBorderStyle == FormBorderStyle.None)
                cp.Style = (cp.Style | WsSysMenu | WsMinimizeBox) & ~WsMaximizeBox;
            return cp;
        }
    }

    /// <summary>
    /// Brings the window's styles, and how Windows 11 draws its corners and border, in step with the
    /// frame. A border style change edits the styles in place - the window is not made again - so
    /// this runs after every change, not only when the handle is created.
    /// </summary>
    private void ApplyFrameState()
    {
        if (!IsHandleCreated) return;
        try { UpdateStyles(); } catch { }

        // Square corners and no outline without a frame, or neighbours would not meet cleanly;
        // Windows' own look with one. Windows 10 knows neither attribute and says so harmlessly.
        var frameless = IsFrameless;
        SetDwmAttribute(DwmwaWindowCornerPreference, frameless ? DwmwcpDoNotRound : DwmwcpDefault);
        SetDwmAttribute(DwmwaBorderColor, frameless ? DwmwaColorNone : DwmwaColorDefault);
    }

    private void SetDwmAttribute(int attribute, int value)
    {
        try { DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static Rectangle CentredDefault(Rectangle near)
    {
        var work = Screen.FromRectangle(near).WorkingArea;
        var w = Math.Min(1280, work.Width - 80);
        var h = Math.Min(800, work.Height - 80);
        return new Rectangle(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
    }

    /// <summary>
    /// Keep the session window above other windows, from the connection's own setting. Full screen
    /// implies it as well, so the two are resolved in one place - otherwise leaving full screen
    /// would quietly cancel a connection's always-on-top.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set { _alwaysOnTop = value; ApplyTopMost(); }
    }

    private void ApplyTopMost()
    {
        try
        {
            TopMost = WindowState != FormWindowState.Minimized && (_fullScreen || _alwaysOnTop)
                && s_windowsWaiting == 0;
        }
        catch { }
    }

    // ---------------------------------------------------------------- prompts that must be seen

    /// <summary>What a session window is waiting on the user for.</summary>
    [Flags]
    internal enum Waiting
    {
        None = 0,
        /// <summary>The window has been disabled: a modal dialog - the control's or the app's - is up.</summary>
        Dialog = 1,
        /// <summary>The control's sign-in prompt is up; it disables only the control, so it is watched for.</summary>
        SignIn = 2,
    }

    private const int WmEnable = 0x000A;
    private const uint GwEnabledPopup = 6;

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    /// <summary>How many session windows are waiting on a prompt. UI thread only.</summary>
    private static int s_windowsWaiting;
    private Waiting _waiting;

    /// <summary>
    /// A prompt waits on this window. While any does, no session window stays above other windows:
    /// a full-screen session, or a frameless tile kept on top, would cover a prompt on its screen -
    /// and the window the prompt belongs to stays disabled, every click on it a beep, with nothing
    /// to show why. Always-on-top comes back as soon as the last prompt is answered.
    /// </summary>
    internal void SetWaiting(Waiting what, bool waiting)
    {
        var before = _waiting != Waiting.None;
        _waiting = waiting ? _waiting | what : _waiting & ~what;
        var after = _waiting != Waiting.None;
        if (before == after) return;

        s_windowsWaiting = Math.Max(0, s_windowsWaiting + (after ? 1 : -1));
        foreach (var window in OpenWindows) window.ApplyTopMost();
    }

    /// <summary>
    /// The window was disabled or enabled again from outside - a modal dialog opened or closed over
    /// it. Logged with the dialog, so a session that stops answering clicks can be explained.
    /// </summary>
    private void OnEnableChanged(bool enabled)
    {
        try
        {
            if (enabled)
            {
                AppLog.Info($"Session window '{Text}' is enabled again.");
            }
            else
            {
                var popup = GetWindow(Handle, GwEnabledPopup);
                AppLog.Info($"Session window '{Text}' was disabled by {Describe(popup == Handle ? IntPtr.Zero : popup)}; " +
                            $"in front: {Describe(Win32.GetForegroundWindow())}.");
            }
        }
        catch { /* only a log line */ }

        SetWaiting(Waiting.Dialog, !enabled);
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "a window it does not own";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var process = "?";
        try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        return $"0x{hwnd.ToInt64():X} {Win32.GetClassName(hwnd)} '{Win32.GetWindowText(hwnd)}' ({process} {pid})";
    }

    public void MinimizeSession()
    {
        TopMost = false;
        WindowState = FormWindowState.Minimized;
    }

    public event EventHandler? Connecting;
    public event EventHandler? ReceivedServerKey;
    public event EventHandler<bool>? AuthenticationWarning;
    public event EventHandler? Connected;
    public event EventHandler? LoginComplete;
    public event EventHandler<RdpDisconnectInfo>? Disconnected;

    /// <summary>
    /// Raised when the remote desktop has actually been asked to change resolution, with the new
    /// size. Moving the window does not raise it - only a real size change does.
    /// </summary>
    public event EventHandler<Size>? RemoteResolutionChanged;

    /// <summary>
    /// Shows <paramref name="content"/> over the whole window, in front of the session. The same
    /// content object can be shown again later; it is only swapped when a different one is passed.
    /// </summary>
    public void ShowOverlay(System.Windows.UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!ReferenceEquals(_overlay.Child, content)) _overlay.Child = content;
        if (!_overlay.Visible)
        {
            _overlay.Visible = true;
            _overlay.BringToFront();
        }
        if (ContainsFocus || !IsHandleCreated) _overlay.Focus();
    }

    /// <summary>Takes the overlay away and gives the session the keyboard.</summary>
    public void HideOverlay()
    {
        if (!_overlay.Visible) return;
        var hadFocus = _overlay.ContainsFocus;
        var overlayWasActive = ReferenceEquals(ActiveControl, _overlay);
        _overlay.Visible = false;
        if (hadFocus) { try { _host.WinFormsControl.Focus(); } catch { } }
        else if (overlayWasActive)
        {
            // The window is in the background, but still remembers the hidden overlay as the control
            // to focus. Coming back, WinForms would then focus the window itself and typing would go
            // nowhere. Pointing it at the session only records the choice - no focus is taken now.
            try { ActiveControl = _host.WinFormsControl; } catch { }
        }
    }

    /// <summary>True while the overlay covers the session - nothing of the remote desktop is showing.</summary>
    public bool IsOverlayVisible => _overlay.Visible;

    /// <summary>
    /// True while one of the control's own modal prompts - the sign-in dialog or the certificate
    /// warning - is up: the prompt disables the window it belongs to for as long as it waits for the
    /// user. By default that is the control's own window, not this one, so both are looked at. One
    /// of the app's own dialogs disables this window too; the caller tells those apart.
    /// </summary>
    public bool IsWaitingForUser =>
        IsHandleCreated
        && (!IsWindowEnabled(Handle)
            || (_host.WinFormsControl.IsHandleCreated && !IsWindowEnabled(_host.WinFormsControl.Handle)));

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>
    /// Configures and starts the session. The window must be shown first so the control has a live
    /// handle to connect through. <paramref name="resize"/> decides whether a later resize
    /// renegotiates the remote resolution (follow-window) or is left to the control (scale/fixed).
    /// </summary>
    public void Start(
        RdpConnection connection,
        RdpDisplayPlan plan,
        RdpCredential? credential,
        IReadOnlyDictionary<string, string>? extraProperties = null,
        bool hideConnectionBar = true)
    {
        _resize = plan.Resize;
        _configuredResize = plan.Resize;
        // Kept for every later resize: the scale the connection was configured with has to be sent
        // again each time, or the first resize would silently reset the session to unscaled.
        _desktopScaleFactor = plan.DesktopScaleFactor;
        _deviceScaleFactor = plan.DeviceScaleFactor;
        _multimon = plan.UseMultimon;
        // Only a start across the displays spreads the session over them; one started in a window,
        // or full screen on one monitor, is one display, and has to be spread when it covers them.
        _singleDisplay = _multimon && !SpansDisplays;
        _spreadPending = false;
        // The session starts at the planned size, so a later move that changes nothing pushes nothing.
        _lastPushedSize = new Size(plan.DesktopWidth & ~1, plan.DesktopHeight & ~1);
        UnsupportedSettings = RdpControlConfigurator.Configure(
            _host.Control, connection, plan, credential, extraProperties, hideConnectionBar);
        // Read back rather than taken from the plan: a size pinned among the connection's own .rdp
        // lines wins over the plan, and that is the size the session really starts at.
        try { _lastPushedSize = new Size(_host.Control.DesktopWidth & ~1, _host.Control.DesktopHeight & ~1); }
        catch { /* the plan's size, set above */ }
        // A multimon session's size comes from its displays, not from the plan: whatever the window
        // is when it leaves full screen differs from it, so that is always pushed.
        if (_multimon) _lastPushedSize = Size.Empty;
        _pushRetries = 0;
        _host.Connect();
    }

    /// <summary>
    /// Pinned .rdp property names from the last <see cref="Start"/> that the control has no
    /// equivalent for. Empty for almost every connection; the session manager reports any that turn
    /// up so a setting never just disappears.
    /// </summary>
    public IReadOnlyList<string> UnsupportedSettings { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Switches between full screen on the window's current monitor and its previous frame.
    ///
    /// The window makes the change itself and only then tells the control, rather than asking the
    /// control and waiting to be called back. The control must never get a veto here: reading its
    /// full-screen state can fail, and a failed read reports "not full screen", which would make
    /// this silently do nothing. The control is still kept in step afterwards so its own
    /// Ctrl+Alt+Break keeps toggling in the right direction.
    /// </summary>
    public void ToggleFullScreen()
    {
        if (_fullScreen) LeaveFullScreen(); else EnterFullScreen();
    }

    public bool IsFullScreen => _fullScreen;

    /// <summary>
    /// Tells the control which way the window now is, so its own Ctrl+Alt+Break toggles in the right
    /// direction. Also covers a session that was STARTED full screen: without this the control still
    /// believes it is windowed, and the first Ctrl+Alt+Break would do no more than correct its own
    /// idea of the state, looking like it did nothing.
    ///
    /// Any request the control raises back at us is harmless - the window is already in that state,
    /// so <see cref="EnterFullScreen"/>/<see cref="LeaveFullScreen"/> return immediately.
    /// </summary>
    private void SyncControlFullScreenState()
    {
        try { _host.FullScreen = _fullScreen; }
        catch { /* the control refuses this until it is connected; the window is already correct */ }
    }

    /// <summary>
    /// Takes the window full screen: a multimon session back across the displays it was opened on,
    /// any other on the monitor it is currently on.
    /// </summary>
    public void EnterFullScreen()
    {
        if (_fullScreen || _inFullScreenChange || WindowState == FormWindowState.Minimized) return;
        _inFullScreenChange = true;
        try
        {
            _windowedState = WindowState;
            _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _windowedBorder = FormBorderStyle == FormBorderStyle.None ? FormBorderStyle.Sizable : FormBorderStyle;
            _fullScreen = true;

            var span = MultimonSpan();
            var screen = span ?? Screen.FromControl(this).Bounds;
            // A minimised window is left alone for the same reason as in LeaveFullScreen: going full
            // screen must not yank it back onto the desktop behind the user's back.
            // Clear any maximised state first: a maximised window ignores an explicit Bounds.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            // Above the taskbar, the way a full-screen remote session is expected to sit.
            ApplyTopMost();
            ApplyFrameState();
        }
        finally { _inFullScreenChange = false; }

        SyncControlFullScreenState();
        EnsureSystemMenu();
        // Whatever the window was, a session across its displays again finds the window's size new
        // the next time it leaves full screen - even the same window as before.
        if (SpansDisplays) _lastPushedSize = Size.Empty;
        ScheduleResolutionPush();
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where a multimon session goes full screen: the box around the displays it was placed across,
    /// while every one of them is still there as it was. Null for any other session, or when the
    /// displays have changed since. Displays of different sizes, or offset, leave parts of the box
    /// on no display at all; that is the layout, not a display gone.
    /// </summary>
    private Rectangle? MultimonSpan()
    {
        if (!_multimon || _spanScreens.Length < 2
            || _fullScreenSpan.Width < DisplayLayoutMinEdge || _fullScreenSpan.Height < DisplayLayoutMinEdge)
            return null;

        var screens = Screen.AllScreens;
        foreach (var recorded in _spanScreens)
            if (!screens.Any(s => s.Bounds == recorded)) return null;
        return _fullScreenSpan;
    }

    /// <summary>
    /// The window is in full screen across the displays of a multimon session - where its layout
    /// comes from the displays, not from a size pushed from here.
    /// </summary>
    private bool SpansDisplays => _fullScreen && MultimonSpan() is { } span && Bounds == span;

    private void ScheduleSpread()
    {
        _spreadPending = true;
        _pushRetries = 0;
        _resizeSettle.Stop();
        _resizeSettle.Interval = ResizeSettleMs;
        _resizeSettle.Start();
    }

    /// <summary>
    /// Sends a multimon session back in full screen the layout of the displays it covers, which the
    /// control works out itself - a size pushed from here is only ever one display.
    /// </summary>
    private void SpreadOverDisplays()
    {
        // Let go when there is nothing to spread over, or no one to tell yet: signing in, restoring
        // from minimized and every other way back across the displays ask again.
        if (!SpansDisplays || !_connected || WindowState == FormWindowState.Minimized)
        {
            _spreadPending = false;
            return;
        }
        if (_host.SyncDisplaySettings())
        {
            _spreadPending = false;
            _singleDisplay = false;
            _lastPushedSize = Size.Empty;
            _pushRetries = 0;
            _resizeSettle.Interval = ResizeSettleMs;
            AppLog.Info($"Session '{Text}' is spread over its displays again.");
            return;
        }
        if (_pushRetries++ < MaxPushRetries)
        {
            _resizeSettle.Interval = PushRetryMs;
            _resizeSettle.Start();
            return;
        }
        _spreadPending = false;
        _resizeSettle.Interval = ResizeSettleMs;
        AppLog.Info($"Session '{Text}' would not take its displays' layout back.");
    }

    /// <summary>Returns the window to the frame it had before it went full screen.</summary>
    public void LeaveFullScreen()
    {
        if (!_fullScreen || _inFullScreenChange) return;
        _inFullScreenChange = true;
        _spreadPending = false;
        try
        {
            _fullScreen = false;
            ApplyTopMost();   // not simply false: the connection may ask to stay on top anyway
            // A window opened without a frame goes back to having none.
            FormBorderStyle = _frameless
                ? FormBorderStyle.None
                : _windowedBorder == FormBorderStyle.None ? FormBorderStyle.Sizable : _windowedBorder;
            ApplyFrameState();

            // Never pull the window out of a minimised state. Minimising a full-screen session makes
            // the control report that it has left full screen, and that arrives here a moment later -
            // restoring the window would undo the minimise the user just asked for. The frame is put
            // back the next time the window is actually shown instead.
            if (WindowState != FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
                if (_windowedBounds.Width >= DisplayLayoutMinEdge && _windowedBounds.Height >= DisplayLayoutMinEdge)
                    Bounds = _windowedBounds;
                WindowState = _windowedState;
            }
        }
        finally { _inFullScreenChange = false; }

        SyncControlFullScreenState();
        EnsureSystemMenu();
        ScheduleResolutionPush();
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        base.OnResizeBegin(e);
        BeginSnapDrag();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        _snapDrag = null;
        base.OnResizeEnd(e);
        ScheduleResolutionPush();
    }

    /// <summary>
    /// A window without a frame keeps its size in pixels when it is moved onto a monitor with other
    /// scaling: its neighbours there are laid out in pixels too, and the session need not change.
    /// </summary>
    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
    {
        if (IsFrameless)
        {
            desiredSize = Size;
            return true;
        }
        return base.OnGetDpiScaledSize(deviceDpiOld, deviceDpiNew, ref desiredSize);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        // Without a caption there is nothing to maximize to, and a maximized borderless window covers
        // the taskbar; one that got there anyway (a keyboard shortcut) goes straight back.
        if (IsFrameless && WindowState == FormWindowState.Maximized && !_inFullScreenChange)
        {
            WindowState = FormWindowState.Normal;
            return;
        }

        base.OnSizeChanged(e);
        if (!_inFullScreenChange) ApplyTopMost();
        if (WindowState == FormWindowState.Minimized) _wasMinimized = true;
        else if (_wasMinimized)
        {
            _wasMinimized = false;
            SyncControlFullScreenState();
        }
        // Maximize/restore does not raise ResizeEnd, so react to the size change too; the timer
        // coalesces the burst either way.
        if (_connected) ScheduleResolutionPush();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) { try { _host.Disconnect(); } catch { } }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_waiting != Waiting.None) SetWaiting(_waiting, false);
            OpenWindows.Remove(this);
            _resizeSettle.Dispose();
            _host.Dispose();
            try { _overlay.Child = null; _overlay.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    private void ScheduleResolutionPush()
    {
        // A multimon session across its displays again, after it was given one display's size, is
        // spread over them - whatever its resize setting, since the window is what reduced it.
        if (SpansDisplays && _singleDisplay)
        {
            ScheduleSpread();
            return;
        }
        if (_resize != ResizeBehavior.FollowWindow) return;
        _pushRetries = 0;
        _resizeSettle.Stop();
        _resizeSettle.Interval = ResizeSettleMs;
        _resizeSettle.Start();
    }

    private const int MaxPushRetries = 3;
    private const int PushRetryMs = 1000;
    private int _pushRetries;

    private void PushResolution()
    {
        if (!_connected || WindowState == FormWindowState.Minimized || _resize != ResizeBehavior.FollowWindow) return;

        // A multimon session across its displays takes its layout from them. A size pushed from here
        // is always one display, so the window spanning them would merge them into one wide screen.
        // One full screen on a single monitor - its displays gone, or put there by a set - is sized
        // like any other.
        if (SpansDisplays) return;

        var client = _host.WinFormsControl.ClientSize;
        if (client.Width < DisplayLayoutMinEdge || client.Height < DisplayLayoutMinEdge) return;

        // Only when the size actually changed. Windows ends a window MOVE with the same message that
        // ends a resize, so without this a plain drag across the desktop would renegotiate the remote
        // desktop at its current size - which costs a full screen redraw and looks like a blink.
        // Compared as sent: the session only takes even sizes.
        var sent = new Size(client.Width & ~1, client.Height & ~1);
        if (sent == _lastPushedSize) return;

        // The scale the connection was configured with, not one derived from the window: the .rdp
        // path sent exactly what the user chose, and deriving it here would quietly override them.
        if (!_host.UpdateResolution(client.Width, client.Height, _desktopScaleFactor, _deviceScaleFactor))
        {
            // Refused - most often a session that has only just signed in and cannot take a new size
            // yet. Tried again a few times, a second apart; after that the next resize will.
            if (_pushRetries++ < MaxPushRetries)
            {
                _resizeSettle.Interval = PushRetryMs;
                _resizeSettle.Start();
            }
            else AppLog.Info($"Session '{Text}' would not take the window's size ({sent.Width}x{sent.Height}).");
            return;
        }

        _pushRetries = 0;
        _resizeSettle.Interval = ResizeSettleMs;
        _lastPushedSize = sent;
        if (_multimon) _singleDisplay = true;
        RemoteResolutionChanged?.Invoke(this, client);
    }

    private const int DisplayLayoutMinEdge = 200;

    // ---------------------------------------------------------------- system menu

    // Below the reserved SC_* range (0xF000+), and their low four bits are zero because Windows
    // reserves them inside a system command's id - which is also why the two are 0x10 apart.
    private const int SysCommandFullScreen = 0xA000;
    private const int SysCommandFrame = 0xA010;
    private const int SysCommandAlwaysOnTop = 0xA020;
    private const int SysCommandSmartSizing = 0xA030;
    private const int ScMaximize = 0xF030;
    private const int WmSysCommand = 0x0112;
    private const int WmInitMenuPopup = 0x0117;
    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfByCommand = 0x0000;
    private const int MfEnabled = 0x0000;
    private const int MfGrayed = 0x0001;
    private const int MfUnchecked = 0x0000;
    private const int MfChecked = 0x0008;

    private bool _menuEnabled;
    private string _menuEnterCaption = "Full screen";
    private string _menuExitCaption = "Exit full screen";
    private string _menuHideFrameCaption = "Hide window frame";
    private string _menuShowFrameCaption = "Show window frame";
    private string _menuAlwaysOnTopCaption = "Always on top";
    private string _menuSmartSizingCaption = "Smart sizing";

    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenu(IntPtr hMenu, int uPosition, int uFlags, IntPtr uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")] private static extern int GetMenuState(IntPtr hMenu, int uId, int uFlags);
    [DllImport("user32.dll")] private static extern bool EnableMenuItem(IntPtr hMenu, int uIDEnableItem, int uEnable);
    [DllImport("user32.dll")] private static extern int CheckMenuItem(IntPtr hMenu, int uIDCheckItem, int uCheck);

    /// <summary>The captions for the frame entry of the window menu, in the UI language.</summary>
    public void EnableFrameMenu(string hideCaption, string showCaption)
    {
        _menuHideFrameCaption = hideCaption;
        _menuShowFrameCaption = showCaption;
        EnsureSystemMenu();
    }

    /// <summary>
    /// The captions for the window menu's toggles, in the UI language - the switches the built-in
    /// Remote Desktop window keeps there, for this session only.
    /// </summary>
    public void EnableMenuToggles(string alwaysOnTopCaption, string smartSizingCaption)
    {
        _menuAlwaysOnTopCaption = alwaysOnTopCaption;
        _menuSmartSizingCaption = smartSizingCaption;
        EnsureSystemMenu();
    }

    /// <summary>The remote picture is scaled to the window rather than the session resized to it.</summary>
    public bool SmartSizing => _resize == ResizeBehavior.Scale;

    /// <summary>
    /// Switches smart sizing for this session, as the built-in client's window menu does: on, the
    /// remote desktop keeps its resolution and is scaled to the window; off, it goes back to what
    /// the connection asked for - following the window, or a fixed size. Not saved.
    /// </summary>
    public void ToggleSmartSizing()
    {
        var scale = !SmartSizing;
        try
        {
            _host.Control.AdvancedSettings9.SmartSizing = scale;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The session would not switch smart sizing.", ex);
            return;
        }

        _resize = scale
            ? ResizeBehavior.Scale
            : _configuredResize == ResizeBehavior.Scale ? ResizeBehavior.FollowWindow : _configuredResize;
        if (!scale) ScheduleResolutionPush();   // back to following: the session takes the window's size
        EnsureSystemMenu();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureSystemMenu();
        ApplyFrameState();
    }

    /// <summary>
    /// Adds the app's entries to the window menu - each only when it is not there yet, since the
    /// menu outlives every change of border style - and words them for the window's state. A
    /// window made without a window menu (one that opened full screen) has none until it gets a frame.
    /// </summary>
    private void EnsureSystemMenu()
    {
        if (!_menuEnabled || !IsHandleCreated) return;
        try
        {
            var menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return;

            if (GetMenuState(menu, SysCommandFullScreen, MfByCommand) == -1)
            {
                AppendMenu(menu, MfSeparator, 0, null);
                AppendMenu(menu, MfString, SysCommandFullScreen, _fullScreen ? _menuExitCaption : _menuEnterCaption);
            }
            if (GetMenuState(menu, SysCommandFrame, MfByCommand) == -1)
                AppendMenu(menu, MfString, SysCommandFrame, _frameless ? _menuShowFrameCaption : _menuHideFrameCaption);
            if (GetMenuState(menu, SysCommandAlwaysOnTop, MfByCommand) == -1)
            {
                AppendMenu(menu, MfSeparator, 0, null);
                AppendMenu(menu, MfString, SysCommandAlwaysOnTop, _menuAlwaysOnTopCaption);
            }
            if (GetMenuState(menu, SysCommandSmartSizing, MfByCommand) == -1)
                AppendMenu(menu, MfString, SysCommandSmartSizing, _menuSmartSizingCaption);

            UpdateSystemMenu(menu);
        }
        catch { /* the menu is a convenience; the hotkey and the app's own buttons still work */ }
    }

    /// <summary>Words the entries for the window's state and greys out what cannot be done now.</summary>
    private void UpdateSystemMenu(IntPtr menu)
    {
        ModifyMenu(menu, SysCommandFullScreen, MfByCommand | MfString, SysCommandFullScreen,
            _fullScreen ? _menuExitCaption : _menuEnterCaption);
        ModifyMenu(menu, SysCommandFrame, MfByCommand | MfString, SysCommandFrame,
            _frameless ? _menuShowFrameCaption : _menuHideFrameCaption);
        EnableMenuItem(menu, SysCommandFrame, MfByCommand | (CanToggleFrame ? MfEnabled : MfGrayed));
        ModifyMenu(menu, SysCommandAlwaysOnTop, MfByCommand | MfString, SysCommandAlwaysOnTop, _menuAlwaysOnTopCaption);
        CheckMenuItem(menu, SysCommandAlwaysOnTop, MfByCommand | (_alwaysOnTop ? MfChecked : MfUnchecked));
        ModifyMenu(menu, SysCommandSmartSizing, MfByCommand | MfString, SysCommandSmartSizing, _menuSmartSizingCaption);
        CheckMenuItem(menu, SysCommandSmartSizing, MfByCommand | (SmartSizing ? MfChecked : MfUnchecked));
        // WinForms enables Maximize from its own MaximizeBox each time; a frameless window has none.
        if (IsFrameless) EnableMenuItem(menu, ScMaximize, MfByCommand | MfGrayed);
    }

    // ---------------------------------------------------------------- lining up with other windows

    /// <summary>How close, in 96-DPI pixels, an edge has to come to another to snap to it.</summary>
    private const int SnapDip = 10;
    private const int VkControl = 0x11;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmSizing = 0x0214;
    private const int WmMoving = 0x0216;
    private const int WmDpiChanged = 0x02E0;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public int Flags;
    }

    /// <summary>What a snapping move or resize lines up with, read once when it starts.</summary>
    private sealed class SnapDrag
    {
        public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
        public required IReadOnlyList<PixelRect> Neighbours { get; init; }
        public required int Threshold { get; init; }
        public Rectangle? Last { get; set; }
    }

    /// <summary>
    /// A window without a frame - or one opened without and showing its frame for a moment - lines up
    /// with the other session windows and the monitor and taskbar edges while it is moved or resized.
    /// Framed windows move as Windows moves them. Ctrl held places it exactly where it is dragged.
    /// </summary>
    private void BeginSnapDrag()
    {
        _snapDrag = null;
        if (_fullScreen || !(_frameless || _framelessIntent) || !IsHandleCreated) return;

        try
        {
            var screens = Screen.AllScreens;
            var monitors = new List<MonitorInfo>(screens.Length);
            for (var i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                var w = screens[i].WorkingArea;
                monitors.Add(new MonitorInfo(i, screens[i].DeviceName, string.Empty,
                    b.X, b.Y, b.Width, b.Height, w.X, w.Y, w.Width, w.Height, screens[i].Primary, 96, 96));
            }

            var neighbours = new List<PixelRect>();
            foreach (var other in OpenWindows)
            {
                if (ReferenceEquals(other, this) || !other.IsSnapTarget) continue;
                var body = other.VisibleBounds();
                neighbours.Add(new PixelRect(body.X, body.Y, body.Width, body.Height));
            }

            _snapDrag = new SnapDrag
            {
                Monitors = monitors,
                Neighbours = neighbours,
                Threshold = (int)Math.Round(SnapDip * Win32.GetDpiForWindowSafe(Handle) / 96.0),
            };
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Preparing to line the session window up failed: {ex.Message}");
        }
    }

    /// <summary>Another session window worth lining up with: on this desktop, and neither hidden, full screen, minimized nor maximized.</summary>
    private bool IsSnapTarget =>
        IsHandleCreated && Visible && !_fullScreen && WindowState == FormWindowState.Normal
        && !Win32.IsWindowCloaked(Handle);

    /// <summary>
    /// Moves the rectangle Windows proposes for a move or resize onto the lines nearby. It works on
    /// what shows of the window, so what meets a neighbour is what can be seen - and stays met when
    /// the frame is hidden again. A resized window without a frame keeps an even size: the session
    /// takes only even sizes, and an odd one would leave a line of nothing beside it.
    /// </summary>
    private bool SnapProposedRect(ref Message m, RectEdges? resizing)
    {
        if (_snapDrag is not { } drag || m.LParam == IntPtr.Zero || Win32.IsKeyDown(VkControl)) return false;

        var proposed = Marshal.PtrToStructure<Win32.RECT>(m.LParam);
        var window = Rectangle.FromLTRB(proposed.Left, proposed.Top, proposed.Right, proposed.Bottom);

        // The part that does not show, as it is now - a change of scale mid-drag changes it.
        var current = Bounds;
        var visible = VisibleBounds();
        var insets = new PixelInsets(
            visible.Left - current.Left, visible.Top - current.Top,
            current.Right - visible.Right, current.Bottom - visible.Bottom);
        var body = insets.Deflate(new PixelRect(window.X, window.Y, window.Width, window.Height));

        PixelRect snapped;
        if (resizing is { } edges)
        {
            snapped = ScreenGeometry.SnapEdges(body, edges, drag.Monitors, drag.Neighbours, drag.Threshold);
            if (_frameless || _framelessIntent) snapped = EvenSize(snapped, edges, drag.Monitors);
        }
        else
        {
            var dx = drag.Last is { } last ? Math.Sign(window.X - last.X) : 0;
            var dy = drag.Last is { } lastY ? Math.Sign(window.Y - lastY.Y) : 0;
            snapped = ScreenGeometry.SnapMove(body, drag.Monitors, drag.Neighbours, drag.Threshold, dx, dy);
        }
        drag.Last = window;

        var result = insets.Inflate(snapped);
        Marshal.StructureToPtr(
            new Win32.RECT { Left = result.Left, Top = result.Top, Right = result.Right, Bottom = result.Bottom },
            m.LParam, false);
        m.Result = (IntPtr)1;
        return true;
    }

    /// <summary>
    /// An odd size grows by the edge being dragged - over the neighbour rather than short of it. An
    /// edge on a monitor or taskbar line stays there and the size stays odd, which the resolution
    /// push rounds down: growing it would put a line of the session on the next monitor.
    /// </summary>
    private static PixelRect EvenSize(PixelRect rect, RectEdges edges, IReadOnlyList<MonitorInfo> monitors)
    {
        bool OnScreenLine(int edge, bool vertical)
        {
            foreach (var m in monitors)
            {
                var (b, w) = (m.Bounds, m.WorkArea);
                if (vertical
                    ? edge == b.Left || edge == b.Right || edge == w.Left || edge == w.Right
                    : edge == b.Top || edge == b.Bottom || edge == w.Top || edge == w.Bottom)
                {
                    return true;
                }
            }
            return false;
        }

        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
        if (((right - left) & 1) != 0)
        {
            if (edges.HasFlag(RectEdges.Left)) { if (!OnScreenLine(left, true)) left--; }
            else if (edges.HasFlag(RectEdges.Right)) { if (!OnScreenLine(right, true)) right++; }
        }
        if (((bottom - top) & 1) != 0)
        {
            if (edges.HasFlag(RectEdges.Top)) { if (!OnScreenLine(top, false)) top--; }
            else if (edges.HasFlag(RectEdges.Bottom)) { if (!OnScreenLine(bottom, false)) bottom++; }
        }
        return PixelRect.FromEdges(left, top, right, bottom);
    }

    /// <summary>The edges a WM_SIZING drag moves, from its WMSZ_* code.</summary>
    private static RectEdges SizingEdges(int wmsz) => wmsz switch
    {
        1 => RectEdges.Left,
        2 => RectEdges.Right,
        3 => RectEdges.Top,
        4 => RectEdges.Top | RectEdges.Left,
        5 => RectEdges.Top | RectEdges.Right,
        6 => RectEdges.Bottom,
        7 => RectEdges.Bottom | RectEdges.Left,
        8 => RectEdges.Bottom | RectEdges.Right,
        _ => RectEdges.None,
    };

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmSysCommand:
            {
                // The low four bits of a system command are reserved by Windows, so the id is masked
                // before it is compared.
                var command = m.WParam.ToInt32() & 0xFFF0;
                if (command == SysCommandFullScreen)
                {
                    ToggleFullScreen();
                    return;
                }
                if (command == SysCommandAlwaysOnTop)
                {
                    // For this session only, as in the built-in client; the connection keeps its setting.
                    AlwaysOnTop = !_alwaysOnTop;
                    EnsureSystemMenu();
                    return;
                }
                if (command == SysCommandSmartSizing)
                {
                    ToggleSmartSizing();
                    return;
                }
                if (command == SysCommandFrame)
                {
                    // The taskbar can open the menu of a minimized window; the state is checked again.
                    ToggleFrame();
                    return;
                }
                if (command == ScMaximize && IsFrameless) return;
                break;
            }

            case WmMoving:
                if (SnapProposedRect(ref m, null)) return;
                break;

            case WmSizing:
                if (SizingEdges(m.WParam.ToInt32()) is var edges && edges != RectEdges.None
                    && SnapProposedRect(ref m, edges)) return;
                break;

            case WmWindowPosChanging when _pendingFrameRect is { } target && m.LParam != IntPtr.Zero:
            {
                // Switching the frame moves and sizes the window more than once on the way; each of
                // those is held to the one rectangle wanted, so the session is resized at most once.
                var pos = Marshal.PtrToStructure<WindowPos>(m.LParam);
                pos.X = target.X;
                pos.Y = target.Y;
                pos.Cx = target.Width;
                pos.Cy = target.Height;
                pos.Flags &= ~(SwpNoMove | SwpNoSize);
                Marshal.StructureToPtr(pos, m.LParam, false);
                break;
            }
        }

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WmInitMenuPopup when ((m.LParam.ToInt64() >> 16) & 0xFFFF) != 0 && _menuEnabled:
                // The window menu is about to show: its entries say what they would do now.
                try { UpdateSystemMenu(m.WParam); } catch { }
                break;

            case WmDpiChanged when _snapDrag is not null:
                // Dragged onto a monitor with other scaling: the snap distance goes with it.
                BeginSnapDrag();
                break;

            case WmEnable:
                OnEnableChanged(m.WParam != IntPtr.Zero);
                break;
        }
    }
}
