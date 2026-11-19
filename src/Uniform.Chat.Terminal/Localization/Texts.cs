namespace Uniform.Chat.Terminal.Localization;

using System.Globalization;
using Uniform.Matrix.Hosting.Localization;

/// <summary>The terminal client's language: the system's, or the one of setting <c>Ui:Language</c>, for its texts in <see cref="Strings"/> and the libraries' own texts, such as event descriptions.</summary>
internal static class Texts
{
    /// <summary>Use the language of culture name <paramref name="language"/>, e.g. <c>fi</c>, on every thread; null, empty or unknown keeps the system's. A right-to-left language, such as Arabic, shows English, as terminals draw such text in reverse.</summary>
    public static void Apply(string? language)
    {
        // The chosen culture, else the system's
        CultureInfo culture = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrWhiteSpace(language))
        {
            try { culture = CultureInfo.GetCultureInfo(language.Trim()); }
            catch (CultureNotFoundException) { }
        }
        if (Languages.IsRightToLeft(culture)) culture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // Done
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    /// <summary>Return <paramref name="format"/>, a text of <see cref="Strings"/> with placeholders such as <c>{0}</c>, filled in with the formats of the current culture.</summary>
    public static string Format(string format, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, format, arguments);

    /// <summary>Return the text of <paramref name="count"/> in the current language, by key <paramref name="otherKey"/> of its <c>_other</c> form, e.g. <c>nameof(Strings.DeviceCount_other)</c>; see <see cref="PluralRules"/>.</summary>
    public static string Plural(string otherKey, long count, params object?[] arguments) => PluralRules.Format(Strings.ResourceManager, otherKey, count, arguments);
}
