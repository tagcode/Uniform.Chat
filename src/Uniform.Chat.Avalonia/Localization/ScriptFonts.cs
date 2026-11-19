namespace Uniform.Chat.Avalonia.Localization;

using global::Avalonia;
using global::Avalonia.Media;

/// <summary>Fonts for the scripts of the app's languages that Inter lacks: Arabic, Devanagari, Bengali and Telugu, so that their texts show without fonts installed on the system; Chinese and Japanese use the system's fonts.</summary>
public static class ScriptFonts
{
    /// <summary>The folder of the bundled fonts.</summary>
    private const string Folder = "avares://Uniform.Chat.Avalonia/Assets/Fonts";

    /// <summary>The bundled fonts by family name, with the Unicode ranges each covers.</summary>
    private static readonly (string Family, string Ranges)[] Fonts =
    [
        ("Noto Sans Arabic", "U+0600-06FF, U+0750-077F, U+0870-08FF, U+FB50-FDFF, U+FE70-FEFF"),
        ("Noto Sans Devanagari", "U+0900-097F, U+A8E0-A8FF"),
        ("Noto Sans Bengali", "U+0980-09FF"),
        ("Noto Sans Telugu", "U+0C00-0C7F"),
    ];

    /// <summary>Use the bundled fonts for the characters of their scripts, before the system's fonts.</summary>
    public static AppBuilder WithScriptFonts(this AppBuilder builder) => builder.With(new FontManagerOptions
    {
        FontFallbacks = [.. Fonts.Select(f => new FontFallback { FontFamily = new FontFamily($"{Folder}#{f.Family}"), UnicodeRange = UnicodeRange.Parse(f.Ranges) })],
    });
}
