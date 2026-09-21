using System.Globalization;
using System.Threading;
using DynatecRDM.Resources;

namespace DynatecRDM.Services;

/// <summary>
/// The language the UI speaks. The text itself is in Resources\Strings.resx (English) and
/// Strings.nb.resx (Norwegian Bokmål); this decides which of the two the Strings class reads, and
/// formats the numbers inside messages the way that language writes them.
/// </summary>
public static class UiLanguage
{
    /// <summary>AppSettings.Language for English. An empty setting follows Windows.</summary>
    public const string English = "en";

    /// <summary>AppSettings.Language for Norwegian Bokmål.</summary>
    public const string Norwegian = "nb";

    /// <summary>The Windows display language, read before this class changes the UI culture.</summary>
    private static readonly CultureInfo WindowsCulture = CultureInfo.CurrentUICulture;

    /// <summary>The culture of the language in use.</summary>
    public static CultureInfo Culture { get; private set; } = Resolve(null);

    /// <summary>The language in use: <see cref="English"/> or <see cref="Norwegian"/>.</summary>
    public static string Current => Culture.TwoLetterISOLanguageName == Norwegian ? Norwegian : English;

    /// <summary>
    /// Switches the UI to the language a setting names, or to the Windows language when the
    /// setting is empty. Text already on screen keeps its language until it is built again.
    /// Returns whether the language changed.
    /// </summary>
    public static bool Apply(string? setting)
    {
        var culture = Resolve(setting);
        var changed = !culture.Equals(Culture);

        Culture = culture;
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        return changed;
    }

    /// <summary>string.Format in the UI language, so a decimal reads 1.5 in English and 1,5 in Norwegian.</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(Culture, format, args);

    /// <summary>
    /// The singular or the plural form, with the count put in for {0}. English and Norwegian both
    /// use the singular for exactly one and the plural for everything else, zero included.
    /// </summary>
    public static string Plural(long count, string one, string many) =>
        Format(count == 1 ? one : many, count);

    /// <summary>
    /// Norwegian for anyone whose Windows is in Norwegian - Bokmål, Nynorsk or plain "no", since
    /// Bokmål is far closer to Nynorsk than English is - and English for everyone else.
    /// </summary>
    private static CultureInfo Resolve(string? setting)
    {
        var norwegian = setting?.Trim().ToLowerInvariant() switch
        {
            English => false,
            Norwegian => true,
            _ => WindowsCulture.TwoLetterISOLanguageName is "nb" or "nn" or "no",
        };

        return CultureInfo.GetCultureInfo(norwegian ? "nb-NO" : "en");
    }
}
