using Color = System.Windows.Media.Color;

namespace DynatecRDM.Services;

/// <summary>
/// WCAG 2.x contrast arithmetic, and the lightness adjustments the theme uses to reach a target
/// ratio without changing a colour's hue.
/// </summary>
internal static class ColorContrast
{
    /// <summary>Relative luminance, 0 for black to 1 for white.</summary>
    public static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    /// <summary>Contrast ratio between two colours, from 1 (none) to 21 (black on white).</summary>
    public static double Ratio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Moves a colour's HSL lightness by <paramref name="amount"/> of the room left in that
    /// direction: +0.2 is a fifth of the way to white, -0.2 a fifth of the way to black. Hue and
    /// saturation are kept, which mixing with white or black would wash out.
    /// </summary>
    public static Color Shift(Color c, double amount)
    {
        ToHsl(c, out var h, out var s, out var l);
        l = amount >= 0 ? l + ((1 - l) * amount) : l + (l * amount);
        return FromHsl(h, s, Math.Clamp(l, 0, 1));
    }

    /// <summary>
    /// The first colour, stepping <paramref name="c"/> lighter or darker, that has at least
    /// <paramref name="minimum"/> contrast against every colour in <paramref name="against"/>.
    /// </summary>
    public static Color Ensure(Color c, IReadOnlyList<Color> against, double minimum, bool lighter)
    {
        for (var step = 0; step <= 100; step++)
        {
            var candidate = Shift(c, (lighter ? 1 : -1) * step / 100.0);
            var ok = true;
            for (var i = 0; i < against.Count && ok; i++) ok = Ratio(candidate, against[i]) >= minimum;
            if (ok) return candidate;
        }

        return lighter ? Color.FromRgb(255, 255, 255) : Color.FromRgb(0, 0, 0);
    }

    /// <summary>Linear blend: 0 is <paramref name="from"/>, 1 is <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    private static double Channel(byte value)
    {
        var v = value / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        l = (max + min) / 2;
        if (delta == 0)
        {
            h = s = 0;
            return;
        }

        s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        h = max == r ? ((g - b) / delta) + (g < b ? 6 : 0)
          : max == g ? ((b - r) / delta) + 2
          : ((r - g) / delta) + 4;
        h /= 6;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            var grey = (byte)Math.Round(l * 255);
            return Color.FromRgb(grey, grey, grey);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return Color.FromRgb(
            (byte)Math.Round(Hue(p, q, h + (1.0 / 3)) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - (1.0 / 3)) * 255));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + ((q - p) * 6 * t);
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + ((q - p) * ((2.0 / 3) - t) * 6);
        return p;
    }
}
