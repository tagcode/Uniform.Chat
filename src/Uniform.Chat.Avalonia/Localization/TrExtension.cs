namespace Uniform.Chat.Avalonia.Localization;

using global::Avalonia.Data;
using global::Avalonia.Markup.Xaml;

/// <summary>A text of the app in the current language, for XAML, e.g. <c>Text="{l:Tr Send}"</c>; it follows a change of language.</summary>
/// <param name="key">The text's key in <c>Strings</c>.</param>
public sealed class TrExtension(string key) : MarkupExtension
{
    /// <summary>The text's key in <c>Strings</c>.</summary>
    public string Key { get; set; } = key;

    /// <summary>Return a binding to the text in <see cref="Translations.Instance"/>.</summary>
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{Key}]") { Source = Translations.Instance, Mode = BindingMode.OneWay };
}
