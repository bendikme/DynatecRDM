using System.Windows;
using DynatecRDM.Converters;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DynatecRDM.Services;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Follows the Windows app mode (Settings &gt; Personalisation &gt; Colours &gt; Choose your app
/// mode). A theme is one palette dictionary in the application's resources, and every palette
/// reference in the UI is a DynamicResource, so swapping that one dictionary repaints every open
/// window. The title bars, which WPF does not draw, are repainted alongside.
/// </summary>
public sealed class ThemeService : IDisposable
{
    public const string DefaultAccent = "#2A94FF";

    /// <summary>What derived accent colours aim for: WCAG AA text contrast plus a little headroom.</summary>
    private const double DerivedTarget = 4.6;

    /// <summary>SelectionOpacity of the text boxes in Controls.xaml.</summary>
    private const double SelectionOpacity = 0.4;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private static readonly Uri DarkPalette = new("pack://application:,,,/DynatecRDM;component/Themes/Palette.Dark.xaml");
    private static readonly Uri LightPalette = new("pack://application:,,,/DynatecRDM;component/Themes/Palette.Light.xaml");

    /// <summary>Everything text and accents are drawn on.</summary>
    private static readonly string[] Surfaces =
    {
        "BackgroundColor", "SurfaceColor", "SurfaceAltColor", "SurfaceHoverColor", "SurfaceSelectedColor",
    };

    private static readonly string[] SurfacesAndTints = Surfaces.Concat(new[] { "AccentSubtleColor", "DangerSubtleColor" }).ToArray();

    /// <summary>
    /// The contrast every palette must meet (WCAG 2.x AA: 4.5:1 for text, 3:1 for the parts of a
    /// control that identify it). Checked each time a palette is applied, so a colour edited in
    /// the XAML or an accent chosen in Settings cannot quietly fall below them.
    /// </summary>
    private static readonly (string What, string Foreground, string[] Backgrounds, double Minimum)[] Rules =
    {
        ("primary text", "TextPrimaryColor", SurfacesAndTints, 7.0),
        ("secondary text", "TextSecondaryColor", SurfacesAndTints, 4.5),
        ("muted text", "TextMutedColor", SurfacesAndTints, 4.5),
        ("accent text", "AccentColor", Surfaces, 4.5),
        ("success text", "SuccessColor", Surfaces, 4.5),
        ("warning text", "WarningColor", Surfaces, 4.5),
        ("danger text", "DangerColor", Surfaces.Concat(new[] { "DangerSubtleColor" }).ToArray(), 4.5),
        ("text on the accent", "OnAccentColor", new[] { "AccentColor", "AccentHoverColor", "AccentPressedColor" }, 4.5),
        ("text on danger buttons", "OnDangerColor", new[] { "DangerFillColor", "DangerFillHoverColor" }, 4.5),
        ("control borders", "BorderStrongColor", new[] { "BackgroundColor", "SurfaceColor", "SurfaceAltColor" }, 3.0),
    };

    private readonly System.Windows.Application _app;
    private ResourceDictionary? _palette;
    private string? _accent;
    private bool _applied;
    private bool _disposed;

    private ThemeService(System.Windows.Application app, string? accent)
    {
        _app = app;
        _accent = accent;
    }

    public static ThemeService? Current { get; private set; }

    public AppTheme Theme { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Raised on the UI thread once a new palette is in place, for the few things resources do
    /// not reach, such as the tray icon's Windows Forms menu.
    /// </summary>
    public event EventHandler? PaletteChanged;

    /// <summary>Applies the Windows theme with the saved accent and keeps following Windows from then on.</summary>
    public static ThemeService Start(System.Windows.Application app, string? accent)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (Current is { } running)
        {
            running.SetAccent(accent);
            return running;
        }

        var service = new ThemeService(app, accent);
        Current = service;
        service.Apply(ReadWindowsTheme());

        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        SystemEvents.UserPreferenceChanged += service.OnUserPreferenceChanged;
        return service;
    }

    /// <summary>The app mode Windows is set to. Absent means it was never changed, and light is the default.</summary>
    public static AppTheme ReadWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightTheme) is int value && value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the Windows app mode; staying dark.", ex);
            return AppTheme.Dark;
        }
    }

    /// <summary>Re-derives the accent colours from a new choice in Settings.</summary>
    public void SetAccent(string? hex)
    {
        if (_disposed) return;
        _accent = hex;
        Apply(Theme);
    }

    /// <summary>A palette colour, for code that cannot use a resource reference.</summary>
    public Color GetColor(string key, Color fallback) =>
        _palette?[key] is Color color ? color : fallback;

    /// <summary>Every contrast rule the palette breaks, described; empty when it meets them all.</summary>
    public static IReadOnlyList<string> FindContrastProblems(ResourceDictionary palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var problems = new List<string>();
        foreach (var (what, foreground, backgrounds, minimum) in Rules)
        {
            var fg = (Color)palette[foreground];
            foreach (var background in backgrounds)
            {
                var ratio = ColorContrast.Ratio(fg, (Color)palette[background]);
                if (ratio < minimum)
                    problems.Add($"{what} ({foreground}) is {ratio:0.00}:1 on {background}, below {minimum}:1");
            }
        }

        // Selected text sits on the accent at SelectionOpacity, over the text box's own surface.
        var text = (Color)palette["TextPrimaryColor"];
        var accent = (Color)palette["AccentColor"];
        foreach (var surface in new[] { "SurfaceAltColor", "SurfaceColor" })
        {
            var selection = ColorContrast.Mix((Color)palette[surface], accent, SelectionOpacity);
            var ratio = ColorContrast.Ratio(text, selection);
            if (ratio < 4.5) problems.Add($"selected text is {ratio:0.00}:1 on the selection over {surface}, below 4.5:1");
        }

        return problems;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; }
        catch (Exception ex) { AppLog.Warn("Could not stop following the Windows theme.", ex); }

        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void Apply(AppTheme theme)
    {
        var palette = new ResourceDictionary { Source = theme == AppTheme.Light ? LightPalette : DarkPalette };
        DeriveAccent(palette, theme == AppTheme.Dark, _accent);

        foreach (var problem in FindContrastProblems(palette))
            AppLog.Warn($"{theme} theme contrast: {problem}.");

        var merged = _app.Resources.MergedDictionaries;
        var index = _palette is not null ? merged.IndexOf(_palette) : IndexOfPalette(merged);
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette);
        _palette = palette;

        if (!_applied || Theme != theme) AppLog.Info($"Using the {theme.ToString().ToLowerInvariant()} theme, following Windows.");
        _applied = true;
        Theme = theme;

        foreach (Window window in _app.Windows) PaintTitleBar(window);

        try
        {
            PaletteChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("A palette listener threw.", ex);
        }
    }

    /// <summary>
    /// Turns the chosen accent into the four accent colours for this theme. A dark theme needs a
    /// light accent - accent text has to read on dark surfaces, and the accent then carries dark
    /// text as a button - and a light theme the opposite, so the choice is moved lighter or darker
    /// until it meets the targets, keeping its hue.
    /// </summary>
    private static void DeriveAccent(ResourceDictionary palette, bool dark, string? hex)
    {
        var seed = HexToBrushConverter.GetBrush(hex)?.Color
                   ?? HexToBrushConverter.GetBrush(DefaultAccent)!.Color;

        var surfaces = Surfaces.Select(k => (Color)palette[k]).ToArray();
        var onAccent = new[] { (Color)palette["OnAccentColor"] };

        var accent = ColorContrast.Ensure(seed, surfaces, DerivedTarget, dark);
        accent = ColorContrast.Ensure(accent, onAccent, DerivedTarget, dark);
        accent = KeepSelectionReadable(palette, accent, surfaces, onAccent[0], dark);
        var hover = ColorContrast.Ensure(ColorContrast.Shift(accent, dark ? 0.16 : -0.14), onAccent, DerivedTarget, dark);
        var pressed = ColorContrast.Ensure(ColorContrast.Shift(accent, dark ? -0.10 : -0.26), onAccent, DerivedTarget, dark);

        Put(palette, "Accent", accent);
        Put(palette, "AccentHover", hover);
        Put(palette, "AccentPressed", pressed);
        Put(palette, "AccentSubtle", Tint(palette, accent, dark));

        // For a filled button that always carries white - the header's "new connection": the
        // accent taken darker until white reads on it, in both themes and for any chosen accent.
        var white = new[] { Color.FromRgb(255, 255, 255) };
        var strong = ColorContrast.Ensure(seed, white, DerivedTarget, lighter: false);
        Put(palette, "AccentStrong", strong);
        Put(palette, "AccentStrongHover", ColorContrast.Ensure(ColorContrast.Shift(strong, -0.10), white, DerivedTarget, lighter: false));
        Put(palette, "AccentStrongPressed", ColorContrast.Ensure(ColorContrast.Shift(strong, -0.20), white, DerivedTarget, lighter: false));
    }

    /// <summary>
    /// Selected text is drawn on the accent at SelectionOpacity. An accent at the far end of the
    /// range - white or yellow on the dark theme - lifts that highlight until the text on it stops
    /// reading, so such an accent is eased back towards the surfaces, for as long as it still
    /// meets its own targets. An ordinary accent passes at the first step and is left alone.
    /// </summary>
    private static Color KeepSelectionReadable(ResourceDictionary palette, Color accent, Color[] surfaces, Color onAccent, bool dark)
    {
        var text = (Color)palette["TextPrimaryColor"];
        var behind = new[] { (Color)palette["SurfaceAltColor"], (Color)palette["SurfaceColor"] };

        for (var step = 0; step <= 100; step++)
        {
            var candidate = ColorContrast.Shift(accent, (dark ? -1 : 1) * step / 100.0);
            if (behind.All(s => ColorContrast.Ratio(text, ColorContrast.Mix(s, candidate, SelectionOpacity)) >= DerivedTarget) &&
                surfaces.All(s => ColorContrast.Ratio(candidate, s) >= DerivedTarget) &&
                ColorContrast.Ratio(candidate, onAccent) >= DerivedTarget)
            {
                return candidate;
            }
        }

        return accent;
    }

    /// <summary>The surface washed with the accent, as strongly as the text drawn on it allows.</summary>
    private static Color Tint(ResourceDictionary palette, Color accent, bool dark)
    {
        var surface = (Color)palette["SurfaceColor"];
        var primary = (Color)palette["TextPrimaryColor"];
        var secondary = (Color)palette["TextSecondaryColor"];
        var muted = (Color)palette["TextMutedColor"];

        var tint = surface;
        foreach (var amount in new[] { 0.20, 0.17, 0.14, 0.11, 0.08, 0.05 })
        {
            tint = ColorContrast.Mix(surface, accent, dark ? amount : amount * 0.6);
            if (ColorContrast.Ratio(primary, tint) >= 7.0 &&
                ColorContrast.Ratio(secondary, tint) >= 4.5 &&
                ColorContrast.Ratio(muted, tint) >= 4.5)
            {
                break;
            }
        }

        return tint;
    }

    private static void Put(ResourceDictionary palette, string name, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        palette[name + "Color"] = color;
        palette[name + "Brush"] = brush;
    }

    private static int IndexOfPalette(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Windows announces an app-mode change as a general preference change, as it does many
    /// others, so the setting itself is read to tell whether the theme actually moved.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;

        try
        {
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;

                var theme = ReadWindowsTheme();
                if (theme != Theme) Apply(theme);
            }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not follow the Windows theme change.", ex);
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) Current?.PaintTitleBar(window);
    }

    private void PaintTitleBar(Window window) =>
        WindowTitleBar.Apply(window, Theme == AppTheme.Dark, GetColor("BackgroundColor", Color.FromRgb(0x16, 0x18, 0x1D)));
}
