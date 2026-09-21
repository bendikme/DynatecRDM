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
/// the post-launch placement and the editor's description can never disagree.
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
        ResizeBehavior resize)
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

    /// <summary>winposstr's show command: 3 opens maximized, 1 opens normally.</summary>
    public int ShowCommand { get; }

    public ResizeBehavior Resize { get; }

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

    public static DisplayLayout Resolve(DisplaySettings display, IReadOnlyList<MonitorInfo>? monitors)
    {
        ArgumentNullException.ThrowIfNull(display);

        IReadOnlyList<MonitorInfo> known = monitors is { Count: > 0 } ? monitors : Array.Empty<MonitorInfo>();
        var resize = ResizeOf(display);
        var primary = PrimaryOf(known);

        switch (display.Placement)
        {
            case WindowPlacementMode.SpecificMonitorFullscreen:
                return FullScreenOn(display, resize, ByIndex(known, display.TargetMonitorIndex) ?? primary);

            case WindowPlacementMode.SpecificMonitorMaximized:
                return MaximizedOn(display, resize, ByIndex(known, display.TargetMonitorIndex) ?? primary);

            case WindowPlacementMode.CustomRectangle:
                return AtRectangle(display, resize, known, primary);

            case WindowPlacementMode.SpanAllMonitors:
                return AcrossAll(display, resize, known, primary);

            case WindowPlacementMode.SelectedMonitors:
                return AcrossSelected(display, resize, known, primary);

            default:
                if (display.UseAllMonitors) return AcrossAll(display, resize, known, primary);
                return display.ScreenMode == ScreenMode.Windowed
                    ? WindowOn(display, resize, primary)
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

        var chosen = new List<MonitorInfo>(display.SelectedMonitors.Count);
        foreach (var id in display.SelectedMonitors)
        {
            var monitor = MonitorForMstscId(known, id);
            if (monitor is not null && !chosen.Contains(monitor)) chosen.Add(monitor);
        }

        if (chosen.Count < 2) return FullScreenOn(display, resize, chosen.Count == 1 ? chosen[0] : primary);

        // The first id is the remote session's primary display, so the order is kept.
        var ids = new int[chosen.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = MstscIdOf(known, chosen[i]);
        return MultiMonitor(display, resize, chosen, chosen[0], ids);
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

    private static DisplayLayout WindowOn(DisplaySettings display, ResizeBehavior resize, MonitorInfo? monitor)
    {
        if (monitor is null) return Unplaced(DisplayLayoutKind.Window, display, resize, showCommand: 1);

        // The size asked for is the remote desktop inside the window, so the frame goes around it.
        var width = EvenEdge(display.DesktopWidth, 1920);
        var height = Edge(display.DesktopHeight, 1080);
        var window = CenteredWindow(width, height, monitor, shrinkOversize: false);
        var client = ClientOf(window, monitor);
        return new DisplayLayout(
            DisplayLayoutKind.Window, monitor, new[] { monitor }, Array.Empty<int>(),
            width, height, window, client.Width, client.Height, showCommand: 1, resize);
    }

    private static DisplayLayout MaximizedOn(DisplaySettings display, ResizeBehavior resize, MonitorInfo? monitor)
    {
        if (monitor is null) return Unplaced(DisplayLayoutKind.MaximizedWindow, display, resize, showCommand: 3);

        // A maximized window's borders hang outside the work area; only the caption comes off the client.
        var frame = Win32.GetCaptionedFrameInsets(monitor.DpiX);
        var work = monitor.WorkArea;
        var caption = Math.Max(0, frame.Top - frame.Bottom);

        var restore = CenteredWindow(display.DesktopWidth, display.DesktopHeight, monitor, shrinkOversize: true);
        var client = ClientOf(restore, monitor);
        return new DisplayLayout(
            DisplayLayoutKind.MaximizedWindow, monitor, new[] { monitor }, Array.Empty<int>(),
            EvenEdge(work.Width, 1920), Edge(work.Height - caption, 1080),
            restore, client.Width, client.Height, showCommand: 3, resize);
    }

    private static DisplayLayout AtRectangle(
        DisplaySettings display, ResizeBehavior resize, IReadOnlyList<MonitorInfo> known, MonitorInfo? primary)
    {
        var rect = new PixelRect(
            display.CustomLeft,
            display.CustomTop,
            Edge(display.CustomWidth, 1280),
            Edge(display.CustomHeight, 800));

        var monitor = ScreenGeometry.MostOverlapping(known, rect) ?? primary;
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
    private static DisplayLayout Unplaced(DisplayLayoutKind kind, DisplaySettings display, ResizeBehavior resize, int showCommand)
    {
        var width = EvenEdge(display.DesktopWidth, 1920);
        var height = Edge(display.DesktopHeight, 1080);
        return new DisplayLayout(
            kind, null, Array.Empty<MonitorInfo>(), Array.Empty<int>(),
            width, height, new PixelRect(0, 0, width, height), width, height, showCommand, resize);
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// A window whose inside is <paramref name="clientWidth"/> x <paramref name="clientHeight"/>,
    /// centred in the monitor's work area. One that would not fit is either cut to the work area
    /// or, when <paramref name="shrinkOversize"/> is set, opened at a comfortable share of it - the
    /// right choice for the window a full-screen session drops back to, which should look like one.
    /// </summary>
    private static PixelRect CenteredWindow(int clientWidth, int clientHeight, MonitorInfo monitor, bool shrinkOversize)
    {
        var frame = Win32.GetCaptionedFrameInsets(monitor.DpiX);
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
    /// Moves <paramref name="start"/> by the drag offset, snapped to nearby monitor edges and kept on
    /// the screens. Null when no position near the pointer keeps it there; the caller then leaves the
    /// rectangle where it last was.
    /// </summary>
    public static PixelRect? Move(PixelRect start, int dx, int dy, IReadOnlyList<MonitorInfo> monitors, int snap)
    {
        var wanted = start.Offset(dx, dy);
        if (monitors.Count == 0) return wanted;
        if (snap > 0) wanted = SnapMove(wanted, monitors, snap);

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
    /// nearby monitor edges and keeping the result on the screens.
    /// </summary>
    public static PixelRect? Resize(
        PixelRect start, RectEdges edges, int dx, int dy,
        IReadOnlyList<MonitorInfo> monitors, int minWidth, int minHeight, int snap)
    {
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        if (monitors.Count > 0 && snap > 0)
        {
            if (edges.HasFlag(RectEdges.Left)) dx = SnapEdge(start.Left + dx, monitors, vertical: true, snap) - start.Left;
            else if (edges.HasFlag(RectEdges.Right)) dx = SnapEdge(start.Right + dx, monitors, vertical: true, snap) - start.Right;
            if (edges.HasFlag(RectEdges.Top)) dy = SnapEdge(start.Top + dy, monitors, vertical: false, snap) - start.Top;
            else if (edges.HasFlag(RectEdges.Bottom)) dy = SnapEdge(start.Bottom + dy, monitors, vertical: false, snap) - start.Bottom;
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

    private static PixelRect SnapMove(PixelRect rect, IReadOnlyList<MonitorInfo> monitors, int snap)
    {
        var dx = BestShift(rect.Left, rect.Right, monitors, vertical: true, snap);
        var dy = BestShift(rect.Top, rect.Bottom, monitors, vertical: false, snap);
        return rect.Offset(dx, dy);
    }

    /// <summary>
    /// The smallest shift, at most <paramref name="snap"/>, that puts either edge on a monitor edge
    /// or on the edge of its work area - the taskbar being the one people line windows up against.
    /// </summary>
    private static int BestShift(int low, int high, IReadOnlyList<MonitorInfo> monitors, bool vertical, int snap)
    {
        var best = 0;
        var bestSize = snap + 1;

        void Try(int line)
        {
            foreach (var shift in new[] { line - low, line - high })
            {
                var size = Math.Abs(shift);
                if (size >= bestSize) continue;
                bestSize = size;
                best = shift;
            }
        }

        foreach (var m in monitors)
        {
            var work = m.WorkArea;
            if (vertical)
            {
                Try(m.Left);
                Try(m.Right);
                Try(work.Left);
                Try(work.Right);
            }
            else
            {
                Try(m.Top);
                Try(m.Bottom);
                Try(work.Top);
                Try(work.Bottom);
            }
        }
        return best;
    }

    private static int SnapEdge(int edge, IReadOnlyList<MonitorInfo> monitors, bool vertical, int snap) =>
        edge + BestShift(edge, edge, monitors, vertical, snap);

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
