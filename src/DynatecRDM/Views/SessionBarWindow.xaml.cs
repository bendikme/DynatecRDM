using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Services;
using DynatecRDM.ViewModels;

namespace DynatecRDM.Views;

/// <summary>
/// The strip that hangs from the top edge of a full-screen session. It never activates: the
/// remote session keeps the keyboard while the bar is clicked, exactly as with Remote Desktop's
/// own bar. Placement is done in device pixels, because the bar has to sit on the edge of a
/// monitor whose scale can differ from the one the window was last on.
/// </summary>
public partial class SessionBarWindow : Window
{
    private const double SlideInMs = 170;
    private const double SlideOutMs = 130;

    /// <summary>One mouse-wheel notch; touchpads deliver it in smaller pieces.</summary>
    private const int WheelNotch = 120;

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

    private readonly SessionBarViewModel _viewModel;

    private MonitorInfo? _monitor;
    private IntPtr _handle;
    private int _generation;
    private int _wheel;
    private bool _allowClose;

    public SessionBarWindow(SessionBarViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = viewModel;

        ReleaseMainWindow();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
        PreviewMouseWheel += OnPreviewMouseWheel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>True from the moment the bar starts coming in until it has started going away.</summary>
    public bool IsRevealed { get; private set; }

    /// <summary>The bar's rectangle in device pixels, shadow included.</summary>
    internal bool TryGetScreenRect(out Win32.RECT rect)
    {
        rect = default;
        return _handle != IntPtr.Zero && IsVisible && Win32.GetWindowRect(_handle, out rect);
    }

    /// <summary>Brings the bar down from the top edge of <paramref name="monitor"/>.</summary>
    public bool Reveal(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _monitor = monitor;
        _generation++;
        IsRevealed = true;

        try
        {
            var scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
            BarPanel.MaxWidth = Math.Max(1, (monitor.Width / scale) - 32);

            if (!IsVisible)
            {
                ResetToHidden();

                // Roughly right before the first frame, so Windows creates the window on the monitor
                // it is meant for; Place() then puts it on the pixel.
                Left = (monitor.Left + (monitor.Width / 2.0)) / scale - 240;
                Top = monitor.Top / scale;
                Show();
            }

            UpdateLayout();
            Place();
            BringCurrentTabIntoView();
            Animate(0, 1, SlideInMs, EaseOut, onDone: null);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Showing the session bar failed.", ex);
            Conceal(animate: false);
            return false;
        }
    }

    /// <summary>Slides the bar away, or drops it at once when <paramref name="animate"/> is false.</summary>
    public void Conceal(bool animate)
    {
        IsRevealed = false;
        _wheel = 0;
        if (!IsVisible) return;

        var generation = ++_generation;
        try
        {
            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                Hide();
                ResetToHidden();
                return;
            }

            Animate(-HiddenOffset(), 0, SlideOutMs, EaseIn, onDone: () =>
            {
                // A reveal that started while this was running owns the window now.
                if (generation != _generation || IsRevealed) return;
                Hide();
                ResetToHidden();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hiding the session bar failed.", ex);
        }
    }

    /// <summary>
    /// Puts the bar back on top. Activating a session can lift its window into the topmost band,
    /// above a bar that was there first.
    /// </summary>
    public void KeepOnTop()
    {
        if (_handle == IntPtr.Zero || !IsVisible) return;

        Win32.SetWindowPos(
            _handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Really closes the window, used only when the application is shutting down.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        try
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Closing the session bar failed.", ex);
        }
    }

    // ----------------------------------------------------------------- events

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // No activation on click, and no Alt+Tab entry: the bar is part of the session, not a window.
        var style = Win32.GetWindowLongPtr(_handle, Win32.GWL_EXSTYLE).ToInt64();
        style |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLongPtr(_handle, Win32.GWL_EXSTYLE, new IntPtr(style));
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF processes WM_MOUSEACTIVATE itself. The extended style alone is not enough to
        // guarantee that a click keeps the keyboard in the RDP control.
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: deliver the click without activating the bar
        }
        return IntPtr.Zero;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A tab came or went, or the monitor scale changed under the window: stay centred.
        if (IsVisible) Place();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Conceal(animate: false);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        // Reversing direction starts a fresh notch rather than cancelling half of the last one.
        if (Math.Sign(e.Delta) != Math.Sign(_wheel)) _wheel = 0;
        _wheel += e.Delta;

        while (Math.Abs(_wheel) >= WheelNotch)
        {
            var step = _wheel > 0 ? -1 : 1;
            _wheel += step * WheelNotch;
            _viewModel.SwitchBy(step);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionBarViewModel.Current) && IsVisible) BringCurrentTabIntoView();
    }

    // -------------------------------------------------------------- placement

    /// <summary>Centres the bar on the top edge of its monitor, in device pixels.</summary>
    private void Place()
    {
        if (_monitor is not { } monitor || _handle == IntPtr.Zero) return;

        try
        {
            if (!Win32.GetWindowRect(_handle, out var rect) || rect.Width <= 0) return;

            var x = monitor.Left + ((monitor.Width - rect.Width) / 2);
            var y = monitor.Top;

            Win32.SetWindowPos(
                _handle, Win32.HWND_TOPMOST, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Placing the session bar failed.", ex);
        }
    }

    private void BringCurrentTabIntoView()
    {
        try
        {
            var current = _viewModel.Current;
            if (current is null) return;

            foreach (var tab in _viewModel.Tabs)
            {
                if (!ReferenceEquals(tab.Session, current)) continue;
                if (TabList.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement element)
                    element.BringIntoView();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Scrolling the current session tab into view failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- animation

    private void Animate(double slideTo, double opacityTo, double milliseconds, IEasingFunction easing, Action? onDone)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            Root.BeginAnimation(OpacityProperty, null);
            SlideTransform.Y = slideTo;
            Root.Opacity = opacityTo;
            onDone?.Invoke();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));

        // No From: each animation starts wherever the last one left off, so a bar that is called
        // back halfway out simply turns round.
        var slide = new DoubleAnimation(slideTo, duration) { EasingFunction = easing };
        if (onDone is not null) slide.Completed += (_, _) => onDone();

        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(opacityTo, duration), HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetToHidden()
    {
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        Root.BeginAnimation(OpacityProperty, null);
        SlideTransform.Y = -HiddenOffset();
        Root.Opacity = 0;
    }

    private double HiddenOffset() => Math.Max(BarPanel.ActualHeight, 40) + 8;

    /// <summary>
    /// WPF hands Application.MainWindow to the first window constructed. Other code uses it as a
    /// dialog owner, and this window hides itself, so it must never keep the slot.
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
            AppLog.Warn("Releasing the main-window slot from the session bar failed.", ex);
        }
    }

    private static IEasingFunction Freeze(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
