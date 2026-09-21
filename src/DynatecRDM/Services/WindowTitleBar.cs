using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;

namespace DynatecRDM.Services;

/// <summary>
/// Paints the non-client area (title bar, borders) to match the theme. WPF does not style the
/// title bar, so without this a dark window sits under a white caption, or a light one under a
/// dark caption when Windows itself is dark. ThemeService calls this for every window.
/// </summary>
public static class WindowTitleBar
{
    // Windows 10 2004 and later. On 1809-1909 the same meaning lived at 19.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    /// <summary>
    /// Gives one window a dark or light caption in <paramref name="caption"/>. Safe to call
    /// before the window has a handle (it does nothing) and again whenever the theme changes.
    /// </summary>
    public static void Apply(Window window, bool dark, Color caption)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Dark mode is also what makes Windows draw the caption text and buttons light, so it
            // has to be switched off again for a light theme, not just left alone.
            var mode = dark ? 1 : 0;
            // The attribute id changed between builds; trying the current one first and falling
            // back costs nothing, and a wrong id is simply refused.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref mode, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref mode, sizeof(int));

            // Windows 11 only; refused harmlessly on Windows 10. COLORREF is 0x00BBGGRR.
            var colorRef = (uint)(caption.R | (caption.G << 8) | (caption.B << 16));
            var captionColor = colorRef;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(uint));

            var borderColor = colorRef;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));
        }
        catch (Exception ex)
        {
            // Cosmetic only - a mismatched title bar is never worth failing a window over.
            AppLog.Debug_($"The title bar could not be themed: {ex.Message}");
        }
    }
}
