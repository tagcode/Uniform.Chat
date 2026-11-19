namespace Uniform.Chat.Avalonia.Tests;

using System.Globalization;
using global::Avalonia.Controls;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Media;
using global::Avalonia.VisualTree;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Testing;

/// <summary>The app's texts: every one in each language, and a change of language shown at once; alone, as the language is the whole process's.</summary>
[Collection(nameof(LocalizationTests))]
[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public class LocalizationTests
{
    /// <summary>Return every culture the app's texts are translated to.</summary>
    public static TheoryData<string> Cultures => LocalizationChecks.Cultures(rightToLeft: true);

    /// <summary>Every English text is in <paramref name="culture"/>, in each plural form the language uses, with the same placeholders; the day formats are valid.</summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void TranslateEveryText(string culture) => LocalizationChecks.AssertTranslated(Strings.ResourceManager, culture, hotkeys: false);

    /// <summary>Choosing Finnish in the settings turns a shown text Finnish at once, Arabic turns the layout right to left, and the system's language turns both back; the choice is kept.</summary>
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
            model.Settings.LanguageIndex = Translations.Languages.ToList().IndexOf("fi");
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Assert: the same text, now Finnish, and kept for the next start
            Assert.Equal("Salasanaasi käytetään vain kirjautumiseen; sitä ei tallenneta.", notice.Text);
            Assert.Equal("fi", new SettingsViewModel(application, canNotify: false).Language);
            Assert.Equal(FlowDirection.LeftToRight, window.Content is MainView main ? main.FlowDirection : default);

            // Act: Arabic, which runs from right to left, as does the layout
            model.Settings.LanguageIndex = Translations.Languages.ToList().IndexOf("ar");
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(FlowDirection.RightToLeft, ((MainView)window.Content!).FlowDirection);
        }
        finally
        {
            // Done: back to the system's language for the other tests
            model.Settings.LanguageIndex = 0;
            window.Close();
        }
        Assert.Equal("Your password is used only to sign in; it is not saved.", FindNotice().Text);
        Assert.Equal(FlowDirection.LeftToRight, Translations.Instance.FlowDirection);
    }
}
