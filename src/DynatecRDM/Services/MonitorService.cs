using System.Windows.Interop;
using System.Windows.Threading;
using DynatecRDM.Interop;
using DynatecRDM.Models;
using DynatecRDM.Resources;

namespace DynatecRDM.Services;

/// <summary>
/// Publishes the display topology from a cached snapshot and refreshes it when Windows
/// reports that the topology changed. Every read is lock-free.
/// </summary>
public sealed class MonitorService : IMonitorService, IDisposable
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;

    /// <summary>SPI_SETWORKAREA - the only WM_SETTINGCHANGE that can move a work area.</summary>
    private const int SpiSetWorkArea = 0x002F;

    private const int ListenerIdle = 0;
    private const int ListenerOwned = 1;
    private const int ListenerFailed = 2;

    // WM_DISPLAYCHANGE and WM_SETTINGCHANGE are broadcast, and a broadcast never reaches a
    // message-only (HWND_MESSAGE) window - so the watcher has to be a real top-level window.
    // It is a one-pixel, never-shown tool window parked off-screen.
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int OffScreen = -32000;

    private sealed class Snapshot
    {
        public Snapshot(MonitorInfo[] monitors, IntPtr[] handles, int[] mstscIds, MonitorInfo primary)
        {
            Monitors = monitors;
            Handles = handles;
            MstscIds = mstscIds;
            Primary = primary;
        }

        public MonitorInfo[] Monitors { get; }
        public IntPtr[] Handles { get; }
        public int[] MstscIds { get; }
        public MonitorInfo Primary { get; }
    }

    private readonly object _sync = new();

    private Snapshot _snapshot;
    private Dispatcher? _dispatcher;
    private HwndSource? _listener;
    private DispatcherTimer? _debounce;
    private int _listenerState;
    private volatile bool _disposed;

    public event EventHandler? MonitorsChanged;

    public MonitorService()
    {
        _snapshot = Build();
        EnsureListener();
    }

    /// <summary>The primary display. Never null, even when enumeration fails.</summary>
    public MonitorInfo Primary => Volatile.Read(ref _snapshot).Primary;

    public IReadOnlyList<MonitorInfo> GetMonitors(bool refresh = false)
    {
        if (refresh) return Refresh().Monitors;

        EnsureListener();
        return Volatile.Read(ref _snapshot).Monitors;
    }

    public MonitorInfo GetMonitorAt(int x, int y)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        var handle = IntPtr.Zero;
        try
        {
            handle = MonitorApi.MonitorFromPoint(x, y);
        }
        catch (Exception ex)
        {
            AppLog.Warn("MonitorFromPoint failed.", ex);
        }

        if (handle != IntPtr.Zero)
        {
            var handles = snapshot.Handles;
            for (var i = 0; i < handles.Length; i++)
            {
                if (handles[i] == handle) return snapshot.Monitors[i];
            }
        }

        // Handles go stale between a topology change and our refresh - fall back to geometry.
        var monitors = snapshot.Monitors;
        for (var i = 0; i < monitors.Length; i++)
        {
            var m = monitors[i];
            if (x >= m.Left && x < m.Right && y >= m.Top && y < m.Bottom) return m;
        }

        return snapshot.Primary;
    }

    public MonitorInfo GetByIndex(int index)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return (uint)index < (uint)snapshot.Monitors.Length
            ? snapshot.Monitors[index]
            : snapshot.Primary;
    }

    public IReadOnlyList<int> ToMstscIds(IEnumerable<int> indexes)
    {
        if (indexes is null) return Array.Empty<int>();

        var map = Volatile.Read(ref _snapshot).MstscIds;
        var ids = new List<int>(4);

        // Order is preserved on purpose: mstsc treats the first id in "selectedmonitors:s:"
        // as the session's primary display, so the caller's ordering carries meaning.
        foreach (var index in indexes)
        {
            if ((uint)index >= (uint)map.Length) continue;

            var id = map[index];
            if (!ids.Contains(id)) ids.Add(id);
        }

        return ids;
    }

    private Snapshot Refresh()
    {
        Snapshot next;
        bool changed;

        lock (_sync)
        {
            var previous = _snapshot;
            next = Build();
            changed = !SameTopology(previous.Monitors, next.Monitors);
            Volatile.Write(ref _snapshot, next);
        }

        if (changed) RaiseMonitorsChanged();
        return next;
    }

    private static bool SameTopology(MonitorInfo[] a, MonitorInfo[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i])) return false;
        }
        return true;
    }

    private static Snapshot Build()
    {
        IReadOnlyList<MonitorApi.NativeMonitor> native;
        try
        {
            native = MonitorApi.Enumerate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Display enumeration failed; using a synthetic primary monitor.", ex);
            return BuildFallback();
        }

        var count = native.Count;
        if (count == 0) return BuildFallback();

        var monitors = new MonitorInfo[count];
        var handles = new IntPtr[count];
        var primaryIndex = -1;

        for (var i = 0; i < count; i++)
        {
            var n = native[i];
            handles[i] = n.Handle;

            var friendly = string.IsNullOrWhiteSpace(n.FriendlyName) ? n.DeviceName : n.FriendlyName;
            monitors[i] = new MonitorInfo(
                i,
                n.DeviceName,
                friendly,
                n.Bounds.Left,
                n.Bounds.Top,
                n.Bounds.Width,
                n.Bounds.Height,
                n.WorkArea.Left,
                n.WorkArea.Top,
                n.WorkArea.Width,
                n.WorkArea.Height,
                n.IsPrimary,
                n.DpiX == 0 ? 96u : n.DpiX,
                n.DpiY == 0 ? 96u : n.DpiY);

            if (n.IsPrimary && primaryIndex < 0) primaryIndex = i;
        }

        if (primaryIndex < 0) primaryIndex = 0;

        // The id Remote Desktop knows a display by - what "mstsc /l" prints, what selectedmonitors:s:
        // holds, and what the hosted control's "SelectedMonitors" setting takes - is the display's
        // ordinal in the raw EnumDisplayDevices walk. It is NOT its position in this list, which is
        // sorted left to right, and it is not "primary first". Numbering by position here produced
        // wrong ids whenever the primary was not the leftmost display, or an adapter was detached
        // (detached adapters consume an ordinal, so real ids are gapped, e.g. 0, 3, 4).
        var mstscIds = new int[count];
        var missingOrdinal = false;
        for (var i = 0; i < count; i++)
        {
            mstscIds[i] = native[i].AdapterOrdinal;
            if (mstscIds[i] < 0) missingOrdinal = true;
        }

        // Only if the walk told us nothing: fall back to the old positional numbering, which is at
        // least stable, rather than handing out -1.
        if (missingOrdinal)
        {
            AppLog.Warn("Display ordinals were unavailable; falling back to positional monitor ids.");
            mstscIds[primaryIndex] = 0;
            var nextId = 1;
            for (var i = 0; i < count; i++)
            {
                if (i != primaryIndex) mstscIds[i] = nextId++;
            }
        }

        for (var i = 0; i < count; i++) monitors[i] = monitors[i] with { MstscId = mstscIds[i] };

        return new Snapshot(monitors, handles, mstscIds, monitors[primaryIndex]);
    }

    private static Snapshot BuildFallback()
    {
        int width = 1920, height = 1080;
        int workWidth = width, workHeight = height;
        var device = @"\\.\DISPLAY1";

        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is not null && screen.Bounds.Width > 0 && screen.Bounds.Height > 0)
            {
                var bounds = screen.Bounds;
                var work = screen.WorkingArea;
                width = bounds.Width;
                height = bounds.Height;
                workWidth = work.Width > 0 ? work.Width : width;
                workHeight = work.Height > 0 ? work.Height : height;
                device = screen.DeviceName;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Screen fallback failed; assuming a 1920x1080 primary monitor.", ex);
        }

        var info = new MonitorInfo(0, device, Strings.Monitor_PrimaryDisplay, 0, 0, width, height,
            0, 0, workWidth, workHeight, true, 96, 96) { MstscId = 0 };

        return new Snapshot(new[] { info }, new[] { IntPtr.Zero }, new[] { 0 }, info);
    }

    private void EnsureListener()
    {
        if (_disposed || Volatile.Read(ref _listenerState) != ListenerIdle) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher
                         ?? Dispatcher.FromThread(Thread.CurrentThread);

        // No UI thread yet: stay idle and try again on the next call.
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        if (Interlocked.CompareExchange(ref _listenerState, ListenerOwned, ListenerIdle) != ListenerIdle) return;

        _dispatcher = dispatcher;

        if (dispatcher.CheckAccess()) CreateListener();
        else _ = dispatcher.InvokeAsync(CreateListener, DispatcherPriority.Background);
    }

    private void CreateListener()
    {
        if (_disposed) return;

        try
        {
            var parameters = new HwndSourceParameters("DynatecRDM.DisplayWatcher")
            {
                ParentWindow = IntPtr.Zero,
                WindowStyle = WsPopup,
                ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
                PositionX = OffScreen,
                PositionY = OffScreen,
                Width = 1,
                Height = 1,
            };

            var source = new HwndSource(parameters);
            source.AddHook(OnWindowMessage);

            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            timer.Tick += OnDebounceTick;

            _debounce = timer;
            _listener = source;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _listenerState, ListenerFailed);
            AppLog.Warn("Display-change listener unavailable; monitor changes will not raise events.", ex);
            return;
        }

        // Dispose can have run while this was still queued on the dispatcher.
        if (_disposed) TeardownListener();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var interesting = msg == WmDisplayChange ||
                          (msg == WmSettingChange && wParam.ToInt64() == SpiSetWorkArea);

        if (interesting && !_disposed)
        {
            // These arrive in bursts; coalesce them and re-enumerate once things settle.
            var timer = _debounce;
            if (timer is not null)
            {
                timer.Stop();
                timer.Start();
            }
        }

        return IntPtr.Zero;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        if (_disposed) return;

        // Enumeration talks to the display driver and reads friendly names out of the device
        // tree, so it stays off the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                if (!_disposed) Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Monitor refresh failed after a display change.", ex);
            }
        });
    }

    private void RaiseMonitorsChanged()
    {
        var handler = MonitorsChanged;
        if (handler is null) return;

        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted) return;
            _ = dispatcher.InvokeAsync(
                () => MonitorsChanged?.Invoke(this, EventArgs.Empty),
                DispatcherPriority.Background);
            return;
        }

        handler(this, EventArgs.Empty);
    }

    /// <summary>Idempotent: whichever caller wins the exchange does the teardown.</summary>
    private void TeardownListener()
    {
        var source = Interlocked.Exchange(ref _listener, null);
        var timer = Interlocked.Exchange(ref _debounce, null);
        if (source is null && timer is null) return;

        try
        {
            if (timer is not null)
            {
                timer.Stop();
                timer.Tick -= OnDebounceTick;
            }

            if (source is not null)
            {
                source.RemoveHook(OnWindowMessage);
                source.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Display-change listener teardown failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        MonitorsChanged = null;

        // Nothing built yet - CreateListener will see _disposed and tear itself down.
        if (_listener is null && _debounce is null) return;

        // HwndSource has thread affinity, and once the dispatcher is gone so is its window.
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            TeardownListener();
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            try
            {
                dispatcher.Invoke(TeardownListener, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Display-change listener teardown could not be marshalled.", ex);
            }
        }
    }
}
