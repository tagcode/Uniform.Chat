namespace Uniform.Matrix.Client.Localization;

using System.Globalization;

/// <summary>Fills in the library's texts of <see cref="Strings"/>, which are in <see cref="CultureInfo.CurrentUICulture"/>: English by default, and the languages the library has texts for, such as Finnish.</summary>
internal static class Texts
{
    /// <summary>Return <paramref name="format"/>, a text with placeholders such as <c>{0}</c>, filled in with the formats of the current culture.</summary>
    public static string Format(string format, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, format, arguments);
}
