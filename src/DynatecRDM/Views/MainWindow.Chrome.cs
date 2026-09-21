using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DynatecRDM.Services;
using Point = System.Windows.Point;

namespace DynatecRDM.Views;

/// <summary>
/// The window without a Windows title bar. WindowChrome turns the header strip into the caption;
/// this adds what it leaves out:
///
///   - the window buttons, which are ordinary buttons in the header;
///   - snap layouts on Windows 11, which appear only over something Windows believes is a
///     maximize button - so the header's button answers the hit test as one, and its hover,
///     press and click are handled here because Windows now owns its mouse input;
///   - a maximized window that fits the screen. Windows makes a maximized window larger than the
///     screen by its frame, which a window without a frame would otherwise lose off every edge.
/// </summary>
public partial class MainWindow
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;

    private const double CaptionHeight = 44;

    private bool _maximizePressed;
    private bool _trackingNcLeave;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(ChromeHook);
            FitMaximized();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The window caption could not be set up.", ex);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        FitMaximized();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitMaximized();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    /// <summary>Reached from the keyboard or UI Automation; a mouse click arrives as WM_NCLBUTTONUP.</summary>
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximized();

    /// <summary>The same close as the Windows button, so closing to the tray still applies.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximized()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>
    /// Pulls the content in by the frame Windows adds around a maximized window, and keeps the
    /// whole header draggable when that pushes it down.
    /// </summary>
    private void FitMaximized()
    {
        try
        {
            if (Content is not FrameworkElement root) return;

            var inset = new Thickness(0);
            if (WindowState == WindowState.Maximized)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var dpi = GetDpiForWindow(hwnd);
                    var padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
                    var scale = VisualTreeHelper.GetDpi(this);
                    var x = (GetSystemMetricsForDpi(SmCxFrame, dpi) + padded) / scale.DpiScaleX;
                    var y = (GetSystemMetricsForDpi(SmCyFrame, dpi) + padded) / scale.DpiScaleY;
                    inset = new Thickness(x, y, x, y);
                }
            }

            root.Margin = inset;
            if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = CaptionHeight + inset.Top;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Fitting the maximized window failed: {ex.Message}");
        }
    }

    private IntPtr ChromeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
                break;

            case WmNcMouseMove:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    TrackNcLeave(hwnd);
                    SetMaximizeVisual(_maximizePressed ? "pressed" : "hover");
                }
                else
                {
                    SetMaximizeVisual(null);
                }
                break;

            case WmNcMouseLeave:
                _trackingNcLeave = false;
                _maximizePressed = false;
                SetMaximizeVisual(null);
                break;

            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                // Left to Windows this would draw and track its own classic button.
                handled = true;
                _maximizePressed = true;
                SetMaximizeVisual("pressed");
                return IntPtr.Zero;

            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                if (_maximizePressed)
                {
                    _maximizePressed = false;
                    SetMaximizeVisual(null);
                    ToggleMaximized();
                }
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (MaximizeButton is not { IsVisible: true } button) return false;

        // Screen coordinates, signed: a second monitor to the left is negative.
        var raw = lParam.ToInt64();
        var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));

        try
        {
            var local = button.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaximizeVisual(string? state)
    {
        if (MaximizeButton is { } button && !Equals(button.Tag, state)) button.Tag = state;
    }

    /// <summary>WM_NCMOUSELEAVE only arrives when asked for, and it is what clears the hover.</summary>
    private void TrackNcLeave(IntPtr hwnd)
    {
        if (_trackingNcLeave) return;

        var track = new TrackMouseEventInfo
        {
            Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(),
            Flags = TmeLeave | TmeNonClient,
            Window = hwnd,
        };
        _trackingNcLeave = TrackMouseEvent(ref track);
    }

    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
