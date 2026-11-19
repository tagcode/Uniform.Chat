namespace Uniform.Matrix.Testing;

using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Uniform.Matrix.Hosting.Localization;
using Xunit;

/// <summary>Checks of a project's translations, shared by the test projects of the projects that have texts: each language has every text, in the plural forms its rules need, with the same placeholders.</summary>
public static partial class LocalizationChecks
{
    /// <summary>Return the cultures the translations are saved under, other than English, the copies of <see cref="Languages.Copies"/> included; right-to-left languages only when <paramref name="rightToLeft"/>, as the terminal shows English for them.</summary>
    public static TheoryData<string> Cultures(bool rightToLeft)
    {
        // Each language, then its copies
        TheoryData<string> cultures = [];
        foreach (Language language in Languages.All.Where(l => l.Culture != "en" && (rightToLeft || !l.RightToLeft)))
        {
            cultures.Add(language.Culture);
            foreach (string copy in Languages.Copies.GetValueOrDefault(language.Culture, [])) cultures.Add(copy);
        }
        // Return cultures
        return cultures;
    }

    /// <summary>Assert that <paramref name="culture"/> has a text for each English one, not empty, with the same placeholders: one per plural form its language uses for each text that counts, and no others; a copy, such as <c>no</c> of <c>nb</c>, the same texts as its source.</summary>
    /// <param name="texts">The project's texts.</param>
    /// <param name="culture">The culture name, e.g. <c>pl</c>.</param>
    /// <param name="hotkeys">Whether texts named <c>Button…</c> and <c>Menu…</c> keep their Terminal.Gui hotkey, e.g. <c>_Liity</c>.</param>
    public static void AssertTranslated(ResourceManager texts, string culture, bool hotkeys)
    {
        // The English texts and the culture's own, without falling back
        CultureInfo cultureInfo = CultureInfo.GetCultureInfo(culture);
        Dictionary<string, string> english = Read(texts, CultureInfo.InvariantCulture), translated = Read(texts, cultureInfo);
        string[] plurals = [.. english.Keys.Where(k => k.EndsWith(PluralRules.OtherSuffix, StringComparison.Ordinal)).Select(k => k[..^PluralRules.OtherSuffix.Length])];
        HashSet<string> expected = [.. english.Keys.Where(k => !plurals.Any(p => IsFormOf(k, p))), .. plurals.SelectMany(p => PluralRules.Forms(cultureInfo).Select(f => $"{p}_{f}"))];
        Assert.Empty(expected.Except(translated.Keys).Order());
        Assert.Empty(translated.Keys.Except(expected).Order());

        // Each text: filled in, the placeholders of its English text, and one that formats
        foreach ((string key, string text) in translated)
        {
            string source = english.TryGetValue(key, out string? same) ? same : english[$"{plurals.First(p => IsFormOf(key, p))}{PluralRules.OtherSuffix}"];
            Assert.False(string.IsNullOrWhiteSpace(text), $"{culture} {key}: empty");
            Assert.True(Placeholders(source).SetEquals(Placeholders(text)), $"{culture} {key}: other placeholders than in \"{source}\"");
            object?[] arguments = [.. Enumerable.Range(0, Placeholders(text).Select(int.Parse).DefaultIfEmpty(-1).Max() + 1).Select(i => (object?)1)];
            if (arguments.Length > 0) _ = string.Format(cultureInfo, text, arguments);
            if (key.StartsWith("DayFormat", StringComparison.Ordinal)) _ = new DateTime(2026, 3, 2).ToString(text, cultureInfo);
            if (hotkeys && key.StartsWith("Button", StringComparison.Ordinal)) Assert.True(text.Contains('_'), $"{culture} {key}: no hotkey in \"{text}\"");
        }

        // Done: a copy has its source's texts
        string? source2 = Languages.Copies.FirstOrDefault(c => c.Value.Contains(culture)).Key;
        if (source2 is not null) Assert.Equal(Read(texts, CultureInfo.GetCultureInfo(source2)).OrderBy(e => e.Key), translated.OrderBy(e => e.Key));
    }

    /// <summary>Assert that the menus and dialogs of <paramref name="groups"/>, each a list of keys shown together, have different hotkeys in <paramref name="culture"/>, among the texts that have one.</summary>
    public static void AssertDistinctHotkeys(ResourceManager texts, string culture, IEnumerable<string[]> groups)
    {
        // Each group's hotkeys, case-insensitive
        CultureInfo cultureInfo = CultureInfo.GetCultureInfo(culture);
        foreach (string[] group in groups)
        {
            List<(string Key, char Hotkey)> keys = [.. group.Select(k => (Key: k, Text: texts.GetString(k, cultureInfo) ?? throw new InvalidOperationException(k))).Where(e => e.Text.IndexOf('_') is int i && i >= 0 && i + 1 < e.Text.Length).Select(e => (e.Key, char.ToLower(e.Text[e.Text.IndexOf('_') + 1], cultureInfo)))];
            // Done: no hotkey twice
            foreach (IGrouping<char, (string Key, char Hotkey)> same in keys.GroupBy(k => k.Hotkey).Where(g => g.Count() > 1)) Assert.Fail($"{culture}: hotkey {same.Key} in {string.Join(", ", same.Select(s => s.Key))}");
        }
    }

    /// <summary>Return whether <paramref name="key"/> is a plural form of <paramref name="plural"/>, e.g. <c>DeviceCount_few</c> of <c>DeviceCount</c>.</summary>
    private static bool IsFormOf(string key, string plural) => key.StartsWith(plural + "_", StringComparison.Ordinal) && key[(plural.Length + 1)..] is "zero" or "one" or "two" or "few" or "many" or "other";

    /// <summary>Return one culture's texts, without its parents'.</summary>
    private static Dictionary<string, string> Read(ResourceManager texts, CultureInfo culture) =>
        (texts.GetResourceSet(culture, createIfNotExists: true, tryParents: false) ?? throw new InvalidOperationException($"No texts for {culture.Name}")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    /// <summary>Return the numbers of a text's placeholders.</summary>
    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>A placeholder of string.Format.</summary>
    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
