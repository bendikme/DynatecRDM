using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DynatecRDM.Interop;

/// <summary>
/// Thin wrappers over the user32/dwmapi surface the manager needs to find, place and
/// photograph mstsc windows. Nothing here throws: a failed call returns a safe default.
/// </summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOMOVE = 0x2;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const uint SWP_FRAMECHANGED = 0x20;
    public const uint SWP_SHOWWINDOW = 0x40;
    public const uint SWP_NOOWNERZORDER = 0x200;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_CAPTION = 0xC00000;
    public const int WS_THICKFRAME = 0x40000;
    public const int WS_MAXIMIZE = 0x1000000;
    public const int WS_MINIMIZE = 0x20000000;
    public const int WS_VISIBLE = 0x10000000;

    public const uint PW_RENDERFULLCONTENT = 2;

    public const uint MOD_ALT = 1;
    public const uint MOD_CONTROL = 2;
    public const uint MOD_SHIFT = 4;
    public const uint MOD_WIN = 8;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint WM_NULL = 0x0000;
    private const uint WM_CLOSE = 0x0010;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindowsNative(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextNative(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameNative(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attribute, out RECT value, int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [ThreadStatic] private static char[]? _textBuffer;
    [ThreadStatic] private static char[]? _classBuffer;

    private static bool _dpiApiMissing;
    private static readonly int RectSize = Marshal.SizeOf<RECT>();

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 4 ? new IntPtr(GetWindowLong32(hWnd, nIndex)) : GetWindowLongPtr64(hWnd, nIndex);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 4
            ? new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()))
            : SetWindowLongPtr64(hWnd, nIndex, dwNewLong);

    /// <summary>Enumerates top-level windows. Returning false from the callback stops the walk.</summary>
    public static bool EnumWindows(Func<IntPtr, bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Exception? failure = null;
        EnumWindowsProc proc = (hWnd, _) =>
        {
            try
            {
                return callback(hWnd);
            }
            catch (Exception ex)
            {
                // An exception must never unwind through the native enumeration frames.
                failure = ex;
                return false;
            }
        };

        bool result;
        try
        {
            result = EnumWindowsNative(proc, IntPtr.Zero);
        }
        finally
        {
            GC.KeepAlive(proc);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public static string GetWindowText(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;

        var buffer = _textBuffer ??= new char[512];
        int copied = GetWindowTextNative(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, Math.Min(copied, buffer.Length)) : string.Empty;
    }

    public static string GetClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;

        var buffer = _classBuffer ??= new char[260];
        int copied = GetClassNameNative(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, Math.Min(copied, buffer.Length)) : string.Empty;
    }

    /// <summary>True when the window pumps messages within the timeout; a hung mstsc returns false.</summary>
    public static bool IsWindowResponsive(IntPtr hWnd, int timeoutMs)
    {
        if (hWnd == IntPtr.Zero) return false;

        uint timeout = (uint)Math.Clamp(timeoutMs, 1, 60_000);

        // SMTO_BLOCK is deliberately not used: it would freeze the caller for the whole timeout
        // if this ever runs on the UI thread.
        return SendMessageTimeout(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG, timeout, out _) != IntPtr.Zero;
    }

    /// <summary>True for windows the shell keeps hidden, e.g. on another virtual desktop.</summary>
    public static bool IsWindowCloaked(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            return DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The visible frame, without the invisible resize border GetWindowRect includes.</summary>
    public static bool TryGetExtendedFrameBounds(IntPtr hWnd, out RECT rect)
    {
        rect = default;
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            if (DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, RectSize) != 0)
                return false;
            rect = bounds;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static uint GetDpiForWindowSafe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || _dpiApiMissing) return 96;
        try
        {
            uint dpi = GetDpiForWindow(hWnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            _dpiApiMissing = true;
        }
        catch (DllNotFoundException)
        {
            _dpiApiMissing = true;
        }
        return 96;
    }

    /// <summary>
    /// Brings a window to the front even when we do not own the foreground: Windows only grants
    /// SetForegroundWindow to the thread owning input, so we attach to that thread's input queue.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;

        uint thisThread = GetCurrentThreadId();
        uint otherThread = 0;
        bool attached = false;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            else if (!IsWindowVisible(hWnd)) ShowWindow(hWnd, SW_SHOW);

            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != hWnd)
            {
                otherThread = GetWindowThreadProcessId(foreground, out _);
                if (otherThread != 0 && otherThread != thisThread)
                    attached = AttachThreadInput(thisThread, otherThread, true);
            }

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        catch
        {
            // Focus is best effort and must never take a caller down.
        }
        finally
        {
            if (attached)
            {
                try
                {
                    AttachThreadInput(thisThread, otherThread, false);
                }
                catch
                {
                    // Nothing sensible to do when detaching fails.
                }
            }
        }
    }

    public static bool PostCloseMessage(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd) && PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
