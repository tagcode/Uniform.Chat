namespace Uniform.Chat.Avalonia.Localization;

using System.ComponentModel;
using System.Globalization;

/// <summary>The app's texts in the chosen language, for XAML through <see cref="TrExtension"/>; changing the language updates every bound text at once.</summary>
/// <remarks>Code reads its texts from <c>Strings</c>, in <see cref="CultureInfo.CurrentUICulture"/>, which <see cref="Apply"/> sets for every thread, so texts made after a change are in the new language; libraries' own texts, such as event descriptions, follow it too.</remarks>
public sealed class Translations : INotifyPropertyChanged
{
    private static readonly CultureInfo System = CultureInfo.CurrentUICulture;

    /// <summary>The one instance the views bind to.</summary>
    public static Translations Instance { get; } = new();

    /// <summary>The languages to choose from, by culture name, the system's first as an empty name.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["", "en", "fi"];

    /// <summary>Raised for the indexer, as <c>Item</c> and <c>Item[]</c>, when the language changes, so that bound texts are read again.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, for texts made in code to be made again.</summary>
    public event Action? Changed;

    /// <summary>The text of <paramref name="key"/> in the current language, or the key itself where there is none, so a missing text shows rather than fails.</summary>
    public string this[string key] => Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Return <paramref name="format"/>, a text of <c>Strings</c> with placeholders such as <c>{0}</c>, filled in with the formats of the current culture.</summary>
    public static string Format(string format, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, format, arguments);

    /// <summary>Return the name of a language as people choose it, in the current language for the system's, e.g. <c>Suomi</c>.</summary>
    public static string NameOf(string language) => language switch
    {
        "" => Strings.LanguageSystem,
        "fi" => "Suomi",
        _ => "English",
    };

    /// <summary>Use the language of culture name <paramref name="language"/>, e.g. <c>fi</c>; null or empty for the system's. Texts and the formats of dates and numbers follow on every thread.</summary>
    public void Apply(string? language)
    {
        // The culture, the system's when unset or unknown
        CultureInfo culture = System;
        if (!string.IsNullOrEmpty(language))
        {
            try { culture = CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException) { culture = System; }
        }
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        // Done: bound texts and code read again; Avalonia's indexer bindings listen for "Item", others for "Item[]"
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }
}
