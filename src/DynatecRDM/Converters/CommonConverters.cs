using System.Globalization;
using System.IO;
using DynatecRDM.Resources;
using DynatecRDM.Services;
using Binding = System.Windows.Data.Binding;
using BitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using BitmapImage = System.Windows.Media.Imaging.BitmapImage;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using IMultiValueConverter = System.Windows.Data.IMultiValueConverter;
using IValueConverter = System.Windows.Data.IValueConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Visibility = System.Windows.Visibility;

namespace DynatecRDM.Converters;

/// <summary>Shared parsing helpers. Kept out of the converters so the name Convert stays free.</summary>
internal static class ConverterUtil
{
    internal static bool IsInverted(object? parameter) => parameter switch
    {
        bool b => b,
        string s => s.Equals("invert", StringComparison.OrdinalIgnoreCase)
            || s.Equals("inverse", StringComparison.OrdinalIgnoreCase)
            || s.Equals("inverted", StringComparison.OrdinalIgnoreCase)
            || s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s == "!",
        _ => false,
    };

    internal static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => bool.TryParse(s, out var parsed) ? parsed : s.Trim().Length > 0,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            System.Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0d,
        _ => true,
    };

    internal static Visibility ToVisibility(bool visible, object? parameter) =>
        visible ^ IsInverted(parameter) ? Visibility.Visible : Visibility.Collapsed;

    internal static int? ToCount(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => l > int.MaxValue ? int.MaxValue : (int)l,
        string s => s.Length,
        System.Collections.ICollection c => c.Count,
        System.Collections.IEnumerable e => CountOf(e),
        _ => null,
    };

    private static int CountOf(System.Collections.IEnumerable source)
    {
        var count = 0;
        var enumerator = source.GetEnumerator();
        try
        {
            while (enumerator.MoveNext()) count++;
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
        return count;
    }

    internal static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(ConverterUtil.ToBool(value), parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility v && v == Visibility.Visible) ^ ConverterUtil.IsInverted(parameter);
}

public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !ConverterUtil.ToBool(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !ConverterUtil.ToBool(value);
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterUtil.ToVisibility(value is not null, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? value?.ToString();
        return ConverterUtil.ToVisibility(!string.IsNullOrWhiteSpace(text), parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = ConverterUtil.ToCount(value);
        return ConverterUtil.ToVisibility(count.GetValueOrDefault() > 0, parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Binds a radio button or toggle to one value of an enum.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        if (value.Equals(parameter)) return true;

        if (parameter is not string option) return false;

        var name = value.ToString();
        if (name is null) return false;

        if (option.IndexOf(',') < 0)
            return string.Equals(name, option.Trim(), StringComparison.OrdinalIgnoreCase);

        foreach (var candidate in option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is null || !ConverterUtil.ToBool(value)) return Binding.DoNothing;

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (parameter is string option)
        {
            var parts = option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var first = parts.Length > 0 ? parts[0] : option.Trim();

            if (type.IsEnum && Enum.TryParse(type, first, ignoreCase: true, out var parsed) && parsed is not null)
                return parsed;

            return type == typeof(string) ? first : Binding.DoNothing;
        }

        return type.IsInstanceOfType(parameter) ? parameter : Binding.DoNothing;
    }
}

/// <summary>
/// True when the value is a colour <see cref="HexToBrushConverter"/> can read. Lets a style fall
/// back to a theme brush for "no colour", which a ConverterParameter could not do: it is fixed at
/// load time and would keep the old theme's colour after a theme change.
/// </summary>
public sealed class IsColorConverter : IValueConverter
{
    public static readonly IsColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        HexToBrushConverter.GetBrush(value as string ?? value?.ToString()) is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Text colour for writing on a user-chosen colour: near-black or white, whichever contrasts more.
/// Every background has at least 4.5:1 against one of the two.
/// </summary>
public sealed class ContrastForegroundConverter : IValueConverter
{
    public static readonly ContrastForegroundConverter Instance = new();

    private static readonly SolidColorBrush Dark = ConverterUtil.Frozen(0x0E, 0x11, 0x16);
    private static readonly SolidColorBrush Light = ConverterUtil.Frozen(0xFF, 0xFF, 0xFF);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var background = HexToBrushConverter.GetBrush(value as string ?? value?.ToString());
        if (background is null) return System.Windows.DependencyProperty.UnsetValue;

        return ColorContrast.Ratio(Dark.Color, background.Color) >= ColorContrast.Ratio(Light.Color, background.Color)
            ? Dark
            : Light;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>"#RRGGBB" (also #RGB, #AARRGGBB and colour names) to a frozen, cached brush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    private const int CacheLimit = 128;
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(32, StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = GetBrush(value as string ?? value?.ToString());
        if (brush is not null) return brush;

        return GetBrush(parameter as string) ?? Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    /// <summary>Frozen brush for a colour string, or null when it cannot be read.</summary>
    public static SolidColorBrush? GetBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        var key = hex.Trim();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        if (!TryParseColor(key, out var color)) return null;

        var brush = new SolidColorBrush(color);
        brush.Freeze();

        lock (Gate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = brush;
        }

        return brush;
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;

        var span = text.AsSpan().Trim();
        if (span.Length == 0) return false;
        if (span[0] == '#') span = span[1..];

        if (span.Length is 3 or 6 or 8
            && uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            switch (span.Length)
            {
                case 3:
                    color = Color.FromRgb(
                        (byte)(((packed >> 8) & 0xF) * 17),
                        (byte)(((packed >> 4) & 0xF) * 17),
                        (byte)((packed & 0xF) * 17));
                    return true;
                case 6:
                    color = Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
                    return true;
                default:
                    color = Color.FromArgb(
                        (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
                    return true;
            }
        }

        if (text.Length > 0 && text[0] == '#') return false;

        try
        {
            if (ColorConverter.ConvertFromString(text) is Color named)
            {
                color = named;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }
}

/// <summary>
/// File path to an image. The file is read through a stream and cached on load so the snapshot
/// writer can keep overwriting it, and a refreshed file is always picked up.
/// As a multi-value converter the first value is the path and the rest only exist to make the
/// binding re-run: a snapshot is rewritten under the same name, so the path alone never changes.
/// </summary>
public sealed class FileToImageConverter : IValueConverter, IMultiValueConverter
{
    public static readonly FileToImageConverter Instance = new();

    private const int CacheLimit = 16;
    private static readonly Dictionary<string, BitmapImage> Cache = new(16, StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string ?? (value as Uri)?.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;

            var decodeWidth = DecodeWidth(parameter);

            // The stamp is part of the key, so a rewritten snapshot is never served from the cache.
            var key = $"{path}|{file.LastWriteTimeUtc.Ticks}|{file.Length}|{decodeWidth}";

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }

            var image = Load(path, decodeWidth);

            lock (Gate)
            {
                if (Cache.Count >= CacheLimit) Cache.Clear();
                Cache[key] = image;
            }

            return image;
        }
        catch (Exception ex)
        {
            AppLog.Debug_($"Image '{path}' could not be loaded: {ex.Message}");
            return null;
        }
    }

    private static BitmapImage Load(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        using (var stream = new FileStream(
                   path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // No IgnoreImageCache: WPF's image cache is keyed by UriSource, and with only a stream
            // set that flag makes EndInit evict a null key and throw on every single load.
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: > 0 } ? Convert(values[0], targetType, parameter, culture) : null;

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        Array.Empty<object?>();

    private static int DecodeWidth(object? parameter) => parameter switch
    {
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}

/// <summary>UTC timestamp to a short human age: "just now", "3 min ago", "2 h ago", "yesterday", "12 Mar".</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var effective = culture ?? CultureInfo.CurrentCulture;

        // The binding culture only reads the input. The text speaks the UI language, which the
        // binding culture (en-US unless a Language is set on the element) does not follow.
        switch (value)
        {
            case DateTime dt:
                return Describe(Normalize(dt));
            case DateTimeOffset dto:
                return Describe(dto.UtcDateTime);
            case string s when DateTime.TryParse(
                s, effective, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed):
                return Describe(parsed);
            default:
                return string.Empty;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public static string Describe(DateTime utc, CultureInfo? culture = null)
    {
        if (utc == default) return string.Empty;

        var effective = culture ?? UiLanguage.Culture;
        var delta = DateTime.UtcNow - utc;
        if (delta.Ticks < 0) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 60) return Strings.Time_JustNow;
        if (delta.TotalMinutes < 60) return UiLanguage.Format(Strings.Time_MinutesAgo, (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return UiLanguage.Format(Strings.Time_HoursAgo, (int)delta.TotalHours);

        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today.AddDays(-1)) return Strings.Time_Yesterday;

        return local.Year == today.Year
            ? local.ToString(Strings.Time_DateThisYear, effective)
            : local.ToString(Strings.Time_DateOtherYear, effective);
    }

    private static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
