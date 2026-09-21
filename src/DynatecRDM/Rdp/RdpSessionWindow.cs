using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DynatecRDM.Models;

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
    private ResizeBehavior _resize = ResizeBehavior.FollowWindow;
    private int _desktopScaleFactor = 100;
    private int _deviceScaleFactor = 100;
    private bool _connected;

    private bool _fullScreen;
    private bool _inFullScreenChange;
    private Rectangle _windowedBounds;
    private FormBorderStyle _windowedBorder;
    private FormWindowState _windowedState;
    private Size _lastPushedSize = Size.Empty;
    private bool _alwaysOnTop;
    private bool _wasMinimized;

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

        _resizeSettle = new System.Windows.Forms.Timer { Interval = ResizeSettleMs };
        _resizeSettle.Tick += (_, _) => { _resizeSettle.Stop(); PushResolution(); };

        _host.Connected += (_, _) => { _connected = true; Connected?.Invoke(this, EventArgs.Empty); };
        _host.LoginComplete += (_, _) =>
        {
            SyncControlFullScreenState();
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
        if (IsHandleCreated) BuildSystemMenu();
    }

    /// <summary>
    /// Positions the window before it is shown. A full-screen session gets a borderless frame over
    /// <paramref name="bounds"/>; a windowed one gets a normal frame at it. The remembered windowed
    /// frame is set so a later <see cref="ToggleFullScreen"/> has somewhere to drop back to.
    /// </summary>
    public void PlaceAt(Rectangle bounds, bool fullScreen, Rectangle windowedBounds = default, bool maximized = false)
    {
        StartPosition = FormStartPosition.Manual;

        // Where a full-screen session lands when it is taken out of full screen. The caller works
        // this out from the connection's own layout; only fall back to a centred default when it
        // gave nothing usable.
        _windowedBounds = windowedBounds.Width >= DisplayLayoutMinEdge && windowedBounds.Height >= DisplayLayoutMinEdge
            ? windowedBounds
            : CentredDefault(bounds);

        if (fullScreen)
        {
            _fullScreen = true;
            _windowedBorder = FormBorderStyle.Sizable;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = bounds;
        }
        else
        {
            _fullScreen = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = bounds;
            // A connection asking for a maximised window gets one, rather than a window that merely
            // happens to be large.
            WindowState = maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        ApplyTopMost();
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
        try { TopMost = WindowState != FormWindowState.Minimized && (_fullScreen || _alwaysOnTop); } catch { }
    }

    public void MinimizeSession()
    {
        TopMost = false;
        WindowState = FormWindowState.Minimized;
    }

    public event EventHandler? Connected;
    public event EventHandler? LoginComplete;
    public event EventHandler<RdpDisconnectInfo>? Disconnected;

    /// <summary>
    /// Raised when the remote desktop has actually been asked to change resolution, with the new
    /// size. Moving the window does not raise it - only a real size change does.
    /// </summary>
    public event EventHandler<Size>? RemoteResolutionChanged;

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
        // Kept for every later resize: the scale the connection was configured with has to be sent
        // again each time, or the first resize would silently reset the session to unscaled.
        _desktopScaleFactor = plan.DesktopScaleFactor;
        _deviceScaleFactor = plan.DeviceScaleFactor;
        _lastPushedSize = Size.Empty;
        UnsupportedSettings = RdpControlConfigurator.Configure(
            _host.Control, connection, plan, credential, extraProperties, hideConnectionBar);
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

    /// <summary>Takes the window full screen on the monitor it is currently on.</summary>
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

            var screen = Screen.FromControl(this).Bounds;
            // A minimised window is left alone for the same reason as in LeaveFullScreen: going full
            // screen must not yank it back onto the desktop behind the user's back.
            // Clear any maximised state first: a maximised window ignores an explicit Bounds.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            // Above the taskbar, the way a full-screen remote session is expected to sit.
            ApplyTopMost();
            // The borderless frame has no system menu; it is rebuilt when the frame returns.
            _menuBuilt = false;
        }
        finally { _inFullScreenChange = false; }

        SyncControlFullScreenState();
        RefreshSystemMenu();
        ScheduleResolutionPush();
    }

    /// <summary>Returns the window to the frame it had before it went full screen.</summary>
    public void LeaveFullScreen()
    {
        if (!_fullScreen || _inFullScreenChange) return;
        _inFullScreenChange = true;
        try
        {
            _fullScreen = false;
            ApplyTopMost();   // not simply false: the connection may ask to stay on top anyway
            FormBorderStyle = _windowedBorder == FormBorderStyle.None ? FormBorderStyle.Sizable : _windowedBorder;

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
        RefreshSystemMenu();
        ScheduleResolutionPush();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        ScheduleResolutionPush();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
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
            _resizeSettle.Dispose();
            _host.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ScheduleResolutionPush()
    {
        if (_resize != ResizeBehavior.FollowWindow) return;
        _resizeSettle.Stop();
        _resizeSettle.Start();
    }

    private void PushResolution()
    {
        if (!_connected || WindowState == FormWindowState.Minimized || _resize != ResizeBehavior.FollowWindow) return;

        var client = _host.WinFormsControl.ClientSize;
        if (client.Width < DisplayLayoutMinEdge || client.Height < DisplayLayoutMinEdge) return;

        // Only when the size actually changed. Windows ends a window MOVE with the same message that
        // ends a resize, so without this a plain drag across the desktop would renegotiate the remote
        // desktop at its current size - which costs a full screen redraw and looks like a blink.
        if (client == _lastPushedSize) return;
        _lastPushedSize = client;

        // The scale the connection was configured with, not one derived from the window: the .rdp
        // path sent exactly what the user chose, and deriving it here would quietly override them.
        _host.UpdateResolution(client.Width, client.Height, _desktopScaleFactor, _deviceScaleFactor);
        RemoteResolutionChanged?.Invoke(this, client);
    }

    private const int DisplayLayoutMinEdge = 200;

    // ---------------------------------------------------------------- system menu

    // Below the reserved SC_* range (0xF000+), and its low four bits are zero because Windows
    // reserves them inside a system command's id.
    private const int SysCommandFullScreen = 0xA000;
    private const int WmSysCommand = 0x0112;
    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfByCommand = 0x0000;

    private bool _menuEnabled;
    private bool _menuBuilt;
    private string _menuEnterCaption = "Full screen";
    private string _menuExitCaption = "Exit full screen";

    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenu(IntPtr hMenu, int uPosition, int uFlags, IntPtr uIDNewItem, string lpNewItem);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The frame is rebuilt whenever the border style changes, which takes the added item with
        // it, so the menu is put back each time the handle comes up.
        _menuBuilt = false;
        if (_menuEnabled) BuildSystemMenu();
    }

    private void BuildSystemMenu()
    {
        if (_menuBuilt) return;
        try
        {
            var menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return;
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, SysCommandFullScreen, _fullScreen ? _menuExitCaption : _menuEnterCaption);
            _menuBuilt = true;
        }
        catch { /* the menu is a convenience; the hotkey still works without it */ }
    }

    /// <summary>
    /// Puts the menu entry back, or just re-words it. A full-screen window is borderless and so has
    /// no system menu to add to at all; this is what gets the entry back once the frame returns.
    /// </summary>
    private void RefreshSystemMenu()
    {
        if (!_menuEnabled || !IsHandleCreated) return;
        if (_menuBuilt) UpdateSystemMenuCaption();
        else BuildSystemMenu();
    }

    /// <summary>Keeps the menu entry's wording in step with the window's state.</summary>
    private void UpdateSystemMenuCaption()
    {
        if (!_menuEnabled || !_menuBuilt || !IsHandleCreated) return;
        try
        {
            var menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return;
            ModifyMenu(menu, SysCommandFullScreen, MfByCommand | MfString, SysCommandFullScreen,
                _fullScreen ? _menuExitCaption : _menuEnterCaption);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        // The low four bits of a system command are reserved by Windows, so the id is masked before
        // it is compared.
        if (m.Msg == WmSysCommand && (m.WParam.ToInt32() & 0xFFF0) == SysCommandFullScreen)
        {
            ToggleFullScreen();
            return;
        }
        base.WndProc(ref m);
    }
}
