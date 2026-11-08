namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>Appearance and notification settings, and the logs, licences and audio test in their place.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Create the view.</summary>
    public SettingsView() => InitializeComponent();

    /// <summary>Copy the shown log entries, e.g. to send them with a bug report.</summary>
    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        // Assert logs and a clipboard
        if (DataContext is not SettingsViewModel { Logs: LogsViewModel logs } || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        // Done
        await clipboard.SetTextAsync(logs.Text);
    }
}
