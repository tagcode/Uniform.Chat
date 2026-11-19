namespace Uniform.Matrix.Gui.Terminal.Localization;

using System.Globalization;
using Uniform.Matrix.Hosting.Localization;

/// <summary>Formatting of the texts in <see cref="Strings"/>, in the language of the current UI culture.</summary>
internal static class Texts
{
    /// <summary>Return <paramref name="format"/>, a text of <see cref="Strings"/> with placeholders such as <c>{0}</c>, filled in with the formats of the current culture.</summary>
    public static string Format(string format, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, format, arguments);

    /// <summary>Return the text of <paramref name="count"/> in the current language, by key <paramref name="otherKey"/> of its <c>_other</c> form, e.g. <c>nameof(Strings.MoreLines_other)</c>; see <see cref="PluralRules"/>.</summary>
    public static string Plural(string otherKey, long count, params object?[] arguments) => PluralRules.Format(Strings.ResourceManager, otherKey, count, arguments);
}
