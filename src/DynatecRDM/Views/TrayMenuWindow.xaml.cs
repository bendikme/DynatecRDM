using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The quick-launch popup. Created once and reused: showing it is a Show() plus a placement
/// pass, with no data access on the way in.
/// </summary>
public partial class TrayMenuWindow : Window
{
    private const double PopupWidth = 420;
    private const double PopupMaxHeight = 620;
    private const double PopupMinHeight = 120;
    private const double Gap = 8;

    private static readonly DoubleAnimation FadeIn = CreateFade();

    private readonly TrayMenuViewModel _viewModel;

    private MonitorInfo? _monitor;
    private int _anchorX;
    private int _anchorY;
    private bool _fromTray;
    private bool _hiding;
    private bool _allowClose;
    private bool _refitQueued;

    /// <summary>Keeps the popup's last picture - as it was when it went away - off the screen.</summary>
    private readonly FirstFrameCloak _cloak;

    public TrayMenuWindow(TrayMenuViewModel vm)
    {
        _viewModel = vm ?? throw new ArgumentNullException(nameof(vm));

        InitializeComponent();
        DataContext = vm;
        _cloak = new FirstFrameCloak(this);

        ReleaseMainWindow();

        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.RowsChanged += OnRowsChanged;

        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        RowsList.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>When the popup last went away, so a click on the tray icon toggles instead of flickering.</summary>
    public long LastHiddenTicks { get; private set; }

    /// <summary>
    /// WPF hands Application.MainWindow to the first window constructed, and when the app starts
    /// in the tray that window is this popup. Other code picks MainWindow as a dialog owner, which
    /// would parent a modal dialog to a window that hides itself the moment it loses focus.
    /// Clearing the slot lets the real main window claim it when it is built.
    /// </summary>
    private void ReleaseMainWindow()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is not null && ReferenceEquals(app.MainWindow, this)) app.MainWindow = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Releasing the main-window slot from the quick-launch menu failed.", ex);
        }
    }

    /// <summary>
    /// Places the popup near the cursor, clamped to the work area of the monitor under it, and
    /// shows it. Sizes and positions are computed in the monitor's own scale so a mixed-DPI desktop
    /// lands the window where the user is looking. Opened <paramref name="fromTray"/>, the anchor is
    /// the tray icon and the popup runs from it towards the right edge of the screen.
    /// </summary>
    public void ShowAt(MonitorInfo monitor, int cursorX, int cursorY, bool fromTray = false)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _monitor = monitor;
        _anchorX = cursorX;
        _anchorY = cursorY;
        _fromTray = fromTray;

        try
        {
            ApplyLogicalPlacement();

            if (!IsVisible)
            {
                // Unseen until it has been drawn where and as it now is - see FirstFrameCloak.
                _cloak.BeforeShow();
                Show();
            }

            // Lay out before the first frame is composed, then correct in physical pixels so a
            // different monitor DPI cannot leave the window half off the screen.
            UpdateLayout();
            ApplyLogicalPlacement();
            ClampToMonitor();

            Activate();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) Win32.ForceForeground(handle);

            FocusSearch();
            Root.BeginAnimation(OpacityProperty, FadeIn);
        }
        catch (Exception ex)
        {
            AppLog.Error("Showing the quick-launch menu failed.", ex);
        }
    }

    /// <summary>Dismisses the popup without destroying it.</summary>
    public void HidePopup()
    {
        if (_hiding || !IsVisible) return;

        _hiding = true;
        try
        {
            Hide();
            _cloak.Hidden();
            LastHiddenTicks = Environment.TickCount64;
            _viewModel.OnHidden();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hiding the quick-launch menu failed.", ex);
        }
        finally
        {
            _hiding = false;
        }
    }

    /// <summary>Really closes the window, used only when the application is shutting down.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        try
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.RowsChanged -= OnRowsChanged;
            _cloak.Hidden();
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the quick-launch menu failed.", ex);
        }
    }

    // ----------------------------------------------------------------- events

    private void OnCloseRequested(object? sender, EventArgs e) => HidePopup();

    private void OnDeactivated(object? sender, EventArgs e) => HidePopup();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HidePopup();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    HidePopup();
                    break;

                case Key.Down:
                    e.Handled = true;
                    _viewModel.MoveSelection(1);
                    break;

                case Key.Up:
                    e.Handled = true;
                    _viewModel.MoveSelection(-1);
                    break;

                case Key.Enter:
                    if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) break;
                    e.Handled = true;
                    _viewModel.ActivateSelected();
                    break;

                // Delete ends the highlighted session, the way it closes a window in Alt+Tab - but
                // only when the search box would do nothing with it, so editing the query still works.
                case Key.Delete when Keyboard.Modifiers == ModifierKeys.None && !SearchWantsDelete():
                    if (_viewModel.DisconnectSelected()) e.Handled = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("The quick-launch menu could not handle that key.", ex);
        }
    }

    /// <summary>True when Delete would remove text from the search box: a selection, or text after the caret.</summary>
    private bool SearchWantsDelete() =>
        SearchBox.IsKeyboardFocusWithin
        && (SearchBox.SelectionLength > 0 || SearchBox.CaretIndex < SearchBox.Text.Length);

    private void OnRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (sender is not ListBoxItem item || item.DataContext is not TrayRow row) return;

            e.Handled = true;
            _viewModel.Activate(row);
        }
        catch (Exception ex)
        {
            AppLog.Error("The quick-launch menu could not handle that click.", ex);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (RowsList.SelectedItem is { } selected) RowsList.ScrollIntoView(selected);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Scrolling the quick-launch selection into view failed.", ex);
        }
    }

    private void OnRowsChanged(object? sender, EventArgs e)
    {
        if (!IsVisible || _refitQueued) return;

        _refitQueued = true;
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _refitQueued = false;
                if (!IsVisible) return;

                try
                {
                    ApplyLogicalPlacement();
                    ClampToMonitor();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Re-fitting the quick-launch menu failed.", ex);
                }
            }));
        }
        catch (Exception ex)
        {
            _refitQueued = false;
            AppLog.Warn("Queueing a quick-launch re-fit failed.", ex);
        }
    }

    // -------------------------------------------------------------- placement

    private void ApplyLogicalPlacement()
    {
        if (_monitor is not { } monitor) return;

        var scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        var workLeft = monitor.WorkLeft / scale;
        var workTop = monitor.WorkTop / scale;
        var workWidth = monitor.WorkWidth / scale;
        var workHeight = monitor.WorkHeight / scale;

        var maxHeight = Math.Max(PopupMinHeight, Math.Min(PopupMaxHeight, workHeight - (2 * Gap)));
        MaxHeight = maxHeight;
        Width = PopupWidth;
        Height = Math.Clamp(MeasureContentHeight(maxHeight), PopupMinHeight, maxHeight);

        Place(
            _anchorX / scale, _anchorY / scale,
            workLeft, workTop, workWidth, workHeight,
            Width, Height, Gap, _fromTray,
            out var left, out var top);

        Left = left;
        Top = top;
    }

    /// <summary>
    /// Final correction in device pixels. WPF converts Left and Top with the DPI the window
    /// currently has, which is the wrong one the moment the popup moves between monitors.
    /// </summary>
    private void ClampToMonitor()
    {
        if (_monitor is not { } monitor) return;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            if (!Win32.GetWindowRect(handle, out var rect)) return;

            double width = rect.Right - rect.Left;
            double height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            var scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;

            Place(
                _anchorX, _anchorY,
                monitor.WorkLeft, monitor.WorkTop, monitor.WorkWidth, monitor.WorkHeight,
                width, height, Gap * scale, _fromTray,
                out var x, out var y);

            var left = (int)Math.Round(x);
            var top = (int)Math.Round(y);
            if (left == rect.Left && top == rect.Top) return;

            Win32.SetWindowPos(
                handle, IntPtr.Zero, left, top, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clamping the quick-launch menu to its monitor failed.", ex);
        }
    }

    /// <summary>
    /// Anchors the popup beside the cursor, on whichever side of it has room, then clamps the
    /// result into the work area. Handles a taskbar on any edge without knowing where it is.
    ///
    /// From the tray the left edge starts at the icon instead and the clamp stops it at the right
    /// edge of the screen, so with the notification area in the corner the popup sits flush in it.
    /// </summary>
    private static void Place(
        double cursorX, double cursorY,
        double workLeft, double workTop, double workWidth, double workHeight,
        double width, double height, double gap, bool fromTray,
        out double x, out double y)
    {
        var workRight = workLeft + workWidth;
        var workBottom = workTop + workHeight;

        if (fromTray) x = cursorX;
        else x = cursorX > workLeft + (workWidth / 2) ? cursorX - width - gap : cursorX + gap;
        y = cursorY > workTop + (workHeight / 2) ? cursorY - height - gap : cursorY + gap;

        var maxX = Math.Max(workLeft, workRight - width);
        var maxY = Math.Max(workTop, workBottom - height);

        x = Math.Clamp(x, workLeft, maxX);
        y = Math.Clamp(y, workTop, maxY);
    }

    private double MeasureContentHeight(double maxHeight)
    {
        try
        {
            Root.Measure(new System.Windows.Size(PopupWidth, maxHeight));
            var height = Root.DesiredSize.Height;
            if (height > 1 && !double.IsNaN(height) && !double.IsInfinity(height))
                return Math.Min(height, maxHeight);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Measuring the quick-launch menu failed.", ex);
        }

        return ActualHeight > 1 ? Math.Min(ActualHeight, maxHeight) : maxHeight;
    }

    private void FocusSearch()
    {
        try
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Focusing the quick-launch search box failed.", ex);
        }
    }

    private static DoubleAnimation CreateFade()
    {
        var animation = new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(90)))
        {
            FillBehavior = FillBehavior.Stop,
        };
        animation.Freeze();
        return animation;
    }
}
