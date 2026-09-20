using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynatecRDM.Services;

/// <summary>
/// Paints the non-client area (title bar, borders) to match the dark theme. WPF does not style
/// the title bar, so without this a dark window sits under a white caption and looks unfinished.
/// </summary>
public static class DarkTitleBar
{
    // Windows 10 2004 and later. On 1809-1909 the same meaning lived at 19.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    /// <summary>Matches BackgroundBrush in Theme.xaml.</summary>
    private const uint CaptionColorBgr = 0x1D1816; // COLORREF is 0x00BBGGRR, so #16181D reversed.

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    /// <summary>
    /// Applies the dark caption to every window in the application as it loads, including
    /// dialogs opened later by a view model.
    /// </summary>
    public static void ApplyToAllWindows()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) Apply(window);
    }

    /// <summary>Applies the dark caption to one window. Safe to call before or after it is shown.</summary>
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var on = 1;
            // The attribute id changed between builds; trying the current one first and falling
            // back costs nothing, and a wrong id is simply refused.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref on, sizeof(int));

            // Windows 11 only; refused harmlessly on Windows 10.
            var caption = CaptionColorBgr;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));

            var border = CaptionColorBgr;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
        }
        catch (Exception ex)
        {
            // Cosmetic only - a light title bar is never worth failing a window over.
            AppLog.Debug_($"Dark title bar could not be applied: {ex.Message}");
        }
    }
}
