namespace Uniform.Matrix.Hosting.Tests;

using System.Globalization;
using System.Resources;
using Uniform.Matrix.Hosting.Localization;

/// <summary>The plural forms of whole numbers by the CLDR rules of each language.</summary>
public class PluralRuleTests
{
    /// <summary>Counts take the form of their language's rules, e.g. Polish 2 few, 5 many, 22 few; a culture with a region follows its language's rules.</summary>
    [Theory]
    [InlineData("en", "0:other 1:one 2:other 21:other")]
    [InlineData("fi-FI", "0:other 1:one 2:other")]
    [InlineData("ja", "0:other 1:other 2:other")]
    [InlineData("fr", "0:one 1:one 2:other 1000000:many")]
    [InlineData("pt-BR", "0:one 1:one 2:other")]
    [InlineData("hi", "0:one 1:one 2:other")]
    [InlineData("pl", "0:many 1:one 2:few 4:few 5:many 12:many 22:few 25:many 101:many")]
    [InlineData("ru", "0:many 1:one 2:few 5:many 11:many 21:one 22:few 111:many")]
    [InlineData("uk", "1:one 3:few 14:many 31:one")]
    [InlineData("cs", "0:other 1:one 3:few 5:other")]
    [InlineData("ro", "0:few 1:one 2:few 19:few 20:other 101:few")]
    [InlineData("ar", "0:zero 1:one 2:two 3:few 10:few 11:many 99:many 100:other 103:few")]
    public void ChooseForm(string culture, string expected)
    {
        // Each count and its form
        CultureInfo cultureInfo = CultureInfo.GetCultureInfo(culture);
        foreach (string pair in expected.Split(' '))
        {
            string[] parts = pair.Split(':');
            // Done: the form, one of the language's
            Assert.Equal(parts[1], PluralRules.Category(cultureInfo, long.Parse(parts[0], CultureInfo.InvariantCulture)));
            Assert.Contains(parts[1], PluralRules.Forms(cultureInfo));
        }
    }

    /// <summary>Each language's rules return only its forms, for every count up to 10 000 and a million, and every form at some count.</summary>
    [Fact]
    public void ReturnOwnForms()
    {
        // Each language, by every count
        foreach (Language language in Languages.All)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(language.Culture);
            HashSet<string> seen = [.. Enumerable.Range(0, 10_001).Append(1_000_000).Select(n => PluralRules.Category(culture, n))];
            // Done: no other forms; all but a form only fractions take
            Assert.Subset(PluralRules.Forms(culture).ToHashSet(), seen);
            Assert.Empty(PluralRules.Forms(culture).Except(seen).Except(language.Culture is "ru" or "uk" or "pl" ? ["other"] : []));
        }
    }

    /// <summary>A count's text is its form's, filled in, and the other form where the form has no text.</summary>
    [Fact]
    public void FormatCount()
    {
        // English texts of one and other, and the culture of the thread
        ResourceManager texts = new SingleSetManager(new Dictionary<string, string> { ["Devices_one"] = "{0} device", ["Devices_other"] = "{0} devices", ["Files_other"] = "{0} files" });
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

        // Done
        Assert.Equal("1 device", PluralRules.Format(texts, "Devices_other", 1, 1));
        Assert.Equal("3 devices", PluralRules.Format(texts, "Devices_other", 3, 3));
        Assert.Equal("1 files", PluralRules.Format(texts, "Files_other", 1, 1));
        Assert.Throws<ArgumentException>(() => PluralRules.Format(texts, "Devices_one", 1, 1));
    }

    /// <summary>Texts of one set for every culture.</summary>
    private sealed class SingleSetManager(Dictionary<string, string> texts) : ResourceManager
    {
        /// <summary>Return the text of a key, or null.</summary>
        public override string? GetString(string name, CultureInfo? culture) => texts.GetValueOrDefault(name);
    }
}
