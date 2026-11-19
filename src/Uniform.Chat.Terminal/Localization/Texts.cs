namespace Uniform.Chat.Terminal.Localization;

using System.Globalization;

/// <summary>The terminal client's language: the system's, or the one of setting <c>Ui:Language</c>, for its texts in <see cref="Strings"/> and the libraries' own texts, such as event descriptions.</summary>
internal static class Texts
{
    /// <summary>Use the language of culture name <paramref name="language"/>, e.g. <c>fi</c>, on every thread; null, empty or unknown keeps the system's.</summary>
    public static void Apply(string? language)
    {
        // Only a known culture
        if (string.IsNullOrWhiteSpace(language)) return;
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(language.Trim()); }
        catch (CultureNotFoundException) { return; }
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // Done
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    /// <summary>Return <paramref name="format"/>, a text of <see cref="Strings"/> with placeholders such as <c>{0}</c>, filled in with the formats of the current culture.</summary>
    public static string Format(string format, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, format, arguments);
}
