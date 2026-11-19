namespace Uniform.Chat.Avalonia.Tests;

using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using global::Avalonia.Controls;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.VisualTree;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;

/// <summary>The app's texts: every one in each language, and a change of language shown at once; alone, as the language is the whole process's.</summary>
[Collection(nameof(LocalizationTests))]
[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public partial class LocalizationTests
{
    /// <summary>Every English text has a Finnish one, not empty, with the same placeholders, and Finnish has no texts English lacks.</summary>
    [Fact]
    public void TranslateEveryText() => AssertComplete(Strings.ResourceManager, "fi");

    /// <summary>Choosing Finnish in the settings turns a shown text Finnish at once, and the system's language turns it back; the choice is kept.</summary>
    [AvaloniaFact]
    public void SwitchLanguage()
    {
        // The sign-in page in a window, in English
        string application = $"Uniform.Chat.Avalonia.Tests.{Guid.NewGuid():N}";
        MainViewModel model = new(new GuiPlatform { ApplicationName = application, DeviceName = "tests" }, new AvaloniaUiDispatcher());
        Window window = new() { Width = 500, Height = 800, Content = new MainView { DataContext = model } };
        window.Show();
        TextBlock FindNotice() => window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Strings.ResourceManager.GetString(nameof(Strings.PasswordNotSaved), CultureInfo.CurrentUICulture));
        try
        {
            TextBlock notice = FindNotice();
            Assert.Equal("Your password is used only to sign in; it is not saved.", notice.Text);

            // Act: Finnish
            model.Settings.LanguageIndex = 2;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Assert: the same text, now Finnish, and kept for the next start
            Assert.Equal("Salasanaasi käytetään vain kirjautumiseen; sitä ei tallenneta.", notice.Text);
            Assert.Equal("fi", new SettingsViewModel(application, canNotify: false).Language);
        }
        finally
        {
            // Done: back to the system's language for the other tests
            model.Settings.LanguageIndex = 0;
            window.Close();
        }
        Assert.Equal("Your password is used only to sign in; it is not saved.", FindNotice().Text);
    }

    /// <summary>Assert that <paramref name="language"/> has a text for every key of the neutral texts, with the same placeholders, and no others.</summary>
    internal static void AssertComplete(ResourceManager resources, string language)
    {
        // The neutral texts and the language's own, without falling back
        Dictionary<string, string> neutral = Read(resources, CultureInfo.InvariantCulture);
        Dictionary<string, string> translated = Read(resources, CultureInfo.GetCultureInfo(language));
        Assert.NotEmpty(neutral);
        Assert.Empty(neutral.Keys.Except(translated.Keys).Order());
        Assert.Empty(translated.Keys.Except(neutral.Keys).Order());
        // Done: each filled in with the same placeholders
        foreach ((string key, string text) in neutral)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
            Assert.True(Placeholders(text).SetEquals(Placeholders(translated[key])), $"{key}: \"{text}\" and \"{translated[key]}\" have other placeholders");
        }
    }

    /// <summary>Return the texts of one culture's resource set, without its parents'.</summary>
    private static Dictionary<string, string> Read(ResourceManager resources, CultureInfo culture)
    {
        // As embedded
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false) ?? throw new InvalidOperationException($"No texts in {culture.Name}.");
        // Return texts
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    /// <summary>Return the numbers of a text's placeholders, e.g. 0 of <c>{0:F1}</c>.</summary>
    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>A placeholder of string.Format.</summary>
    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
