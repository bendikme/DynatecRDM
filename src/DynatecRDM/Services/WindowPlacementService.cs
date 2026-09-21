using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Checks that mstsc windows landed where the .rdp file sent them, and puts right the ones that did
/// not. Everything here runs against a window owned by another process that has just been created,
/// so every step is verified - and nothing is touched that is already right.
/// </summary>
public sealed class WindowPlacementService : IWindowPlacementService
{
    private const int MaxAttempts = 3;
    private const int FirstVerifyDelayMs = 120;
    private const int RetryVerifyDelayMs = 200;

    /// <summary>How long a full-screen session gets to fill its monitors by itself.</summary>
    private const int FullScreenSettleMs = 3000;
    private const int FullScreenPollMs = 150;

    /// <summary>Slack for the window rect itself.</summary>
    private const int PositionTolerance = 4;

    /// <summary>Slack for the DWM frame bounds, which sit inside the window rect by the drop shadow.</summary>
    private const int FrameTolerance = 16;

    private readonly IMonitorService _monitors;

    public WindowPlacementService(IMonitorService monitors) => _monitors = monitors;

    public async Task ApplyAsync(IntPtr hwnd, DisplaySettings display, CancellationToken ct = default)
    {
        if (hwnd == IntPtr.Zero || display is null || !Win32.IsWindow(hwnd)) return;

        // "Let Windows decide" means exactly that; the .rdp file has already said everything.
        if (display.Placement != WindowPlacementMode.Default || display.UseAllMonitors)
        {
            var layout = DisplayLayout.Resolve(display, _monitors.GetMonitors());
            try
            {
                if (layout.IsFullScreen) await SettleFullScreenAsync(hwnd, layout, ct).ConfigureAwait(false);
                else await PlaceWindowAsync(hwnd, layout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (display.AlwaysOnTop) SetAlwaysOnTop(hwnd, true);
    }

    public void FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, (int)Win32.SW_RESTORE);
        Win32.ForceForeground(hwnd);
    }

    public void SetAlwaysOnTop(IntPtr hwnd, bool onTop)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        Win32.SetWindowPos(
            hwnd,
            onTop ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
            0, 0, 0, 0,
            (uint)(Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE));
    }

    /// <summary>
    /// A full-screen session goes full screen on the monitors winposstr and selectedmonitors point
    /// at by itself, so it is only watched here. It is never restored or resized into place: that
    /// leaves Remote Desktop believing it is full screen in a window that is not, and then leaving
    /// full screen - and the resolution change that comes with it - no longer works. Only a session
    /// that went full screen on the wrong monitor is moved, as it is, without being restored.
    /// </summary>
    private async Task SettleFullScreenAsync(IntPtr hwnd, DisplayLayout layout, CancellationToken ct)
    {
        if (layout.FullScreenArea is not { } area) return;

        var deadline = Environment.TickCount64 + FullScreenSettleMs;
        while (true)
        {
            if (!Win32.IsWindow(hwnd)) return;
            if (ClientCovers(hwnd, area)) return;
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(FullScreenPollMs, ct).ConfigureAwait(false);
        }

        if (!IsFullScreenSomewhere(hwnd))
        {
            AppLog.Info($"Session window 0x{hwnd.ToInt64():X} did not open full screen; leaving it where it is.");
            return;
        }

        AppLog.Warn($"Session window 0x{hwnd.ToInt64():X} went full screen on the wrong monitor; moving it to " +
                    $"{area.Left},{area.Top} {area.Width}x{area.Height}.");

        Win32.SetWindowPos(
            hwnd, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height,
            (uint)(Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE));

        await Task.Delay(RetryVerifyDelayMs, ct).ConfigureAwait(false);
        if (Win32.IsWindow(hwnd) && !ClientCovers(hwnd, area))
            AppLog.Debug_($"Session window 0x{hwnd.ToInt64():X} did not take the move; leaving it where mstsc put it.");
    }

    /// <summary>
    /// A maximized window or one at a set rectangle. Checked first and left alone when it is already
    /// right. A maximized window is restored, given its restored rectangle and maximized again, so
    /// that un-maximizing it later lands on the same monitor at the chosen size.
    /// </summary>
    private static async Task PlaceWindowAsync(IntPtr hwnd, DisplayLayout layout, CancellationToken ct)
    {
        bool maximize;
        if (layout.Kind == DisplayLayoutKind.MaximizedWindow && layout.Monitor is not null) maximize = true;
        else if (layout.Kind == DisplayLayoutKind.WindowAtRectangle) maximize = false;
        else return;

        var target = layout.WindowRect;
        var work = layout.Monitor?.WorkArea ?? target;

        for (var attempt = 0; attempt <= MaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested || !Win32.IsWindow(hwnd)) return;

            if (IsPlaced(hwnd, target, work, maximize))
            {
                if (attempt > 1)
                    AppLog.Debug_($"Placement {layout.Kind} settled on attempt {attempt} for 0x{hwnd.ToInt64():X}.");
                return;
            }

            if (attempt == MaxAttempts)
            {
                AppLog.Debug_($"Placement {layout.Kind} did not stick after {MaxAttempts} attempts " +
                              $"for 0x{hwnd.ToInt64():X}; leaving the window where mstsc put it.");
                return;
            }

            if (attempt > 0)
                AppLog.Debug_($"Placement {layout.Kind} attempt {attempt} was ignored by the window manager; retrying.");

            Place(hwnd, target, maximize);
            await Task.Delay(attempt == 0 ? FirstVerifyDelayMs : RetryVerifyDelayMs, ct).ConfigureAwait(false);
        }
    }

    private static void Place(IntPtr hwnd, PixelRect target, bool maximize)
    {
        // A maximized or minimized window ignores SetWindowPos, so drop it back to normal first.
        if (Win32.IsZoomed(hwnd) || Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, (int)Win32.SW_RESTORE);

        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.Left, target.Top, target.Width, target.Height,
            (uint)(Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE));

        if (maximize) Win32.ShowWindow(hwnd, (int)Win32.SW_SHOWMAXIMIZED);
    }

    private static bool IsPlaced(IntPtr hwnd, PixelRect target, PixelRect work, bool maximize)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;

        if (maximize)
        {
            if (!Win32.IsZoomed(hwnd)) return false;

            // A maximized window overhangs the work area by its borders, so test its centre.
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            return cx >= work.Left && cx < work.Right && cy >= work.Top && cy < work.Bottom;
        }

        if (Win32.IsZoomed(hwnd) || Win32.IsIconic(hwnd)) return false;
        if (Matches(rect, target, PositionTolerance)) return true;

        // The visible frame is inset from the window rect by the drop shadow; accept that too.
        return Win32.TryGetExtendedFrameBounds(hwnd, out var frame) && Matches(frame, target, FrameTolerance);
    }

    /// <summary>Full screen means the picture covers the area, taskbar included - a caption never does.</summary>
    private static bool ClientCovers(IntPtr hwnd, PixelRect area)
    {
        if (Win32.IsIconic(hwnd) || !Win32.TryGetClientScreenRect(hwnd, out var client)) return false;
        return client.Left <= area.Left && client.Top <= area.Top
            && client.Right >= area.Right && client.Bottom >= area.Bottom;
    }

    private bool IsFullScreenSomewhere(IntPtr hwnd)
    {
        if (!Win32.TryGetClientScreenRect(hwnd, out var client)) return false;
        var monitor = _monitors.GetMonitorAt(client.Left + client.Width / 2, client.Top + client.Height / 2);
        return ClientCovers(hwnd, monitor.Bounds);
    }

    private static bool Matches(in Win32.RECT actual, PixelRect target, int tolerance) =>
        Math.Abs(actual.Left - target.Left) <= tolerance &&
        Math.Abs(actual.Top - target.Top) <= tolerance &&
        Math.Abs(actual.Right - target.Right) <= tolerance &&
        Math.Abs(actual.Bottom - target.Bottom) <= tolerance;
}
