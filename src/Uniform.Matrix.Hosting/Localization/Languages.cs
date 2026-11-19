namespace Uniform.Matrix.Hosting.Localization;

using System.Globalization;

/// <summary>The languages the apps' texts are translated to, by culture name, with the name people know each by.</summary>
/// <remarks>
/// Some translations are saved under more than one culture name so that systems in each find them: Norwegian Bokmål as <c>nb</c> and <c>no</c>; see <see cref="Copies"/>.
/// </remarks>
public static class Languages
{
    /// <summary>The languages in the order a list shows them: by own name, Latin script first, then Greek, Cyrillic, Arabic, Indic and East Asian.</summary>
    public static IReadOnlyList<Language> All { get; } =
    [
        new("id", "Bahasa Indonesia"),
        new("cs", "Čeština"),
        new("da", "Dansk"),
        new("de", "Deutsch"),
        new("en", "English"),
        new("fr", "Français"),
        new("hu", "Magyar"),
        new("nl", "Nederlands"),
        new("nb", "Norsk bokmål"),
        new("pl", "Polski"),
        new("pt", "Português"),
        new("ro", "Română"),
        new("fi", "Suomi"),
        new("sv", "Svenska"),
        new("vi", "Tiếng Việt"),
        new("tr", "Türkçe"),
        new("el", "Ελληνικά"),
        new("bg", "Български"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("ur", "اردو", RightToLeft: true),
        new("ar", "العربية", RightToLeft: true),
        new("mr", "मराठी"),
        new("hi", "हिन्दी"),
        new("bn", "বাংলা"),
        new("te", "తెలుగు"),
        new("zh-Hans", "中文（简体）"),
        new("ja", "日本語"),
    ];

    /// <summary>The culture names a translation is also saved under, by the culture it is written for.</summary>
    public static IReadOnlyDictionary<string, string[]> Copies { get; } = new Dictionary<string, string[]>
    {
        ["nb"] = ["no"],
    };

    /// <summary>Return whether text in <paramref name="culture"/> runs from right to left, as for Arabic and Urdu.</summary>
    public static bool IsRightToLeft(CultureInfo culture)
    {
        // The culture or one it falls back to
        for (CultureInfo current = culture; current.Name.Length > 0; current = current.Parent)
        {
            if (All.Any(l => l.RightToLeft && string.Equals(l.Culture, current.Name, StringComparison.OrdinalIgnoreCase))) return true;
        }
        // Return the system's data
        return culture.TextInfo.IsRightToLeft;
    }
}

/// <summary>A language of the apps' texts.</summary>
/// <param name="Culture">The culture name its texts are saved under, e.g. <c>fi</c>.</param>
/// <param name="Name">Its name in itself, e.g. <c>Suomi</c>.</param>
/// <param name="RightToLeft">Whether its text runs from right to left.</param>
public sealed record Language(string Culture, string Name, bool RightToLeft = false);
