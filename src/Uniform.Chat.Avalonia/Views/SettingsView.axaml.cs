namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>Appearance and notification settings, and the logs, licences and audio test in their place.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Create the view, with each language by its own name in the language list after the system's.</summary>
    public SettingsView()
    {
        // The list before its selection is bound
        InitializeComponent();
        foreach (string language in Translations.Languages.Skip(1)) LanguageBox.Items.Add(new ComboBoxItem { Content = Translations.NameOf(language) });
    }

    /// <summary>Copy the shown log entries, e.g. to send them with a bug report.</summary>
    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        // Assert logs and a clipboard
        if (DataContext is not SettingsViewModel { Logs: LogsViewModel logs } || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        // Done
        await clipboard.SetTextAsync(logs.Text);
    }
}
