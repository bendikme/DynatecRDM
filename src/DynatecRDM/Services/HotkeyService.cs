using System.Windows.Interop;
using System.Windows.Threading;
using DynatecRDM.Interop;

namespace DynatecRDM.Services;

/// <summary>
/// Owns the global quick-launch hotkey. The hotkey hangs off a hidden zero-sized tool window so
/// it keeps working while every real window is hidden in the tray.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const string DefaultGesture = "Ctrl+Alt+R";

    private const int HotkeyId = 0x4452;          // 'DR'
    private const int VkF1 = 0x70;

    private readonly Dispatcher _dispatcher;
    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    public HotkeyService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>Raised on the UI thread every time the gesture is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>The gesture currently held, or null when nothing is registered.</summary>
    public string? CurrentGesture { get; private set; }

    public bool IsRegistered => _registered;

    public bool RegisterDefault() => Register(DefaultGesture);

    /// <summary>
    /// Registers <paramref name="gesture"/>, replacing whatever was registered before. Returns false
    /// when the gesture cannot be parsed or is already owned by another application.
    /// </summary>
    public bool Register(string? gesture)
    {
        if (_disposed) return false;

        if (!TryParse(gesture, out var modifiers, out var virtualKey))
        {
            Unregister();
            if (!string.IsNullOrWhiteSpace(gesture))
                AppLog.Warn($"Hotkey '{gesture}' is not a shortcut we understand.");
            return false;
        }

        return Invoke(() => RegisterCore(gesture!.Trim(), modifiers, virtualKey));
    }

    public void Unregister()
    {
        if (!_registered) return;
        Invoke(() =>
        {
            UnregisterCore();
            return true;
        });
    }

    /// <summary>Returns null when the gesture is usable, otherwise a message fit for the settings UI.</summary>
    public static string? Validate(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return "Enter a shortcut, for example Ctrl+Alt+R.";

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        string? key = null;

        foreach (var part in parts)
        {
            var token = Compact(part);
            if (token.Length == 0) continue;

            var modifier = ParseModifier(token);
            if (modifier != 0)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
                return "Use one key only, plus modifiers.";

            key = token;
            if (!TryParseKey(token, out _))
                return $"'{part.Trim()}' is not a key this shortcut supports.";
        }

        if (key is null) return "Add a key, for example R or F12.";
        if (modifiers == 0) return "Add at least one modifier: Ctrl, Alt, Shift or Win.";
        return null;
    }

    private bool RegisterCore(string gesture, uint modifiers, uint virtualKey)
    {
        UnregisterCore();

        var handle = EnsureSource();
        if (handle == IntPtr.Zero) return false;

        if (!Win32.RegisterHotKey(handle, HotkeyId, modifiers | (uint)Win32.MOD_NOREPEAT, virtualKey))
        {
            AppLog.Warn($"Hotkey '{gesture}' is already taken by another application.");
            return false;
        }

        _registered = true;
        CurrentGesture = gesture;
        AppLog.Info($"Quick-launch hotkey registered: {gesture}");
        return true;
    }

    private void UnregisterCore()
    {
        if (!_registered) return;
        _registered = false;
        CurrentGesture = null;

        var handle = _source?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        try
        {
            Win32.UnregisterHotKey(handle, HotkeyId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Releasing the quick-launch hotkey failed.", ex);
        }
    }

    private IntPtr EnsureSource()
    {
        var existing = _source;
        if (existing is not null && existing.Handle != IntPtr.Zero) return existing.Handle;

        try
        {
            // Deliberately NOT a message-only window (HWND_MESSAGE): the system does not deliver
            // WM_HOTKEY to those, so the hotkey would register successfully and then never fire.
            // A zero-sized tool window is never visible, never appears in the task bar or Alt+Tab,
            // and does receive the message.
            var parameters = new HwndSourceParameters("DynatecRDM.Hotkeys")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW
            };

            var source = new HwndSource(parameters);
            source.AddHook(WndProc);
            _source = source;
            return source.Handle;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not create the hotkey message window.", ex);
            return IntPtr.Zero;
        }
    }

    private void DisposeSource()
    {
        var source = _source;
        _source = null;
        if (source is null) return;

        try
        {
            source.RemoveHook(WndProc);
            source.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Disposing the hotkey message window failed.", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WM_HOTKEY || wParam.ToInt64() != HotkeyId) return IntPtr.Zero;

        handled = true;

        // Raise after the hook returns: a popup menu must never be opened inside a window procedure.
        try
        {
            _dispatcher.BeginInvoke(new Action(RaisePressed), DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Quick-launch hotkey could not be dispatched.", ex);
        }

        return IntPtr.Zero;
    }

    private void RaisePressed()
    {
        try
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick-launch hotkey handler failed.", ex);
        }
    }

    private bool Invoke(Func<bool> action)
    {
        try
        {
            if (_dispatcher.CheckAccess()) return action();
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return false;
            return _dispatcher.Invoke(action, DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hotkey call could not reach the UI thread.", ex);
            return false;
        }
    }

    private static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint mods = 0;
        uint vk = 0;

        foreach (var part in parts)
        {
            var token = Compact(part);
            if (token.Length == 0) continue;

            var modifier = ParseModifier(token);
            if (modifier != 0)
            {
                mods |= modifier;
                continue;
            }

            if (vk != 0) return false;
            if (!TryParseKey(token, out vk)) return false;
        }

        if (vk == 0 || mods == 0) return false;

        modifiers = mods;
        virtualKey = vk;
        return true;
    }

    private static string Compact(string token) =>
        token.IndexOf(' ') < 0 ? token : token.Replace(" ", string.Empty);

    private static uint ParseModifier(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => (uint)Win32.MOD_CONTROL,
        "alt" or "menu" => (uint)Win32.MOD_ALT,
        "shift" or "shft" => (uint)Win32.MOD_SHIFT,
        "win" or "windows" or "meta" or "super" or "cmd" or "lwin" or "rwin" => (uint)Win32.MOD_WIN,
        _ => 0u,
    };

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        if (token.Length <= 3 && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(VkF1 + functionKey - 1);
            return true;
        }

        virtualKey = token.ToLowerInvariant() switch
        {
            "space" or "spacebar" => 0x20u,
            "escape" or "esc" => 0x1Bu,
            "tab" => 0x09u,
            "insert" or "ins" => 0x2Du,
            "delete" or "del" => 0x2Eu,
            "home" => 0x24u,
            "end" => 0x23u,
            "pageup" or "pgup" or "prior" => 0x21u,
            "pagedown" or "pgdn" or "next" => 0x22u,
            "up" or "uparrow" => 0x26u,
            "down" or "downarrow" => 0x28u,
            "left" or "leftarrow" => 0x25u,
            "right" or "rightarrow" => 0x27u,
            "enter" or "return" => 0x0Du,
            "backspace" or "back" => 0x08u,
            _ => 0u,
        };

        return virtualKey != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Invoke(() =>
        {
            UnregisterCore();
            DisposeSource();
            return true;
        });
    }
}
