using System.IO;
using System.Windows.Threading;
using DynatecRDM.Models;
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
    private const string ProductName = "DYNATEC Remote Desktop Manager";
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

    private System.Drawing.Icon? _icon;
    private bool _ownsIcon;
    private TrayMenuViewModel? _viewModel;
    private TrayMenuWindow? _window;
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
            Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayColorTable())
            {
                RoundedEdges = false,
            },
            BackColor = Surface,
            ForeColor = TextPrimary,
            DropShadowEnabled = false,
            Padding = new System.Windows.Forms.Padding(0, 4, 0, 4),
        };

        AddItem("Open manager", () => _shell.ShowMain());
        AddItem("Quick launch", ShowQuickLaunch);
        AddItem("Credentials", () => _shell.ShowCredentials());
        AddItem("Settings", () => _shell.ShowSettings());
        AddSeparator();
        _closeAllItem = AddItem("Close all sessions", CloseAllSessions);
        AddSeparator();
        AddItem("Exit", () => _shell.ExitApplication());

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
    public void ShowQuickLaunch()
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            Post(ShowQuickLaunch);
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

            var cursor = System.Windows.Forms.Cursor.Position;
            var monitor = _services.Monitors.GetMonitorAt(cursor.X, cursor.Y);

            _viewModel.PrepareForShow();
            window.ShowAt(monitor, cursor.X, cursor.Y);
        }
        catch (Exception ex)
        {
            AppLog.Error("Opening the quick-launch menu failed.", ex);
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
                _icon = new System.Drawing.Icon(path, new System.Drawing.Size(16, 16));
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

    private System.Windows.Forms.ToolStripMenuItem AddItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text)
        {
            ForeColor = TextPrimary,
            BackColor = Surface,
        };

        // Let the menu finish closing before the action opens a window.
        item.Click += (_, _) => Post(action);

        _menu.Items.Add(item);
        return item;
    }

    private void AddSeparator()
    {
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator
        {
            BackColor = Surface,
            ForeColor = BorderStrong,
        });
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _closeAllItem.Enabled = ActiveSessionCount() > 0;
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
            ShowQuickLaunch();
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a tray click failed.", ex);
        }
    }

    private void OnMouseDoubleClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        try
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;

            _window?.HidePopup();
            Post(() => _shell.ShowMain());
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
            var text = count switch
            {
                0 => ProductName,
                1 => ProductName + " - 1 session",
                _ => $"{ProductName} - {count} sessions",
            };

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

    private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(0x1D, 0x20, 0x26);
    private static readonly System.Drawing.Color SurfaceHover = System.Drawing.Color.FromArgb(0x2A, 0x2F, 0x38);
    private static readonly System.Drawing.Color SurfacePressed = System.Drawing.Color.FromArgb(0x2E, 0x39, 0x47);
    private static readonly System.Drawing.Color BorderStrong = System.Drawing.Color.FromArgb(0x3A, 0x41, 0x4D);
    private static readonly System.Drawing.Color TextPrimary = System.Drawing.Color.FromArgb(0xE9, 0xEC, 0xF1);

    /// <summary>
    /// A flat dark palette for the WinForms menu so it does not look alien beside the WPF shell.
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

        public override System.Drawing.Color SeparatorDark => BorderStrong;

        public override System.Drawing.Color SeparatorLight => BorderStrong;
    }
}
