using System.Runtime.InteropServices;

namespace DynatecRDM.Rdp;

/// <summary>One key going down or up, in the form the RDP control's SendKeys takes.</summary>
public readonly record struct RdpKeyEvent(int ScanCode, bool Extended, bool Up)
{
    /// <summary>
    /// The key as the lParam of WM_KEYDOWN/WM_KEYUP, which is what SendKeys expects: a repeat
    /// count of one, the scan code, the extended-key flag, and for a release the previous-state
    /// and transition bits.
    /// </summary>
    public int LParam => 1
        | (ScanCode & 0xFF) << 16
        | (Extended ? 1 << 24 : 0)
        | (Up ? unchecked((int)0xC0000000) : 0);
}

/// <summary>
/// Turns keys and text into the scan codes the RDP control sends. The session receives physical
/// keys, not characters, so text is mapped through a keyboard layout - the one the session was
/// told the client uses. A character the layout cannot type makes the whole text fail rather
/// than send something else.
/// </summary>
public static class RdpKeyboard
{
    public static readonly RdpKeyEvent[] Enter = Tap(0x1C);
    public static readonly RdpKeyEvent[] Delete = Tap(0x53, extended: true);
    public static readonly RdpKeyEvent[] Escape = Tap(0x01);

    private const int ScanLeftShift = 0x2A;
    private const int ScanLeftControl = 0x1D;
    private const int ScanAlt = 0x38;               // right Alt (AltGr) when extended
    private const int ScanLeftWindows = 0x5B;       // extended
    private const int ScanSpace = 0x39;
    private const int ScanR = 0x13;

    /// <summary>Windows+R, which opens the Run box.</summary>
    public static RdpKeyEvent[] WindowsR() =>
    [
        new(ScanLeftWindows, true, false),
        new(ScanR, false, false),
        new(ScanR, false, true),
        new(ScanLeftWindows, true, true),
    ];

    /// <summary>A key pressed and released.</summary>
    public static RdpKeyEvent[] Tap(int scanCode, bool extended = false) =>
        [new(scanCode, extended, false), new(scanCode, extended, true)];

    /// <summary>
    /// The keystrokes that type <paramref name="text"/> on <paramref name="layout"/>, or false with
    /// the first character it has no key for.
    /// </summary>
    public static bool TryType(string text, IntPtr layout, List<RdpKeyEvent> into, out char missing)
    {
        missing = '\0';
        foreach (var c in text)
        {
            var scan = VkKeyScanExW(c, layout);
            if (scan == -1) { missing = c; return false; }

            var vk = (uint)(scan & 0xFF);
            var shift = (scan >> 8) & 0xFF;
            var scanCode = MapVirtualKeyExW(vk, MapVkToVscEx, layout);
            if (scanCode == 0 || (shift & ~0x07) != 0) { missing = c; return false; }

            // Ctrl+Alt together is AltGr, which is the right Alt key on the layouts that have it.
            var altGr = (shift & 0x06) == 0x06;
            var modifiers = new List<RdpKeyEvent>(3);
            if ((shift & 0x01) != 0) modifiers.Add(new(ScanLeftShift, false, false));
            if (altGr) modifiers.Add(new(ScanAlt, true, false));
            else
            {
                if ((shift & 0x02) != 0) modifiers.Add(new(ScanLeftControl, false, false));
                if ((shift & 0x04) != 0) modifiers.Add(new(ScanAlt, false, false));
            }

            into.AddRange(modifiers);
            var extended = (scanCode & 0xFF00) is 0xE000 or 0xE100;
            into.AddRange(Tap((int)(scanCode & 0xFF), extended));
            for (var i = modifiers.Count - 1; i >= 0; i--) into.Add(modifiers[i] with { Up = true });

            // A dead key waits for the next key to combine with; a space makes it type itself.
            if ((MapVirtualKeyExW(vk, MapVkToChar, layout) & 0x80000000) != 0) into.AddRange(Tap(ScanSpace));
        }
        return true;
    }

    /// <summary>The keyboard layout of the calling thread - the one a session it connected was told about.</summary>
    public static IntPtr CurrentLayout() => GetKeyboardLayout(0);

    private const uint MapVkToChar = 2;
    private const uint MapVkToVscEx = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanExW(char ch, IntPtr dwhkl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
