namespace Uniform.Matrix.Hosting.Localization;

using System.Globalization;
using System.Resources;

/// <summary>The plural forms of the apps' languages for whole numbers, by the CLDR plural rules, and the texts of a count in the current language.</summary>
/// <remarks>
/// A text that counts has one resource key per form, the base key followed by <c>_zero</c>, <c>_one</c>, <c>_two</c>, <c>_few</c>, <c>_many</c> or <c>_other</c>, e.g. <c>DeviceCount_one</c> and <c>DeviceCount_other</c>.
/// Each language has the forms of <see cref="Forms"/>; <c>_other</c> is always there.
/// <see href="https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html">CLDR language plural rules</see>.
/// </remarks>
public static class PluralRules
{
    /// <summary>The suffix of the form every language has, which a lookup falls back to.</summary>
    public const string OtherSuffix = "_other";

    /// <summary>The forms of English, which languages without their own rules follow.</summary>
    private static readonly string[] OneOther = ["one", "other"];

    /// <summary>Return the language of <paramref name="culture"/> as the rules know it, e.g. <c>no</c> for <c>nb-NO</c>.</summary>
    private static string LanguageOf(CultureInfo culture)
    {
        // The first part of the name, lower case
        string name = culture.Name;
        int dash = name.IndexOf('-');
        string language = (dash < 0 ? name : name[..dash]).ToLowerInvariant();
        // Return the language, with those that share rules joined
        return language switch { "nb" or "nn" => "no", _ => language };
    }

    /// <summary>Return the forms <paramref name="culture"/>'s language uses for whole numbers, <c>other</c> included, e.g. <c>one, few, many, other</c> for Polish.</summary>
    public static IReadOnlyList<string> Forms(CultureInfo culture) => LanguageOf(culture) switch
    {
        "ja" or "zh" or "id" or "vi" => ["other"],
        "fr" or "pt" => ["one", "many", "other"],
        "ro" or "cs" => ["one", "few", "other"],
        "ru" or "uk" or "pl" => ["one", "few", "many", "other"],
        "ar" => ["zero", "one", "two", "few", "many", "other"],
        _ => OneOther,
    };

    /// <summary>Return the form of whole number <paramref name="count"/> in <paramref name="culture"/>'s language: <c>zero</c>, <c>one</c>, <c>two</c>, <c>few</c>, <c>many</c> or <c>other</c>.</summary>
    public static string Category(CultureInfo culture, long count)
    {
        // The rules see the number without its sign
        long n = Math.Abs(count), last = n % 10, lastTwo = n % 100;
        // Return the form by the language's rules
        return LanguageOf(culture) switch
        {
            "ja" or "zh" or "id" or "vi" => "other",
            "hi" or "bn" => n <= 1 ? "one" : "other",
            "fr" or "pt" => n <= 1 ? "one" : n % 1_000_000 == 0 ? "many" : "other",
            "ro" => n == 1 ? "one" : n == 0 || lastTwo is >= 1 and <= 19 ? "few" : "other",
            "cs" => n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other",
            "ru" or "uk" => last == 1 && lastTwo != 11 ? "one" : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "few" : "many",
            "pl" => n == 1 ? "one" : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "few" : "many",
            "ar" => n == 0 ? "zero" : n == 1 ? "one" : n == 2 ? "two" : lastTwo is >= 3 and <= 10 ? "few" : lastTwo >= 11 ? "many" : "other",
            _ => n == 1 ? "one" : "other",
        };
    }

    /// <summary>Return the text of <paramref name="count"/> in the current language: the form of key <paramref name="otherKey"/>'s base that the count takes, else the <c>_other</c> form, filled in with <paramref name="arguments"/>.</summary>
    /// <param name="resources">The texts.</param>
    /// <param name="otherKey">The key of the <c>_other</c> form, e.g. <c>nameof(Strings.DeviceCount_other)</c>.</param>
    /// <param name="count">The number that chooses the form.</param>
    /// <param name="arguments">The values of the placeholders, the count among them where the text shows it.</param>
    public static string Format(ResourceManager resources, string otherKey, long count, params object?[] arguments)
    {
        // Assert the key names the other form
        if (!otherKey.EndsWith(OtherSuffix, StringComparison.Ordinal)) throw new ArgumentException($"{otherKey} is not the key of an {OtherSuffix} form.", nameof(otherKey));
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string key = $"{otherKey[..^OtherSuffix.Length]}_{Category(culture, count)}";
        string format = resources.GetString(key, culture) ?? resources.GetString(otherKey, culture) ?? otherKey;
        // Return text
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
