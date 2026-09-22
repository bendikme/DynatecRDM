using DynatecRDM.Interop;
using DynatecRDM.Models;

namespace DynatecRDM.Services;

/// <summary>The concrete shapes a session can take on screen.</summary>
public enum DisplayLayoutKind
{
    /// <summary>Full screen on one monitor, at that monitor's resolution.</summary>
    FullScreen = 0,
    /// <summary>Full screen across several monitors ("use multimon").</summary>
    FullScreenMultiMonitor = 1,
    /// <summary>A window of the requested size in the middle of the primary monitor.</summary>
    Window = 2,
    /// <summary>A maximized window on one monitor.</summary>
    MaximizedWindow = 3,
    /// <summary>A window at an exact rectangle.</summary>
    WindowAtRectangle = 4,
}

/// <summary>
/// What a connection's display settings amount to on this machine's monitors.
///
/// <see cref="DisplaySettings"/> carries fields that only mean something together - a screen mode, a
/// placement, a monitor list, a window size and a rectangle - and several combinations contradict
/// each other. This is the one place that decides what a combination becomes, so the .rdp writer,
/// the post-launch placement and the editor's description can never disagree - with one deliberate
/// exception: a window without a frame, which only the in-app client can open and an .rdp file has
/// no way to describe. Only callers that can make one ask for it (<see cref="Resolve(DisplaySettings,
/// IReadOnlyList{MonitorInfo}?, bool)"/>); everyone else gets the framed window.
///
/// The shape that matters most is full screen on one monitor. Remote Desktop Connection keeps the
/// remote resolution in step with the switch between full screen and a window only when the session
/// starts full screen at the monitor's own resolution - in Microsoft's words, "you must start the
/// desktop session with a Display configuration set to Full Screen". A multimon session, even one
/// with a single selected monitor, instead keeps its full-screen layout when it leaves full screen
/// (the switch that would change that, singlemoninwindowedmode, exists only in the Windows App).
/// So one chosen monitor is never written as multimon: the monitor is picked through winposstr,
/// whose rectangle is also the window the session drops back to.
/// </summary>
public sealed class DisplayLayout
{
    public const int MinDesktopEdge = 200;
    public const int MaxDesktopEdge = 8192;

    /// <summary>A window too big for its monitor's work area opens at this share of it instead.</summary>
    private const double OversizeWindowShare = 0.8;

    /// <summary>The window a full-screen session drops back to when its stored size is really full screen.</summary>
    private const int DefaultRestoreWidth = 1920;
    private const int DefaultRestoreHeight = 1080;

    private DisplayLayout(
        DisplayLayoutKind kind,
        MonitorInfo? monitor,
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<int> mstscIds,
        int desktopWidth,
        int desktopHeight,
        PixelRect windowRect,
        int windowClientWidth,
        int windowClientHeight,
        int showCommand,
        ResizeBehavior resize,
        bool frameless = false)
    {
        Kind = kind;
        Monitor = monitor;
        Monitors = monitors;
        MstscIds = mstscIds;
        DesktopWidth = desktopWidth;
        DesktopHeight = desktopHeight;
        WindowRect = windowRect;
        WindowClientWidth = windowClientWidth;
        WindowClientHeight = windowClientHeight;
        ShowCommand = showCommand;
        Resize = resize;
        IsFrameless = frameless;
    }

    public DisplayLayoutKind Kind { get; }

    public bool IsFullScreen => Kind is DisplayLayoutKind.FullScreen or DisplayLayoutKind.FullScreenMultiMonitor;

    public bool UsesMultimon => Kind == DisplayLayoutKind.FullScreenMultiMonitor;

    /// <summary>The monitor the session opens on - the first of several. Null when none are known.</summary>
    public MonitorInfo? Monitor { get; }

    /// <summary>The monitors a full-screen session covers; otherwise just <see cref="Monitor"/>.</summary>
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>The ids for selectedmonitors:s: in a multimon session. Empty means every monitor.</summary>
    public IReadOnlyList<int> MstscIds { get; }

    /// <summary>The session resolution asked for when connecting.</summary>
    public int DesktopWidth { get; }
    public int DesktopHeight { get; }

    /// <summary>
    /// winposstr's rectangle, in virtual-desktop pixels: where the window sits whenever it is
    /// neither full screen nor maximized. For a full-screen session it also picks the monitor.
    /// </summary>
    public PixelRect WindowRect { get; }

    /// <summary>The inside of <see cref="WindowRect"/>: the remote resolution when it follows the window.</summary>
    public int WindowClientWidth { get; }
    public int WindowClientHeight { get; }

    /// <summary>
    /// winposstr's show command: 3 opens maximized, 1 opens normally. A window without a frame always
    /// opens normally, "maximized" included: it is simply as big as the work area.
    /// </summary>
    public int ShowCommand { get; }

    public ResizeBehavior Resize { get; }

    /// <summary>
    /// The window has no title bar or borders: <see cref="WindowRect"/> is the session, whole, so
    /// windows placed edge to edge meet without a seam. Only ever true for a windowed layout of a
    /// client that can open such a window.
    /// </summary>
    public bool IsFrameless { get; }

    /// <summary>
    /// What can be seen of the window, in virtual-desktop pixels - the edges that line up with a
    /// neighbour. A framed window's rectangle includes resize borders Windows does not draw, so
    /// they are left out; a frameless window is seen whole. Null for full screen, which is the
    /// monitors themselves.
    /// </summary>
    public PixelRect? VisibleRect => Kind switch
    {
        DisplayLayoutKind.FullScreen or DisplayLayoutKind.FullScreenMultiMonitor => null,
        _ when IsFrameless => WindowRect,
        DisplayLayoutKind.MaximizedWindow => Monitor?.WorkArea ?? WindowRect,
        _ => ScreenGeometry.InvisibleFrame(Monitor?.DpiX ?? 96).Deflate(WindowRect),
    };

    /// <summary>What the screen covers when full screen: the bounding box of <see cref="Monitors"/>.</summary>
    public PixelRect? FullScreenArea => Monitors.Count == 0 ? null : ScreenGeometry.BoundsOf(Monitors);

    /// <summary>Smart sizing wins over dynamic resolution, as it always has in the .rdp writer.</summary>
    public static ResizeBehavior ResizeOf(DisplaySettings display) =>
        display.SmartSizing ? ResizeBehavior.Scale
        : display.DynamicResolution ? ResizeBehavior.FollowWindow
        : ResizeBehavior.Fixed;

    public static void ApplyResize(DisplaySettings display, ResizeBehavior resize)
    {
        display.SmartSizing = resize == ResizeBehavior.Scale;
        display.DynamicResolution = resize == ResizeBehavior.FollowWindow;
    }

    /// <summary>
    /// What the settings amount to for a window with a frame - all an .rdp file and the external
    /// client can express, so the .rdp writer and the external placement use this one. A frameless
    /// setting is left out; see the overload.
    /// </summary>
    public static DisplayLayout Resolve(DisplaySettings display, IReadOnlyList<MonitorInfo>? monitors) =>
        Resolve(display, monitors, framelessSupported: false);

    /// <summary>
    /// What the settings amount to. <paramref name="framelessSupported"/> says the caller's client
    /// can open a window with no frame - the in-app one - so a frameless setting is honoured for
    /// windowed layouts. Full screen never has a frame to lose.
    /// </summary>
    public static DisplayLayout Resolve(DisplaySettings display, IReadOnlyList<MonitorInfo>? monitors, bool framelessSupported)
    {
        ArgumentNullException.ThrowIfNull(display);

        IReadOnlyList<MonitorInfo> known = monitors is { Count: > 0 } ? monitors : Array.Empty<MonitorInfo>();
        var resize = ResizeOf(display);
        var primary = PrimaryOf(known);
        var frameless = framelessSupported && display.Frameless;

        switch (display.Placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
                return FullScreenOn(display, resize, ByIndex(known, display.TargetMonitorIndex) ?? primary);

            case WindowPlacementMode.SpecificMonitorMaximized:
                return MaximizedOn(display, resize, ByIndex(known, display.TargetMonitorIndex) ?? primary, frameless);

            case WindowPlacementMode.CustomRectangle:
                return AtRectangle(display, resize, known, primary, frameless);

            case WindowPlacementMode.SpanAllMonitors:
                return AcrossAll(display, resize, known, primary);

            case WindowPlacementMode.SelectedMonitors:
                return AcrossSelected(display, resize, known, primary);

            default:
                if (display.UseAllMonitors) return AcrossAll(display, resize, known, primary);
                return display.ScreenMode == ScreenMode.Windowed
                    ? WindowOn(display, resize, primary, frameless)
                    : FullScreenOn(display, resize, primary);
        }
    }

    /// <summary>The mstsc id of a monitor, by the numbering "mstsc /l" uses.</summary>
    public static int MstscIdOf(IReadOnlyList<MonitorInfo> monitors, MonitorInfo monitor)
    {
        if (monitor.MstscId >= 0) return monitor.MstscId;

        // Not enumerated by MonitorService: apply its rule - the primary is 0, the rest follow in order.
        var next = 1;
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var id = m.IsPrimary ? 0 : next++;
            if (m.Index == monitor.Index) return id;
        }
        return monitor.IsPrimary ? 0 : monitor.Index + 1;
    }

    /// <summary>The monitor an mstsc id refers to, or null when no such display is connected.</summary>
    public static MonitorInfo? MonitorForMstscId(IReadOnlyList<MonitorInfo> monitors, int id)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (MstscIdOf(monitors, monitors[i]) == id) return monitors[i];
        return null;
    }

    public static MonitorInfo? PrimaryOf(IReadOnlyList<MonitorInfo> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].IsPrimary) return monitors[i];
        return monitors.Count > 0 ? monitors[0] : null;
    }

    public static MonitorInfo? ByIndex(IReadOnlyList<MonitorInfo> monitors, int index)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].Index == index) return monitors[i];
        return index >= 0 && index < monitors.Count ? monitors[index] : null;
    }

    // ------------------------------------------------------------------ shapes

    private static DisplayLayout FullScreenOn(DisplaySettings display, ResizeBehavior resize, MonitorInfo? monitor)
    {
        if (monitor is null) return Unplaced(DisplayLayoutKind.FullScreen, display, resize, showCommand: 1);

        // The window the session drops back to when it leaves full screen. A stored size as large
        // as the monitor (many connections keep the monitor's native resolution) is not a window at
        // all - it just fills the screen again - so it falls back to a clearly windowed default.
        var restoreWidth = display.DesktopWidth;
        var restoreHeight = display.DesktopHeight;
        if (restoreWidth <= 0 || restoreHeight <= 0
            || restoreWidth >= monitor.Width || restoreHeight >= monitor.Height)
        {
            restoreWidth = DefaultRestoreWidth;
            restoreHeight = DefaultRestoreHeight;
        }

        var window = CenteredWindow(restoreWidth, restoreHeight, monitor, shrinkOversize: true);
        var client = ClientOf(window, monitor);
        return new DisplayLayout(
            DisplayLayoutKind.FullScreen, monitor, new[] { monitor }, Array.Empty<int>(),
            EvenEdge(monitor.Width, 1920), Edge(monitor.Height, 1080),
            window, client.Width, client.Height, showCommand: 1, resize);
    }

    private static DisplayLayout AcrossAll(
        DisplaySettings display, ResizeBehavior resize, IReadOnlyList<MonitorInfo> known, MonitorInfo? primary)
    {
        // "All" of a single monitor is simply full screen on it, which toggles properly.
        if (known.Count == 1) return FullScreenOn(display, resize, known[0]);
        return MultiMonitor(display, resize, known, primary, Array.Empty<int>());
    }

    private static DisplayLayout AcrossSelected(
        DisplaySettings display, ResizeBehavior resize, IReadOnlyList<MonitorInfo> known, MonitorInfo? primary)
    {
        if (known.Count == 0)
        {
            // Without the display layout a monitor cannot be targeted by position, so the ids go to
            // mstsc as they are.
            var raw = new List<int>(display.SelectedMonitors.Count);
            foreach (var id in display.SelectedMonitors)
                if (id >= 0 && !raw.Contains(id)) raw.Add(id);

            return raw.Count == 0
                ? Unplaced(DisplayLayoutKind.FullScreen, display, resize, showCommand: 1)
                : MultiMonitor(display, resize, Array.Empty<MonitorInfo>(), null, raw);
        }

        var chosen = ChosenMonitors(display, known);

        // Remote Desktop spans only displays that sit next to each other. A set that does not all
        // touch opens on the part of it that holds the first display - the remote session's primary -
        // rather than as a small picture floating in a window across every screen.
        chosen = ScreenGeometry.TouchingGroup(chosen);

        if (chosen.Count < 2) return FullScreenOn(display, resize, chosen.Count == 1 ? chosen[0] : primary);

        // The first id is the remote session's primary display, so the order is kept.
        var ids = new int[chosen.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = MstscIdOf(known, chosen[i]);
        return MultiMonitor(display, resize, chosen, chosen[0], ids);
    }

    /// <summary>The displays a selected-monitors layout names that are connected, in the order chosen.</summary>
    public static List<MonitorInfo> ChosenMonitors(DisplaySettings display, IReadOnlyList<MonitorInfo> monitors)
    {
        var chosen = new List<MonitorInfo>(display.SelectedMonitors.Count);
        foreach (var id in display.SelectedMonitors)
        {
            var monitor = MonitorForMstscId(monitors, id);
            if (monitor is not null && !chosen.Contains(monitor)) chosen.Add(monitor);
        }
        return chosen;
    }

    private static DisplayLayout MultiMonitor(
        DisplaySettings display,
        ResizeBehavior resize,
        IReadOnlyList<MonitorInfo> covered,
        MonitorInfo? first,
        IReadOnlyList<int> ids)
    {
        if (first is null)
        {
            var bare = Unplaced(DisplayLayoutKind.FullScreenMultiMonitor, display, resize, showCommand: 1);
            return new DisplayLayout(
                DisplayLayoutKind.FullScreenMultiMonitor, null, covered, ids,
                bare.DesktopWidth, bare.DesktopHeight, bare.WindowRect,
                bare.WindowClientWidth, bare.WindowClientHeight, 1, resize);
        }

        // mstsc takes a multimon session's resolution from the monitors themselves.
        var window = CenteredWindow(display.DesktopWidth, display.DesktopHeight, first, shrinkOversize: true);
        var client = ClientOf(window, first);
        return new DisplayLayout(
            DisplayLayoutKind.FullScreenMultiMonitor, first, covered, ids,
            EvenEdge(display.DesktopWidth, 1920), Edge(display.DesktopHeight, 1080),
            window, client.Width, client.Height, showCommand: 1, resize);
    }

    private static DisplayLayout WindowOn(DisplaySettings display, ResizeBehavior resize, MonitorInfo? monitor, bool frameless)
    {
        if (monitor is null) return Unplaced(DisplayLayoutKind.Window, display, resize, showCommand: 1, frameless);

        // The size asked for is the remote desktop inside the window, so the frame goes around it.
        var width = EvenEdge(display.DesktopWidth, 1920);
        var height = Edge(display.DesktopHeight, 1080);

        if (frameless)
        {
            // No frame: the window is the session, cut to the work area if it would not fit.
            var bare = Even(CenteredWindow(width, height, monitor, shrinkOversize: false, frameless: true), new[] { monitor });
            return new DisplayLayout(
                DisplayLayoutKind.Window, monitor, new[] { monitor }, Array.Empty<int>(),
                EvenEdge(bare.Width, 1920), Edge(bare.Height, 1080),
                bare, bare.Width, bare.Height, showCommand: 1, resize, frameless: true);
        }

        var window = CenteredWindow(width, height, monitor, shrinkOversize: false);
        var client = ClientOf(window, monitor);
        return new DisplayLayout(
            DisplayLayoutKind.Window, monitor, new[] { monitor }, Array.Empty<int>(),
            width, height, window, client.Width, client.Height, showCommand: 1, resize);
    }

    private static DisplayLayout MaximizedOn(DisplaySettings display, ResizeBehavior resize, MonitorInfo? monitor, bool frameless)
    {
        if (monitor is null)
            return Unplaced(DisplayLayoutKind.MaximizedWindow, display, resize, showCommand: frameless ? 1 : 3, frameless);

        var work = monitor.WorkArea;

        if (frameless)
        {
            // Maximized without a frame is a normal window as big as the work area. A really
            // maximized window without a caption would cover the taskbar as well, and there is no
            // smaller size for it to go back to.
            var area = Even(work, new[] { monitor });
            return new DisplayLayout(
                DisplayLayoutKind.MaximizedWindow, monitor, new[] { monitor }, Array.Empty<int>(),
                EvenEdge(area.Width, 1920), Edge(area.Height, 1080),
                area, area.Width, area.Height, showCommand: 1, resize, frameless: true);
        }

        // A maximized window's borders hang outside the work area; only the caption comes off the client.
        var frame = Win32.GetCaptionedFrameInsets(monitor.DpiX);
        var caption = Math.Max(0, frame.Top - frame.Bottom);

        var restore = CenteredWindow(display.DesktopWidth, display.DesktopHeight, monitor, shrinkOversize: true);
        var client = ClientOf(restore, monitor);
        return new DisplayLayout(
            DisplayLayoutKind.MaximizedWindow, monitor, new[] { monitor }, Array.Empty<int>(),
            EvenEdge(work.Width, 1920), Edge(work.Height - caption, 1080),
            restore, client.Width, client.Height, showCommand: 3, resize);
    }

    private static DisplayLayout AtRectangle(
        DisplaySettings display, ResizeBehavior resize, IReadOnlyList<MonitorInfo> known, MonitorInfo? primary, bool frameless)
    {
        var rect = new PixelRect(
            display.CustomLeft,
            display.CustomTop,
            Edge(display.CustomWidth, 1280),
            Edge(display.CustomHeight, 800));

        var monitor = ScreenGeometry.MostOverlapping(known, rect) ?? primary;

        if (frameless)
        {
            // The rectangle is the session itself - nothing is taken off for a frame.
            var whole = Even(rect, known);
            return new DisplayLayout(
                DisplayLayoutKind.WindowAtRectangle, monitor,
                monitor is null ? Array.Empty<MonitorInfo>() : new[] { monitor }, Array.Empty<int>(),
                EvenEdge(whole.Width, 1280), Edge(whole.Height, 800),
                whole, whole.Width, whole.Height, showCommand: 1, resize, frameless: true);
        }
        var client = monitor is null
            ? (Width: rect.Width, Height: rect.Height)
            : ClientOf(rect, monitor);

        // The rectangle is the whole window, so the session is sized to what shows inside it.
        return new DisplayLayout(
            DisplayLayoutKind.WindowAtRectangle, monitor,
            monitor is null ? Array.Empty<MonitorInfo>() : new[] { monitor }, Array.Empty<int>(),
            EvenEdge(client.Width, 1280), Edge(client.Height, 800),
            rect, client.Width, client.Height, showCommand: 1, resize);
    }

    /// <summary>No monitor is known, e.g. for an exported file: the stored size and an origin window.</summary>
    private static DisplayLayout Unplaced(
        DisplayLayoutKind kind, DisplaySettings display, ResizeBehavior resize, int showCommand, bool frameless = false)
    {
        var width = EvenEdge(display.DesktopWidth, 1920);
        var height = Edge(display.DesktopHeight, 1080);
        return new DisplayLayout(
            kind, null, Array.Empty<MonitorInfo>(), Array.Empty<int>(),
            width, height, new PixelRect(0, 0, width, height), width, height, showCommand, resize, frameless);
    }

    /// <summary>
    /// A window whose inside is the given size, in the middle of <paramref name="monitor"/>'s work
    /// area - the frame counted only when there is one. What a new rectangle starts as.
    /// </summary>
    public static PixelRect CenteredOn(MonitorInfo monitor, int clientWidth, int clientHeight, bool frameless)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var window = CenteredWindow(clientWidth, clientHeight, monitor, shrinkOversize: true, frameless);
        return frameless ? Even(window, new[] { monitor }) : window;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// A window whose inside is <paramref name="clientWidth"/> x <paramref name="clientHeight"/>,
    /// centred in the monitor's work area. One that would not fit is either cut to the work area
    /// or, when <paramref name="shrinkOversize"/> is set, opened at a comfortable share of it - the
    /// right choice for the window a full-screen session drops back to, which should look like one.
    /// </summary>
    private static PixelRect CenteredWindow(
        int clientWidth, int clientHeight, MonitorInfo monitor, bool shrinkOversize, bool frameless = false)
    {
        var frame = frameless ? default : Win32.GetCaptionedFrameInsets(monitor.DpiX);
        var work = monitor.WorkArea;

        var width = Edge(clientWidth, 1920) + frame.Left + frame.Right;
        var height = Edge(clientHeight, 1080) + frame.Top + frame.Bottom;

        if (width > work.Width || height > work.Height)
        {
            if (shrinkOversize)
            {
                width = (int)(work.Width * OversizeWindowShare);
                height = (int)(work.Height * OversizeWindowShare);
            }
            else
            {
                width = Math.Min(width, work.Width);
                height = Math.Min(height, work.Height);
            }
        }

        return new PixelRect(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }

    private static (int Width, int Height) ClientOf(PixelRect window, MonitorInfo monitor)
    {
        var frame = Win32.GetCaptionedFrameInsets(monitor.DpiX);
        return (
            Math.Max(MinDesktopEdge, window.Width - frame.Left - frame.Right),
            Math.Max(MinDesktopEdge, window.Height - frame.Top - frame.Bottom));
    }

    private static int Edge(int value, int fallback)
    {
        if (value <= 0) return fallback;
        return Math.Clamp(value, MinDesktopEdge, MaxDesktopEdge);
    }

    /// <summary>Widths go out even: the display control channel refuses an odd width.</summary>
    private static int EvenEdge(int value, int fallback) => Edge(value, fallback) & ~1;

    /// <summary>
    /// A frameless window has nothing around the session to hide a stray pixel, and the session
    /// only takes even sizes. So an odd size grows by one pixel - over its neighbour - rather than
    /// leaving a line of nothing beside it. It grows on the side that is not a monitor edge: there
    /// is no neighbour there, only the next screen.
    /// </summary>
    private static PixelRect Even(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
        if (((right - left) & 1) != 0)
        {
            if (!OnMonitorEdge(right, top, bottom, vertical: true, monitors)) right++;
            else if (!OnMonitorEdge(left, top, bottom, vertical: true, monitors)) left--;
            else right++;   // a monitor of odd width: nothing better
        }
        if (((bottom - top) & 1) != 0)
        {
            if (!OnMonitorEdge(bottom, left, right, vertical: false, monitors)) bottom++;
            else if (!OnMonitorEdge(top, left, right, vertical: false, monitors)) top--;
            else bottom++;
        }
        return PixelRect.FromEdges(left, top, right, bottom);
    }

    /// <summary>The line is a side of a monitor that lies alongside the span on the other axis.</summary>
    private static bool OnMonitorEdge(int line, int acrossLow, int acrossHigh, bool vertical, IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (var m in monitors)
        {
            var (lo, hi, crossLo, crossHi) = vertical ? (m.Left, m.Right, m.Top, m.Bottom) : (m.Top, m.Bottom, m.Left, m.Right);
            if (acrossLow < crossHi && crossLo < acrossHigh && (line == lo || line == hi)) return true;
        }
        return false;
    }
}

/// <summary>Which edges of a rectangle a drag moves. None moves the whole rectangle.</summary>
[Flags]
public enum RectEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>Space around the part of a rectangle that matters - a window's frame around what is seen.</summary>
public readonly record struct PixelInsets(int Left, int Top, int Right, int Bottom)
{
    public PixelRect Deflate(PixelRect rect) =>
        PixelRect.FromEdges(rect.Left + Left, rect.Top + Top, rect.Right - Right, rect.Bottom - Bottom);

    public PixelRect Inflate(PixelRect rect) =>
        PixelRect.FromEdges(rect.Left - Left, rect.Top - Top, rect.Right + Right, rect.Bottom + Bottom);
}

/// <summary>
/// Keeps a window rectangle on the screens while it is dragged or resized. "On the screens" means
/// every pixel lies on some monitor, which also holds for layouts whose monitors are not the same
/// size - the bounding box of such a layout has holes a window must not fall into.
/// </summary>
public static class ScreenGeometry
{
    public static PixelRect BoundsOf(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return default;

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.Left);
            top = Math.Min(top, m.Top);
            right = Math.Max(right, m.Right);
            bottom = Math.Max(bottom, m.Bottom);
        }
        return PixelRect.FromEdges(left, top, right, bottom);
    }

    /// <summary>True when every pixel of the rectangle is on a monitor. Monitors never overlap, so areas add up.</summary>
    public static bool IsOnScreens(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        if (rect.IsEmpty || monitors.Count == 0) return false;

        long covered = 0;
        foreach (var m in monitors) covered += rect.OverlapArea(m.Bounds);
        return covered >= (long)rect.Width * rect.Height;
    }

    /// <summary>
    /// The displays of <paramref name="monitors"/> joined to the first of them through shared edges -
    /// what Remote Desktop can span as one desktop. Order is kept; the rest are left out.
    /// </summary>
    public static List<MonitorInfo> TouchingGroup(IReadOnlyList<MonitorInfo> monitors)
    {
        var group = new List<MonitorInfo>(monitors.Count);
        if (monitors.Count == 0) return group;

        var reached = new HashSet<MonitorInfo> { monitors[0] };
        var queue = new Queue<MonitorInfo>();
        queue.Enqueue(monitors[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var other in monitors)
            {
                if (reached.Contains(other) || !Touch(current.Bounds, other.Bounds)) continue;
                reached.Add(other);
                queue.Enqueue(other);
            }
        }

        foreach (var m in monitors) if (reached.Contains(m)) group.Add(m);
        return group;
    }

    /// <summary>Two rectangles share a length of edge: side by side or one above the other, not just a corner.</summary>
    private static bool Touch(PixelRect a, PixelRect b)
    {
        var sideBySide = (a.Right == b.Left || b.Right == a.Left) && a.Top < b.Bottom && b.Top < a.Bottom;
        var stacked = (a.Bottom == b.Top || b.Bottom == a.Top) && a.Left < b.Right && b.Left < a.Right;
        return sideBySide || stacked;
    }

    public static MonitorInfo? MostOverlapping(IReadOnlyList<MonitorInfo> monitors, PixelRect rect)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var area = rect.OverlapArea(m.Bounds);
            if (area <= bestArea) continue;
            bestArea = area;
            best = m;
        }
        return best;
    }

    /// <summary>
    /// The part of a normal window's frame that Windows does not draw: the resize borders left,
    /// right and below, each one pixel short of the frame, whose last pixel is the thin border that
    /// shows. There is none at the top - the title bar is seen. Two framed windows whose visible
    /// edges meet therefore overlap by these borders, as Windows' own snapping has them.
    /// </summary>
    public static PixelInsets InvisibleFrame(uint dpi)
    {
        var frame = Win32.GetCaptionedFrameInsets(dpi);
        return new PixelInsets(Math.Max(0, frame.Left - 1), 0, Math.Max(0, frame.Right - 1), Math.Max(0, frame.Bottom - 1));
    }

    /// <summary>
    /// Moves <paramref name="start"/> by the drag offset, snapped to nearby monitor edges - and to the
    /// edges of <paramref name="neighbours"/>, other windows to line up with - and kept on the screens.
    /// Null when no position near the pointer keeps it there; the caller then leaves the rectangle
    /// where it last was.
    ///
    /// Everything is worked out on what shows of the window: <paramref name="insets"/> is the part of
    /// <paramref name="start"/> that does not (a framed window's invisible borders), and neighbours
    /// are given as what shows of them. So visible edges meet with no gap, and invisible borders
    /// never count against staying on the screens.
    /// </summary>
    public static PixelRect? Move(
        PixelRect start, int dx, int dy, IReadOnlyList<MonitorInfo> monitors, int snap,
        IReadOnlyList<PixelRect>? neighbours = null, PixelInsets insets = default)
    {
        var body = insets.Deflate(start);
        return MoveBody(body, dx, dy, monitors, snap, neighbours) is { } moved ? insets.Inflate(moved) : null;
    }

    private static PixelRect? MoveBody(
        PixelRect start, int dx, int dy, IReadOnlyList<MonitorInfo> monitors, int snap, IReadOnlyList<PixelRect>? neighbours)
    {
        var wanted = start.Offset(dx, dy);
        if (snap > 0)
        {
            // Nearness is judged where the rectangle can actually end up: a drag that overshoots the
            // desktop is clamped back below, and must still snap along the edge it is pressed against.
            var judged = monitors.Count > 0 ? ClampInto(wanted, BoundsOf(monitors)) ?? wanted : wanted;
            wanted = SnapMove(judged, monitors, neighbours, snap, Math.Sign(dx), Math.Sign(dy));
        }
        if (monitors.Count == 0) return wanted;

        var bounds = BoundsOf(monitors);
        PixelRect? best = null;
        long bestDistance = long.MaxValue;

        void Consider(PixelRect? candidate)
        {
            if (candidate is not { } c || !IsOnScreens(c, monitors)) return;
            var distance = (long)Math.Abs(c.Left - wanted.Left) + Math.Abs(c.Top - wanted.Top);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            best = c;
        }

        var clamped = ClampInto(wanted, bounds);
        Consider(clamped);
        // Stopping where the drag first runs into a hole in the layout...
        if (clamped is { } inside && IsOnScreens(start, monitors)) Consider(Retreat(start, inside, monitors));
        // ...or sliding along one axis when only the other runs into it.
        Consider(ClampInto(wanted with { Top = start.Top }, bounds));
        Consider(ClampInto(wanted with { Left = start.Left }, bounds));
        foreach (var m in monitors) Consider(ClampInto(wanted, m.Bounds));

        return best;
    }

    /// <summary>
    /// Drags the given edges by the offset, keeping a minimum size, snapping the moving edges to
    /// nearby monitor edges and to <paramref name="neighbours"/>, and keeping the result on the
    /// screens. <paramref name="insets"/> and <paramref name="neighbours"/> work as for
    /// <see cref="Move"/>; the minimum size is the whole window's.
    /// </summary>
    public static PixelRect? Resize(
        PixelRect start, RectEdges edges, int dx, int dy,
        IReadOnlyList<MonitorInfo> monitors, int minWidth, int minHeight, int snap,
        IReadOnlyList<PixelRect>? neighbours = null, PixelInsets insets = default)
    {
        var body = insets.Deflate(start);
        var resized = ResizeBody(
            body, edges, dx, dy, monitors,
            Math.Max(1, minWidth - insets.Left - insets.Right),
            Math.Max(1, minHeight - insets.Top - insets.Bottom),
            snap, neighbours);
        return resized is { } r ? insets.Inflate(r) : null;
    }

    private static PixelRect? ResizeBody(
        PixelRect start, RectEdges edges, int dx, int dy,
        IReadOnlyList<MonitorInfo> monitors, int minWidth, int minHeight, int snap, IReadOnlyList<PixelRect>? neighbours)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        if (snap > 0 && (monitors.Count > 0 || neighbours is { Count: > 0 }))
        {
            // Where the pointer puts the edges, before snapping: both axes judge nearness on this.
            var proposed = PixelRect.FromEdges(
                edges.HasFlag(RectEdges.Left) ? start.Left + dx : start.Left,
                edges.HasFlag(RectEdges.Top) ? start.Top + dy : start.Top,
                edges.HasFlag(RectEdges.Right) ? start.Right + dx : start.Right,
                edges.HasFlag(RectEdges.Bottom) ? start.Bottom + dy : start.Bottom);

            var movesLeft = edges.HasFlag(RectEdges.Left);
            var movesRight = !movesLeft && edges.HasFlag(RectEdges.Right);
            if (movesLeft || movesRight)
            {
                dx += BestShift(proposed.Left, proposed.Right, movesLeft, movesRight, proposed.Top, proposed.Bottom,
                    vertical: true, monitors, neighbours, snap, Math.Sign(dx));
            }

            var movesTop = edges.HasFlag(RectEdges.Top);
            var movesBottom = !movesTop && edges.HasFlag(RectEdges.Bottom);
            if (movesTop || movesBottom)
            {
                dy += BestShift(proposed.Top, proposed.Bottom, movesTop, movesBottom, proposed.Left, proposed.Right,
                    vertical: false, monitors, neighbours, snap, Math.Sign(dy));
            }
        }

        if (edges.HasFlag(RectEdges.Left)) left = Math.Min(start.Left + dx, right - minWidth);
        if (edges.HasFlag(RectEdges.Right)) right = Math.Max(start.Right + dx, left + minWidth);
        if (edges.HasFlag(RectEdges.Top)) top = Math.Min(start.Top + dy, bottom - minHeight);
        if (edges.HasFlag(RectEdges.Bottom)) bottom = Math.Max(start.Bottom + dy, top + minHeight);

        if (monitors.Count == 0) return PixelRect.FromEdges(left, top, right, bottom);

        var bounds = BoundsOf(monitors);
        left = Math.Max(left, bounds.Left);
        top = Math.Max(top, bounds.Top);
        right = Math.Min(right, bounds.Right);
        bottom = Math.Min(bottom, bounds.Bottom);

        var wanted = PixelRect.FromEdges(left, top, right, bottom);
        if (wanted.Width < minWidth || wanted.Height < minHeight) return null;
        if (IsOnScreens(wanted, monitors)) return wanted;

        // A rectangle that started off the screens can only be brought back by moving it.
        if (!IsOnScreens(start, monitors)) return wanted;

        // An irregular layout: pull the moving edges back towards where they started until the
        // rectangle is whole again, trying each axis alone too so an edge can keep sliding.
        PixelRect? best = null;
        long bestDistance = long.MaxValue;
        var candidates = new[]
        {
            wanted,
            PixelRect.FromEdges(wanted.Left, start.Top, wanted.Right, start.Bottom),
            PixelRect.FromEdges(start.Left, wanted.Top, start.Right, wanted.Bottom),
        };

        foreach (var candidate in candidates)
        {
            var reached = Retreat(start, candidate, monitors);
            var distance = EdgeDistance(reached, wanted);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = reached;
        }

        return best;
    }

    /// <summary>
    /// Brings a rectangle fully onto the screens with as little change as possible: moved inside the
    /// layout if that is enough, otherwise fitted into the monitor it mostly lies on.
    /// </summary>
    public static PixelRect FitOnScreens(
        PixelRect rect, IReadOnlyList<MonitorInfo> monitors, int minWidth, int minHeight, PixelInsets insets) =>
        insets.Inflate(FitOnScreens(
            insets.Deflate(rect), monitors,
            Math.Max(1, minWidth - insets.Left - insets.Right),
            Math.Max(1, minHeight - insets.Top - insets.Bottom)));

    public static PixelRect FitOnScreens(PixelRect rect, IReadOnlyList<MonitorInfo> monitors, int minWidth, int minHeight)
    {
        if (monitors.Count == 0 || IsOnScreens(rect, monitors)) return rect;

        if (ClampInto(rect, BoundsOf(monitors)) is { } moved && IsOnScreens(moved, monitors)) return moved;

        var target = MostOverlapping(monitors, rect) ?? Nearest(monitors, rect);
        var area = target.Bounds;
        var width = Math.Clamp(rect.Width, Math.Min(minWidth, area.Width), area.Width);
        var height = Math.Clamp(rect.Height, Math.Min(minHeight, area.Height), area.Height);
        return ClampInto(rect with { Width = width, Height = height }, area) ?? area;
    }

    private static PixelRect? ClampInto(PixelRect rect, PixelRect area)
    {
        if (rect.Width > area.Width || rect.Height > area.Height) return null;
        return rect with
        {
            Left = Math.Clamp(rect.Left, area.Left, area.Right - rect.Width),
            Top = Math.Clamp(rect.Top, area.Top, area.Bottom - rect.Height),
        };
    }

    /// <summary>
    /// Moves a rectangle onto the nearest lines within <paramref name="snap"/>, each axis on its own.
    /// Both axes judge nearness on the rectangle as given, so the order does not matter.
    /// </summary>
    internal static PixelRect SnapMove(
        PixelRect rect, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<PixelRect>? neighbours,
        int snap, int directionX, int directionY)
    {
        var dx = BestShift(rect.Left, rect.Right, true, true, rect.Top, rect.Bottom, vertical: true, monitors, neighbours, snap, directionX);
        var dy = BestShift(rect.Top, rect.Bottom, true, true, rect.Left, rect.Right, vertical: false, monitors, neighbours, snap, directionY);
        return rect.Offset(dx, dy);
    }

    /// <summary>
    /// Snaps the edges a resize is dragging - one per axis at most - onto the nearest lines within
    /// <paramref name="snap"/>. Nothing else is done: for a window Windows is resizing, which keeps
    /// it on the screens itself.
    /// </summary>
    internal static PixelRect SnapEdges(
        PixelRect rect, RectEdges edges, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<PixelRect>? neighbours, int snap)
    {
        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;

        var movesLeft = edges.HasFlag(RectEdges.Left);
        var movesRight = !movesLeft && edges.HasFlag(RectEdges.Right);
        if (movesLeft || movesRight)
        {
            var shift = BestShift(left, right, movesLeft, movesRight, top, bottom, vertical: true, monitors, neighbours, snap, 0);
            if (movesLeft) left += shift;
            else right += shift;
        }

        var movesTop = edges.HasFlag(RectEdges.Top);
        var movesBottom = !movesTop && edges.HasFlag(RectEdges.Bottom);
        if (movesTop || movesBottom)
        {
            var shift = BestShift(top, bottom, movesTop, movesBottom, left, right, vertical: false, monitors, neighbours, snap, 0);
            if (movesTop) top += shift;
            else bottom += shift;
        }

        return PixelRect.FromEdges(left, top, right, bottom);
    }

    /// <summary>What a snap line is; on an equal distance the lower one wins.</summary>
    private enum SnapKind
    {
        /// <summary>A neighbour's far edge: the two windows meet side by side.</summary>
        Seam = 0,
        /// <summary>The edge of a monitor's work area - the taskbar, above all.</summary>
        WorkArea = 1,
        /// <summary>The edge of a monitor.</summary>
        Monitor = 2,
        /// <summary>A neighbour's near edge: stacked windows line up.</summary>
        Aligned = 3,
    }

    /// <summary>
    /// The smallest shift, at most <paramref name="snap"/>, that puts one of the moving edges of a
    /// span on a line - the moving edges being <paramref name="low"/> and/or <paramref name="high"/>
    /// on this axis. Lines count only near the span on the other axis (<paramref name="across"/>):
    /// a monitor or window far above or beside it does not pull it.
    ///
    /// A neighbour offers its far edges to meet side by side whenever the two are near, and its near
    /// edges to line up with only when they are stacked - one above or beside the other, overlapping
    /// by no more than the snap distance - so a window next to another is never pulled on top of it.
    /// On an equal distance a seam beats a taskbar edge, which beats a monitor edge, which beats a
    /// lined-up edge; then the edge leading the drag; then the smaller shift, so the result never
    /// depends on the order things are listed in.
    /// </summary>
    private static int BestShift(
        int low, int high, bool moveLow, bool moveHigh, int acrossLow, int acrossHigh, bool vertical,
        IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<PixelRect>? neighbours, int snap, int direction)
    {
        var found = false;
        var best = 0;
        var bestSize = 0;
        var bestKind = SnapKind.Aligned;
        var bestLeading = false;

        void Offer(int edge, bool isHigh, int line, SnapKind kind)
        {
            var shift = line - edge;
            var size = Math.Abs(shift);
            if (size > snap) return;

            var leading = direction != 0 && (isHigh ? direction > 0 : direction < 0);
            if (found)
            {
                if (size > bestSize) return;
                if (size == bestSize)
                {
                    if (kind > bestKind) return;
                    if (kind == bestKind)
                    {
                        if (bestLeading && !leading) return;
                        if (leading == bestLeading && shift >= best) return;
                    }
                }
            }

            found = true;
            best = shift;
            bestSize = size;
            bestKind = kind;
            bestLeading = leading;
        }

        void OfferArea(PixelRect area, SnapKind kind)
        {
            var (lo, hi, crossLo, crossHi) = vertical
                ? (area.Left, area.Right, area.Top, area.Bottom)
                : (area.Top, area.Bottom, area.Left, area.Right);
            if (!Near(acrossLow, acrossHigh, crossLo, crossHi, snap)) return;

            foreach (var line in new[] { lo, hi })
            {
                if (moveLow) Offer(low, false, line, kind);
                if (moveHigh) Offer(high, true, line, kind);
            }
        }

        foreach (var m in monitors)
        {
            OfferArea(m.Bounds, SnapKind.Monitor);
            OfferArea(m.WorkArea, SnapKind.WorkArea);
        }

        if (neighbours is not null)
        {
            foreach (var n in neighbours)
            {
                if (n.IsEmpty) continue;
                var (lo, hi, crossLo, crossHi) = vertical
                    ? (n.Left, n.Right, n.Top, n.Bottom)
                    : (n.Top, n.Bottom, n.Left, n.Right);
                if (!Near(acrossLow, acrossHigh, crossLo, crossHi, snap)) continue;

                // Side by side: this one's low edge on its high edge, or the other way round.
                if (moveLow) Offer(low, false, hi, SnapKind.Seam);
                if (moveHigh) Offer(high, true, lo, SnapKind.Seam);

                // Stacked: the same edges line up.
                if (!Stacked(acrossLow, acrossHigh, crossLo, crossHi, snap)) continue;
                if (moveLow) Offer(low, false, lo, SnapKind.Aligned);
                if (moveHigh) Offer(high, true, hi, SnapKind.Aligned);
            }
        }

        return found ? best : 0;
    }

    /// <summary>The two spans overlap, or come within <paramref name="snap"/> of each other.</summary>
    private static bool Near(int aLow, int aHigh, int bLow, int bHigh, int snap) =>
        aLow <= bHigh + snap && bLow <= aHigh + snap;

    /// <summary>Near, and one lies beyond the other: they overlap by no more than <paramref name="snap"/>.</summary>
    private static bool Stacked(int aLow, int aHigh, int bLow, int bHigh, int snap) =>
        Near(aLow, aHigh, bLow, bHigh, snap) && (aHigh <= bLow + snap || bHigh <= aLow + snap);

    /// <summary>The furthest point from <paramref name="from"/> towards <paramref name="to"/> that stays on the screens.</summary>
    private static PixelRect Retreat(PixelRect from, PixelRect to, IReadOnlyList<MonitorInfo> monitors)
    {
        if (IsOnScreens(to, monitors)) return to;

        double low = 0, high = 1;
        for (var i = 0; i < 14; i++)
        {
            var mid = (low + high) / 2;
            if (IsOnScreens(Lerp(from, to, mid), monitors)) low = mid;
            else high = mid;
        }
        return Lerp(from, to, low);
    }

    private static PixelRect Lerp(PixelRect from, PixelRect to, double t) => PixelRect.FromEdges(
        from.Left + (int)Math.Round((to.Left - from.Left) * t),
        from.Top + (int)Math.Round((to.Top - from.Top) * t),
        from.Right + (int)Math.Round((to.Right - from.Right) * t),
        from.Bottom + (int)Math.Round((to.Bottom - from.Bottom) * t));

    private static long EdgeDistance(PixelRect a, PixelRect b) =>
        (long)Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) + Math.Abs(a.Right - b.Right) + Math.Abs(a.Bottom - b.Bottom);

    private static MonitorInfo Nearest(IReadOnlyList<MonitorInfo> monitors, PixelRect rect)
    {
        var cx = rect.Left + rect.Width / 2L;
        var cy = rect.Top + rect.Height / 2L;
        var best = monitors[0];
        var bestDistance = long.MaxValue;
        foreach (var m in monitors)
        {
            var dx = Math.Max(0, Math.Max(m.Left - cx, cx - m.Right));
            var dy = Math.Max(0, Math.Max(m.Top - cy, cy - m.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = m;
        }
        return best;
    }
}
