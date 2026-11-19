namespace Uniform.Chat.Terminal.Tests;

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;

/// <summary>The texts of the terminal client and of its windows and dialogs: every one in Finnish too.</summary>
public partial class LocalizationTests
{
    /// <summary>Every English text has a Finnish one, not empty, with the same placeholders, and Finnish has no texts English lacks.</summary>
    [Theory]
    [InlineData("Uniform.Chat", "Uniform.Chat.Terminal.Localization.Strings")]
    [InlineData("Uniform.Matrix.Gui.Terminal", "Uniform.Matrix.Gui.Terminal.Localization.Strings")]
    public void TranslateEveryText(string assemblyName, string baseName)
    {
        // Each culture's own texts
        ResourceManager texts = new(baseName, Assembly.Load(assemblyName));
        Dictionary<string, string> english = Read(texts, CultureInfo.InvariantCulture), finnish = Read(texts, CultureInfo.GetCultureInfo("fi"));
        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(finnish.Keys).Order());
        Assert.Empty(finnish.Keys.Except(english.Keys).Order());
        // Done: filled in, same placeholders, menu hotkeys kept
        foreach ((string key, string text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(finnish[key]), key);
            Assert.True(Placeholders(text).SetEquals(Placeholders(finnish[key])), $"{key}: other placeholders");
            if (key.StartsWith("Button", StringComparison.Ordinal)) Assert.True(finnish[key].Contains('_'), $"{key}: no hotkey");
        }
    }

    /// <summary>Return one culture's texts, without its parents'.</summary>
    private static Dictionary<string, string> Read(ResourceManager texts, CultureInfo culture) =>
        (texts.GetResourceSet(culture, createIfNotExists: true, tryParents: false) ?? throw new InvalidOperationException(culture.Name)).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    /// <summary>Return the numbers of a text's placeholders.</summary>
    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>A placeholder of string.Format.</summary>
    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
