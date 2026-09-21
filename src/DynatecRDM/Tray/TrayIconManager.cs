using System.IO;
using System.Windows.Threading;
using DynatecRDM.Models;
using DynatecRDM.Resources;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;
using DynatecRDM.Views;

namespace DynatecRDM.Tray;

/// <summary>
/// Owns the notification-area icon, its menu and the quick-launch popup. The popup's view model
/// is created once and refreshed in the background, so opening the popup never waits on the
/// store, the disk or the network.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const string ProductName = AppIdentity.Name;
    private const int TooltipLimit = 63;
    private const int BalloonTitleLimit = 63;
    private const int BalloonTextLimit = 255;
    private const int BalloonMilliseconds = 4000;

    /// <summary>A click that lands within this of a dismissal is the same click, so it toggles.</summary>
    private const long ReopenGuardMs = 250;

    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _closeAllItem;
    private readonly List<(System.Windows.Forms.ToolStripMenuItem Item, Func<string> Text)> _labels = new();

    private System.Drawing.Icon? _icon;
    private bool _ownsIcon;
    private TrayMenuViewModel? _viewModel;
    private TrayMenuWindow? _window;
    private System.Drawing.Point _menuAnchor;
    private bool _disposed;

    public TrayIconManager(AppServices services, IAppShell shell)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        LoadIcon();

        _menu = new System.Windows.Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            RenderMode = System.Windows.Forms.ToolStripRenderMode.Professional,
            Renderer = new TrayMenuRenderer(),
            DropShadowEnabled = false,
            Padding = new System.Windows.Forms.Padding(0, 4, 0, 4),
        };

        AddItem(() => Strings.Tray_OpenManager, () => _shell.ShowMain());
        AddItem(() => Strings.Tray_QuickLaunch, () => ToggleQuickLaunch(_menuAnchor));
        AddItem(() => Strings.Tray_Credentials, () => _shell.ShowCredentials());
        AddItem(() => Strings.Tray_Settings, () => _shell.ShowSettings());
        AddSeparator();
        _closeAllItem = AddItem(() => Strings.Tray_CloseAllSessions, CloseAllSessions);
        AddSeparator();
        AddItem(() => Strings.Tray_Exit, () => _shell.ExitApplication());
        ApplyMenuColours();

        _menu.Opening += OnMenuOpening;

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = Truncate(ProductName, TooltipLimit),
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        _notifyIcon.BalloonTipClicked += OnBalloonClicked;

        _services.Sessions.SessionStarted += OnSessionChanged;
        _services.Sessions.SessionStateChanged += OnSessionChanged;
        _services.Sessions.SessionEnded += OnSessionChanged;

        _viewModel = new TrayMenuViewModel(_services, _shell);
        _ = _viewModel.LoadAsync();

        UpdateTooltip();

        // Build the popup while the app is idle so the first open is as quick as every other one.
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => EnsureWindow()));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The quick-launch menu could not be pre-built.", ex);
        }
    }

    /// <summary>Opens the quick-launch popup at the cursor, or dismisses it when it is already up.</summary>
    public void ShowQuickLaunch() => ToggleQuickLaunch(trayAnchor: null);

    /// <summary>
    /// Opens the popup, or dismisses it when it is already up. <paramref name="trayAnchor"/> is
    /// the tray icon it was opened from; without one it opens beside the cursor, which is where
    /// the global shortcut wants it.
    /// </summary>
    private void ToggleQuickLaunch(System.Drawing.Point? trayAnchor)
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            Post(() => ToggleQuickLaunch(trayAnchor));
            return;
        }

        try
        {
            var window = EnsureWindow();
            if (window is null || _viewModel is null) return;

            if (window.IsVisible)
            {
                window.HidePopup();
                return;
            }

            // Clicking the icon deactivates the popup, which hides it; without this the same
            // click would immediately bring it back.
            if (Environment.TickCount64 - window.LastHiddenTicks < ReopenGuardMs) return;

            OpenQuickLaunch(window, _viewModel, trayAnchor);
        }
        catch (Exception ex)
        {
            AppLog.Error("Opening the quick-launch menu failed.", ex);
        }
    }

    private void OpenQuickLaunch(TrayMenuWindow window, TrayMenuViewModel viewModel, System.Drawing.Point? trayAnchor)
    {
        var anchor = trayAnchor ?? System.Windows.Forms.Cursor.Position;
        var monitor = _services.Monitors.GetMonitorAt(anchor.X, anchor.Y);

        viewModel.PrepareForShow();
        window.ShowAt(monitor, anchor.X, anchor.Y, fromTray: trayAnchor.HasValue);
    }

    /// <summary>
    /// Puts a new UI language on screen. The menu is relabelled in place, which keeps the icon
    /// where it is in the tray; the popup and its view model are built again, because their text
    /// is fixed when they are created.
    /// </summary>
    public void ReloadText()
    {
        if (_disposed) return;

        try
        {
            foreach (var (item, text) in _labels) item.Text = text();
            UpdateTooltip();

            _window?.ForceClose();
            _window = null;
            _viewModel?.Dispose();

            _viewModel = new TrayMenuViewModel(_services, _shell);
            _ = _viewModel.LoadAsync();
            _ = _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => EnsureWindow()));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The tray could not switch language.", ex);
        }
    }

    /// <summary>Shows a balloon from the tray icon.</summary>
    public void Notify(string title, string message, bool isError = false)
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            Post(() => Notify(title, message, isError));
            return;
        }

        try
        {
            var tipTitle = Truncate(string.IsNullOrWhiteSpace(title) ? ProductName : title, BalloonTitleLimit);
            var tipText = Truncate(string.IsNullOrWhiteSpace(message) ? ProductName : message, BalloonTextLimit);

            _notifyIcon.ShowBalloonTip(
                BalloonMilliseconds,
                tipTitle,
                tipText,
                isError ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Showing a tray notification failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _services.Sessions.SessionStarted -= OnSessionChanged;
            _services.Sessions.SessionStateChanged -= OnSessionChanged;
            _services.Sessions.SessionEnded -= OnSessionChanged;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Detaching the tray icon from the session manager failed.", ex);
        }

        try
        {
            _window?.ForceClose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the quick-launch menu failed.", ex);
        }
        finally
        {
            _window = null;
        }

        try
        {
            _viewModel?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disposing the quick-launch view model failed.", ex);
        }
        finally
        {
            _viewModel = null;
        }

        try
        {
            _notifyIcon.MouseClick -= OnMouseClick;
            _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
            _notifyIcon.BalloonTipClicked -= OnBalloonClicked;

            // Hiding before disposing is what stops a dead icon lingering in the tray.
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip = null;
            _notifyIcon.Icon = null;
            _notifyIcon.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Removing the tray icon failed.", ex);
        }

        try
        {
            _menu.Opening -= OnMenuOpening;
            _menu.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disposing the tray menu failed.", ex);
        }

        try
        {
            if (_ownsIcon) _icon?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Releasing the tray icon handle failed.", ex);
        }
        finally
        {
            _icon = null;
        }
    }

    // ------------------------------------------------------------------ icon

    /// <summary>
    /// Packed icon first, then the one baked into the executable, then the system fallback.
    /// A missing icon must never be the reason the application fails to start.
    /// </summary>
    private void LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "dynatec.ico");
            if (File.Exists(path))
            {
                // The size the notification area really draws at this scaling (16 at 100%, 24 at 150%,
                // 32 at 200%), so Windows shows a frame drawn for it instead of stretching the 16.
                _icon = new System.Drawing.Icon(path, System.Windows.Forms.SystemInformation.SmallIconSize);
                _ownsIcon = true;
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The packed tray icon could not be loaded.", ex);
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null)
                {
                    _icon = extracted;
                    _ownsIcon = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The executable's icon could not be extracted.", ex);
        }

        _icon = System.Drawing.SystemIcons.Application;
        _ownsIcon = false;
    }

    // ------------------------------------------------------------------ menu

    /// <summary>The label comes from a function so <see cref="ReloadText"/> can relabel the item.</summary>
    private System.Windows.Forms.ToolStripMenuItem AddItem(Func<string> text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text());

        // Let the menu finish closing before the action opens a window.
        item.Click += (_, _) => Post(action);

        _menu.Items.Add(item);
        _labels.Add((item, text));
        return item;
    }

    private void AddSeparator()
    {
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
    }

    /// <summary>
    /// Colours the menu from the current palette. Run each time it opens, which is the only time
    /// it is drawn, so it is always in the theme Windows is in at that moment.
    /// </summary>
    private void ApplyMenuColours()
    {
        _menu.BackColor = Surface;
        _menu.ForeColor = TextPrimary;
        foreach (System.Windows.Forms.ToolStripItem item in _menu.Items)
        {
            item.BackColor = Surface;
            item.ForeColor = TextPrimary;
        }
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // The menu opens where the icon was right-clicked; the cursor will have moved by the
            // time "Quick launch" is chosen.
            _menuAnchor = System.Windows.Forms.Cursor.Position;
            _closeAllItem.Enabled = ActiveSessionCount() > 0;
            ApplyMenuColours();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Preparing the tray menu failed.", ex);
        }
    }

    private void CloseAllSessions()
    {
        try
        {
            var sessions = _services.Sessions.Sessions;
            foreach (var session in sessions)
            {
                if (!session.IsActive) continue;

                var id = session.Id;
                var name = session.DisplayName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _services.Sessions.CloseAsync(id).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"Closing '{name}' failed.", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing all sessions failed.", ex);
        }
    }

    // ----------------------------------------------------------------- mouse

    private void OnMouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        try
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            ToggleQuickLaunch(System.Windows.Forms.Cursor.Position);
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a tray click failed.", ex);
        }
    }

    /// <summary>
    /// A double click does not open the manager - the popup's button and the tray menu do that. Its
    /// first click opened the popup and its second, by activating the taskbar, hid it again, so all
    /// a double click does is put the popup back.
    /// </summary>
    private void OnMouseDoubleClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        try
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;

            var anchor = System.Windows.Forms.Cursor.Position;
            Post(() =>
            {
                if (_disposed) return;
                var window = EnsureWindow();
                if (window is null || _viewModel is null || window.IsVisible) return;
                OpenQuickLaunch(window, _viewModel, anchor);
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a tray double click failed.", ex);
        }
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        try
        {
            Post(() => _shell.ShowMain());
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a balloon click failed.", ex);
        }
    }

    // -------------------------------------------------------------- plumbing

    private void OnSessionChanged(object? sender, RdpSession e)
    {
        if (_disposed) return;

        if (_dispatcher.CheckAccess()) UpdateTooltip();
        else Post(UpdateTooltip);
    }

    private void UpdateTooltip()
    {
        if (_disposed) return;

        try
        {
            var count = ActiveSessionCount();
            var text = count == 0
                ? ProductName
                : UiLanguage.Format(
                    Strings.Tray_Tooltip,
                    ProductName,
                    UiLanguage.Plural(count, Strings.Tray_Tooltip_Sessions_One, Strings.Tray_Tooltip_Sessions_Many));

            _notifyIcon.Text = Truncate(text, TooltipLimit);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Updating the tray tooltip failed.", ex);
        }
    }

    private int ActiveSessionCount()
    {
        var count = 0;
        foreach (var session in _services.Sessions.Sessions)
        {
            if (session.IsActive) count++;
        }
        return count;
    }

    private TrayMenuWindow? EnsureWindow()
    {
        if (_disposed || _viewModel is null) return null;
        if (_window is not null) return _window;

        try
        {
            _window = new TrayMenuWindow(_viewModel);
        }
        catch (Exception ex)
        {
            AppLog.Error("The quick-launch menu could not be created.", ex);
            _window = null;
        }

        return _window;
    }

    private void Post(Action action)
    {
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AppLog.Error("A tray action failed.", ex);
                }
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("A tray action could not be queued.", ex);
        }
    }

    private static string Truncate(string value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= limit) return value;
        return limit <= 3 ? value[..limit] : string.Concat(value.AsSpan(0, limit - 3), "...");
    }

    // ---------------------------------------------------------------- colours

    // Read from the live palette each time, so the menu follows the theme. The fallbacks are the
    // dark palette, for the moment before ThemeService has started.
    private static System.Drawing.Color Surface => Palette("SurfaceColor", 0x1D, 0x20, 0x26);
    private static System.Drawing.Color SurfaceHover => Palette("SurfaceHoverColor", 0x2A, 0x2F, 0x38);
    private static System.Drawing.Color SurfacePressed => Palette("SurfaceSelectedColor", 0x2A, 0x34, 0x43);
    private static System.Drawing.Color BorderSoft => Palette("BorderSoftColor", 0x2C, 0x31, 0x3A);
    private static System.Drawing.Color BorderStrong => Palette("BorderStrongColor", 0x6B, 0x74, 0x82);
    private static System.Drawing.Color TextPrimary => Palette("TextPrimaryColor", 0xED, 0xEF, 0xF3);
    private static System.Drawing.Color TextMuted => Palette("TextMutedColor", 0x94, 0x9D, 0xAB);

    private static System.Drawing.Color Palette(string key, byte r, byte g, byte b)
    {
        var fallback = System.Windows.Media.Color.FromRgb(r, g, b);
        var colour = ThemeService.Current?.GetColor(key, fallback) ?? fallback;
        return System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B);
    }

    /// <summary>
    /// The professional renderer with the palette's colours. Disabled items would otherwise be
    /// drawn in the system grey, which is too faint to read on the dark surface.
    /// </summary>
    private sealed class TrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextPrimary : TextMuted;
            base.OnRenderItemText(e);
        }
    }

    /// <summary>
    /// A flat palette for the WinForms menu so it does not look alien beside the WPF shell.
    /// Deliberately plain: this menu has to be reliable more than it has to be clever.
    /// </summary>
    private sealed class TrayColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => Surface;

        public override System.Drawing.Color MenuBorder => BorderStrong;

        public override System.Drawing.Color MenuItemBorder => SurfaceHover;

        public override System.Drawing.Color MenuItemSelected => SurfaceHover;

        public override System.Drawing.Color MenuItemSelectedGradientBegin => SurfaceHover;

        public override System.Drawing.Color MenuItemSelectedGradientEnd => SurfaceHover;

        public override System.Drawing.Color MenuItemPressedGradientBegin => SurfacePressed;

        public override System.Drawing.Color MenuItemPressedGradientMiddle => SurfacePressed;

        public override System.Drawing.Color MenuItemPressedGradientEnd => SurfacePressed;

        public override System.Drawing.Color ImageMarginGradientBegin => Surface;

        public override System.Drawing.Color ImageMarginGradientMiddle => Surface;

        public override System.Drawing.Color ImageMarginGradientEnd => Surface;

        public override System.Drawing.Color SeparatorDark => BorderSoft;

        public override System.Drawing.Color SeparatorLight => BorderSoft;
    }
}
