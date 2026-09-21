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

    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x8000000;

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

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
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_COMMAND = 0x0111;
    private const int SC_MINIMIZE = 0xF020;
    private const uint LWA_ALPHA = 0x2;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    // Toolbar control messages, used to read the connection bar's buttons.
    private const uint TB_GETBUTTON = 0x0417;
    private const uint TB_BUTTONCOUNT = 0x0418;

    // Cross-process reads of the TBBUTTON the connection bar owns.
    private const uint PROCESS_VM_ACCESS = 0x0438; // QUERY_INFORMATION | VM_OPERATION | VM_READ | VM_WRITE
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    // sizeof(TBBUTTON) on x64: iBitmap, idCommand, fsState, fsStyle, 6 bytes padding, dwData, iString.
    private const int TbButtonSize = 32;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>ShowWindow without waiting on the owner: safe on another process's window even if it hangs.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

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

    [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindowsNative(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageRaw(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr address, IntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr address, IntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, int dwStyle,
        [MarshalAs(UnmanagedType.Bool)] bool bMenu, int dwExStyle, uint dpi);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    /// <summary>
    /// The top-level window a possibly-child window belongs to, or the window itself.
    ///
    /// <see cref="WindowFromPoint"/> reports the deepest child under the pointer, which inside a
    /// session hosted in this process is the remote desktop control rather than the window around
    /// it. This is what maps that back to the session's own window.
    /// </summary>
    public static IntPtr GetRootWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var root = GetAncestor(hWnd, GA_ROOT);
            return root == IntPtr.Zero ? hWnd : root;
        }
        catch
        {
            return hWnd;
        }
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

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
    /// How far the outer rectangle of an ordinary captioned window - Remote Desktop's own
    /// WS_OVERLAPPEDWINDOW - reaches past its client area at this DPI, invisible resize borders
    /// included. Each side is returned as a positive thickness.
    /// </summary>
    public static RECT GetCaptionedFrameInsets(uint dpi)
    {
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;

        if (dpi == 0) dpi = 96;
        if (!_dpiApiMissing)
        {
            try
            {
                var probe = new RECT();
                if (AdjustWindowRectExForDpi(ref probe, WS_OVERLAPPEDWINDOW, false, 0, dpi))
                    return new RECT { Left = -probe.Left, Top = -probe.Top, Right = probe.Right, Bottom = probe.Bottom };
            }
            catch (EntryPointNotFoundException)
            {
                _dpiApiMissing = true;
            }
            catch (DllNotFoundException)
            {
                _dpiApiMissing = true;
            }
        }

        // Windows 10 at 100%: 8 px of (mostly invisible) border and a 23 px caption, scaled.
        var border = (int)Math.Round(8 * dpi / 96.0);
        var caption = (int)Math.Round(23 * dpi / 96.0);
        return new RECT { Left = border, Top = border + caption, Right = border, Bottom = border };
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

    /// <summary>Minimises the window the way its own minimise button would, so it can react.</summary>
    public static bool PostMinimize(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && IsWindow(hWnd)
        && PostMessage(hWnd, WM_SYSCOMMAND, new IntPtr(SC_MINIMIZE), IntPtr.Zero);

    /// <summary>The client area in screen coordinates: what the window actually shows.</summary>
    public static bool TryGetClientScreenRect(IntPtr hWnd, out RECT rect)
    {
        rect = default;
        if (hWnd == IntPtr.Zero || !GetClientRect(hWnd, out var client)) return false;

        var origin = new POINT();
        if (!ClientToScreen(hWnd, ref origin)) return false;

        rect = new RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height,
        };
        return true;
    }

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Enumerates a window's immediate and nested children; returning false stops the walk.</summary>
    public static void EnumChildWindows(IntPtr parent, Func<IntPtr, bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (parent == IntPtr.Zero) return;

        Exception? failure = null;
        EnumWindowsProc proc = (hWnd, _) =>
        {
            try { return callback(hWnd); }
            catch (Exception ex) { failure = ex; return false; }
        };

        try { EnumChildWindowsNative(parent, proc, IntPtr.Zero); }
        finally { GC.KeepAlive(proc); }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// Clicks a button on Remote Desktop's connection bar - the toolbar in its BBarWindowClass window
    /// - the way the pointer would. This is how a full-screen session is driven from outside: mstsc
    /// ignores an injected Ctrl+Alt+Break and a WM_SYSCOMMAND for full screen, but the bar's own
    /// Restore button leaves full screen and Minimize minimises, both measured to work even when the
    /// bar is switched off in the .rdp file (the window still exists). The right-hand button cluster
    /// is always minimise, restore, close - index 0, 1 and 2 of the three-button toolbar - so
    /// <paramref name="buttonIndex"/> selects among those. Index 2 (close) is never asked for.
    ///
    /// The command id is read from mstsc's own address space rather than assumed, and the click is a
    /// WM_COMMAND to the bar carrying that id, exactly what the toolbar posts when the button is
    /// pressed.
    /// </summary>
    public static bool TryClickConnectionBarButton(uint processId, int buttonIndex)
    {
        if (processId == 0 || buttonIndex < 0) return false;

        var bar = FindConnectionBar(processId);
        if (bar == IntPtr.Zero) return false;

        var toolbar = FindThreeButtonToolbar(bar);
        if (toolbar == IntPtr.Zero) return false;

        // The button count is read live, so an out-of-range index fails rather than clicking nothing.
        var count = SendMessageRaw(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (buttonIndex >= count) return false;

        if (!TryReadButtonCommand(toolbar, processId, buttonIndex, out var command)) return false;

        SendMessageRaw(bar, WM_COMMAND, new IntPtr(command), toolbar);
        return true;
    }

    private static IntPtr FindConnectionBar(uint processId)
    {
        var found = IntPtr.Zero;
        try
        {
            EnumWindows(hWnd =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != processId) return true;
                if (!string.Equals(GetClassName(hWnd), "BBarWindowClass", StringComparison.Ordinal)) return true;
                found = hWnd;
                return false;
            });
        }
        catch
        {
            return IntPtr.Zero;
        }
        return found;
    }

    private static IntPtr FindThreeButtonToolbar(IntPtr bar)
    {
        var found = IntPtr.Zero;
        try
        {
            EnumChildWindows(bar, child =>
            {
                if (!string.Equals(GetClassName(child), "ToolbarWindow32", StringComparison.Ordinal)) return true;
                if (SendMessageRaw(child, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32() != 3) return true;
                found = child;
                return false;
            });
        }
        catch
        {
            return IntPtr.Zero;
        }
        return found;
    }

    /// <summary>
    /// Reads the command id of a toolbar button. TB_GETBUTTON fills a TBBUTTON in the target
    /// process, so the buffer is allocated there, read back, and freed. idCommand is the second
    /// field, four bytes in.
    /// </summary>
    private static bool TryReadButtonCommand(IntPtr toolbar, uint processId, int index, out int command)
    {
        command = 0;
        var handle = OpenProcess(PROCESS_VM_ACCESS, false, processId);
        if (handle == IntPtr.Zero) return false;

        var remote = IntPtr.Zero;
        try
        {
            remote = VirtualAllocEx(handle, IntPtr.Zero, new IntPtr(TbButtonSize), MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;

            if (SendMessageRaw(toolbar, TB_GETBUTTON, new IntPtr(index), remote) == IntPtr.Zero) return false;

            var buffer = new byte[TbButtonSize];
            if (!ReadProcessMemory(handle, remote, buffer, new IntPtr(TbButtonSize), out _)) return false;

            command = BitConverter.ToInt32(buffer, 4);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(handle, remote, IntPtr.Zero, MEM_RELEASE);
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Makes another program's window invisible and lets clicks fall through it, while leaving it
    /// alive for its owner. Returns the extended style it had, so the change can be undone.
    /// </summary>
    public static bool TryMakeInvisible(IntPtr hWnd, out long previousExStyle)
    {
        previousExStyle = 0;
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;

        try
        {
            previousExStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(previousExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            return SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Undoes <see cref="TryMakeInvisible"/>.</summary>
    public static void RestoreVisibility(IntPtr hWnd, long previousExStyle)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;

        try
        {
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(previousExStyle));
        }
        catch
        {
            // Best effort: the window belongs to someone else and may already be gone.
        }
    }
}
