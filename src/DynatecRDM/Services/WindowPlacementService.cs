using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>
/// Moves mstsc windows onto the displays the user picked. Everything here runs against a
/// window owned by another process that has just been created, so every step is verified.
/// </summary>
public sealed class WindowPlacementService : IWindowPlacementService
{
    private const int MaxAttempts = 3;
    private const int FirstVerifyDelayMs = 120;
    private const int RetryVerifyDelayMs = 200;

    /// <summary>Slack for the window rect itself.</summary>
    private const int PositionTolerance = 4;

    /// <summary>Slack for the DWM frame bounds, which sit inside the window rect by the drop shadow.</summary>
    private const int FrameTolerance = 16;

    private readonly IMonitorService _monitors;

    public WindowPlacementService(IMonitorService monitors) => _monitors = monitors;

    public async Task ApplyAsync(IntPtr hwnd, DisplaySettings display, CancellationToken ct = default)
    {
        if (hwnd == IntPtr.Zero || display is null || !Win32.IsWindow(hwnd)) return;

        if (display.Placement != WindowPlacementMode.Default &&
            TryBuildTarget(display, out var target, out var maximize))
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) return;
                if (!Win32.IsWindow(hwnd)) return;

                Place(hwnd, target, maximize);

                try
                {
                    await Task.Delay(attempt == 1 ? FirstVerifyDelayMs : RetryVerifyDelayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!Win32.IsWindow(hwnd)) return;

                if (Verify(hwnd, target, maximize))
                {
                    if (attempt > 1)
                        AppLog.Debug_($"Placement {display.Placement} settled on attempt {attempt} for 0x{hwnd.ToInt64():X}.");
                    break;
                }

                if (attempt == MaxAttempts)
                {
                    AppLog.Debug_($"Placement {display.Placement} did not stick after {MaxAttempts} attempts " +
                                  $"for 0x{hwnd.ToInt64():X}; leaving the window where mstsc put it.");
                }
                else
                {
                    AppLog.Debug_($"Placement {display.Placement} attempt {attempt} was ignored by the window manager; retrying.");
                }
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

    /// <summary>Bounding rectangle of every supplied monitor in virtual-desktop coordinates.</summary>
    /// <remarks>Internal rather than public because <c>Win32.RECT</c> is an internal type.</remarks>
    internal static Win32.RECT Union(IEnumerable<MonitorInfo> monitors)
    {
        if (monitors is null) return default;

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        var any = false;

        foreach (var m in monitors)
        {
            any = true;
            if (m.Left < left) left = m.Left;
            if (m.Top < top) top = m.Top;
            if (m.Right > right) right = m.Right;
            if (m.Bottom > bottom) bottom = m.Bottom;
        }

        return any
            ? new Win32.RECT { Left = left, Top = top, Right = right, Bottom = bottom }
            : default;
    }

    private bool TryBuildTarget(DisplaySettings display, out Win32.RECT target, out bool maximize)
    {
        maximize = false;
        target = default;

        switch (display.Placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
            {
                var monitor = _monitors.GetByIndex(display.TargetMonitorIndex);
                target = Bounds(monitor);
                break;
            }

            case WindowPlacementMode.SpecificMonitorMaximized:
            {
                var monitor = _monitors.GetByIndex(display.TargetMonitorIndex);
                target = WorkArea(monitor);
                maximize = true;
                break;
            }

            case WindowPlacementMode.SpanAllMonitors:
            {
                target = Union(_monitors.GetMonitors());
                break;
            }

            case WindowPlacementMode.SelectedMonitors:
            {
                target = Union(ResolveSelected(display.SelectedMonitors));
                break;
            }

            case WindowPlacementMode.CustomRectangle:
            {
                if (display.CustomWidth > 0 && display.CustomHeight > 0)
                {
                    target = new Win32.RECT
                    {
                        Left = display.CustomLeft,
                        Top = display.CustomTop,
                        Right = display.CustomLeft + display.CustomWidth,
                        Bottom = display.CustomTop + display.CustomHeight,
                    };
                }
                else
                {
                    AppLog.Warn($"Custom rectangle {display.CustomWidth}x{display.CustomHeight} is not usable; " +
                                "falling back to the primary work area.");
                    target = WorkArea(PrimaryMonitor());
                }
                break;
            }

            default:
                return false;
        }

        if (target.Width <= 0 || target.Height <= 0)
        {
            AppLog.Warn($"Placement {display.Placement} produced an empty rectangle; leaving the window alone.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="DisplaySettings.SelectedMonitors"/> holds mstsc monitor ids - the numbers
    /// "mstsc /l" prints and the .rdp file carries - not positions in our own list, so the ids
    /// are translated back through the very map the .rdp file was written with.
    /// </summary>
    private List<MonitorInfo> ResolveSelected(List<int> mstscIds)
    {
        var all = _monitors.GetMonitors();
        var selected = new List<MonitorInfo>(mstscIds.Count == 0 ? 1 : mstscIds.Count);

        if (all.Count != 0 && mstscIds.Count != 0)
        {
            var ourIndexes = new int[all.Count];
            for (var i = 0; i < ourIndexes.Length; i++) ourIndexes[i] = i;

            var idForIndex = _monitors.ToMstscIds(ourIndexes);
            var known = Math.Min(idForIndex.Count, all.Count);

            for (var i = 0; i < mstscIds.Count; i++)
            {
                var id = mstscIds[i];
                for (var j = 0; j < known; j++)
                {
                    if (idForIndex[j] != id) continue;

                    var monitor = all[j];
                    if (!selected.Contains(monitor)) selected.Add(monitor);
                    break;
                }
            }
        }

        if (selected.Count == 0) selected.Add(PrimaryMonitor());
        return selected;
    }

    private MonitorInfo PrimaryMonitor()
    {
        var all = _monitors.GetMonitors();
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].IsPrimary) return all[i];
        }

        // Out-of-range indexes resolve to the primary monitor by contract.
        return _monitors.GetByIndex(-1);
    }

    private static void Place(IntPtr hwnd, in Win32.RECT target, bool maximize)
    {
        // A maximized or minimized window ignores SetWindowPos, so drop it back to normal first.
        if (Win32.IsZoomed(hwnd) || Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, (int)Win32.SW_RESTORE);

        // mstsc owns its own full-screen chrome: position the window over the monitor instead of
        // stripping WS_CAPTION, which would break the connection bar.
        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.Left, target.Top, target.Width, target.Height,
            (uint)(Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED));

        if (maximize) Win32.ShowWindow(hwnd, (int)Win32.SW_SHOWMAXIMIZED);
    }

    private static bool Verify(IntPtr hwnd, in Win32.RECT target, bool maximize)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;

        if (maximize)
        {
            if (!Win32.IsZoomed(hwnd)) return false;

            // A maximized window overhangs the work area by its borders, so test its centre.
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            return cx >= target.Left && cx < target.Right && cy >= target.Top && cy < target.Bottom;
        }

        if (Matches(rect, target, PositionTolerance)) return true;

        // The visible frame is inset from the window rect by the drop shadow; accept that too.
        return Win32.TryGetExtendedFrameBounds(hwnd, out var frame) && Matches(frame, target, FrameTolerance);
    }

    private static bool Matches(in Win32.RECT actual, in Win32.RECT target, int tolerance) =>
        Math.Abs(actual.Left - target.Left) <= tolerance &&
        Math.Abs(actual.Top - target.Top) <= tolerance &&
        Math.Abs(actual.Right - target.Right) <= tolerance &&
        Math.Abs(actual.Bottom - target.Bottom) <= tolerance;

    private static Win32.RECT Bounds(MonitorInfo m) => new()
    {
        Left = m.Left,
        Top = m.Top,
        Right = m.Left + m.Width,
        Bottom = m.Top + m.Height,
    };

    private static Win32.RECT WorkArea(MonitorInfo m) => new()
    {
        Left = m.WorkLeft,
        Top = m.WorkTop,
        Right = m.WorkLeft + m.WorkWidth,
        Bottom = m.WorkTop + m.WorkHeight,
    };
}
