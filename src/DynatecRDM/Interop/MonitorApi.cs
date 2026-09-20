using System.Runtime.InteropServices;

namespace DynatecRDM.Interop;

/// <summary>Physical display enumeration: geometry, friendly names and per-monitor DPI.</summary>
internal static class MonitorApi
{
    public sealed record NativeMonitor(
        IntPtr Handle,
        string DeviceName,
        string FriendlyName,
        Win32.RECT Bounds,
        Win32.RECT WorkArea,
        bool IsPrimary,
        uint DpiX,
        uint DpiY);

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const uint MONITOR_DEFAULTTONEAREST = 0x2;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Win32.RECT rcMonitor;
        public Win32.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Win32.RECT clip, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr MonitorFromPointNative(Win32.POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr MonitorFromWindowNative(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static readonly int MonitorInfoSize = Marshal.SizeOf<MONITORINFOEX>();
    private static readonly int DisplayDeviceSize = Marshal.SizeOf<DISPLAY_DEVICE>();
    private static bool _shcoreMissing;

    /// <summary>
    /// Every attached display, ordered left-to-right then top-to-bottom so an index keeps
    /// meaning between calls and matches how the user reads their desk.
    /// </summary>
    public static IReadOnlyList<NativeMonitor> Enumerate()
    {
        var handles = new List<IntPtr>(4);

        bool Collect(IntPtr hMonitor, IntPtr hdc, ref Win32.RECT clip, IntPtr data)
        {
            try
            {
                handles.Add(hMonitor);
            }
            catch
            {
                // An exception must never unwind through the native enumeration frames.
                return false;
            }
            return true;
        }

        MonitorEnumProc proc = Collect;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        }
        catch
        {
            handles.Clear();
        }
        finally
        {
            GC.KeepAlive(proc);
        }

        if (handles.Count == 0) return new[] { SyntheticPrimary() };

        var adapters = BuildAdapterMap();
        var result = new List<NativeMonitor>(handles.Count);

        for (int i = 0; i < handles.Count; i++)
        {
            IntPtr handle = handles[i];
            var info = new MONITORINFOEX { cbSize = MonitorInfoSize, szDevice = string.Empty };
            if (!GetMonitorInfo(handle, ref info)) continue;

            string deviceName = info.szDevice ?? string.Empty;
            GetDpi(handle, out uint dpiX, out uint dpiY);

            result.Add(new NativeMonitor(
                handle,
                deviceName,
                ResolveFriendlyName(deviceName, adapters),
                info.rcMonitor,
                info.rcWork,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                dpiX,
                dpiY));
        }

        if (result.Count == 0) return new[] { SyntheticPrimary() };

        result.Sort(static (a, b) =>
        {
            int byLeft = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return byLeft != 0 ? byLeft : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

        return EnsurePrimary(result);
    }

    public static IntPtr MonitorFromPoint(int x, int y) =>
        MonitorFromPointNative(new Win32.POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);

    public static IntPtr MonitorFromWindow(IntPtr hWnd) =>
        MonitorFromWindowNative(hWnd, MONITOR_DEFAULTTONEAREST);

    private static void GetDpi(IntPtr handle, out uint dpiX, out uint dpiY)
    {
        if (!_shcoreMissing)
        {
            try
            {
                if (GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out uint x, out uint y) == 0 && x != 0 && y != 0)
                {
                    dpiX = x;
                    dpiY = y;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                _shcoreMissing = true;
            }
            catch (EntryPointNotFoundException)
            {
                _shcoreMissing = true;
            }
        }

        dpiX = 96;
        dpiY = 96;
    }

    /// <summary>Maps an adapter device name (\\.\DISPLAY1) to its adapter description.</summary>
    private static Dictionary<string, string> BuildAdapterMap()
    {
        var map = new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 64; i++)
        {
            var device = NewDisplayDevice();
            if (!EnumDisplayDevices(null, i, ref device, 0)) break;
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            string name = device.DeviceName ?? string.Empty;
            if (name.Length != 0) map[name] = (device.DeviceString ?? string.Empty).Trim();
        }
        return map;
    }

    private static string ResolveFriendlyName(string deviceName, Dictionary<string, string> adapters)
    {
        if (deviceName.Length == 0) return "Display";

        var monitor = NewDisplayDevice();
        if (EnumDisplayDevices(deviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
        {
            string name = (monitor.DeviceString ?? string.Empty).Trim();
            if (name.Length != 0) return name;
        }

        if (adapters.TryGetValue(deviceName, out var adapter) && adapter.Length != 0) return adapter;

        return deviceName;
    }

    private static DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = DisplayDeviceSize,
        DeviceName = string.Empty,
        DeviceString = string.Empty,
        DeviceID = string.Empty,
        DeviceKey = string.Empty,
    };

    /// <summary>Guarantees exactly one monitor is flagged primary so callers can rely on it.</summary>
    private static IReadOnlyList<NativeMonitor> EnsurePrimary(List<NativeMonitor> monitors)
    {
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary) return monitors;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (m.Bounds.Left <= 0 && m.Bounds.Top <= 0 && m.Bounds.Right > 0 && m.Bounds.Bottom > 0)
            {
                monitors[i] = m with { IsPrimary = true };
                return monitors;
            }
        }

        monitors[0] = monitors[0] with { IsPrimary = true };
        return monitors;
    }

    private static NativeMonitor SyntheticPrimary()
    {
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;

        var bounds = new Win32.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        return new NativeMonitor(IntPtr.Zero, @"\\.\DISPLAY1", "Primary display", bounds, bounds, true, 96, 96);
    }
}
