namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;

/// <summary>Text sizes of the app scaled from the chosen base size: the titles, small and tiny texts of <c>Styles.axaml</c> grow and shrink with it.</summary>
public static class TextScale
{
    /// <summary>Base text size of the normal setting, which the sizes of <c>Styles.axaml</c> are made for.</summary>
    public const double Normal = 14;

    /// <summary>Put the text sizes for <paramref name="textSize"/> into <paramref name="resources"/>; Fluent controls size their text by <c>ControlContentThemeFontSize</c>.</summary>
    public static void Apply(IResourceDictionary resources, double textSize)
    {
        // The base size, and the others in proportion
        double scale = textSize / Normal;
        resources["ControlContentThemeFontSize"] = textSize;
        resources["TitleFontSize"] = Math.Round(16 * scale);
        resources["SmallFontSize"] = Math.Round(12 * scale, 1);
        // Done
        resources["TinyFontSize"] = Math.Round(11 * scale, 1);
    }
}
