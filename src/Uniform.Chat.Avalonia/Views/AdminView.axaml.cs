namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>Server administration: reports, accounts and registration tokens.</summary>
public partial class AdminView : UserControl
{
    /// <summary>Create the view.</summary>
    public AdminView() => InitializeComponent();

    /// <summary>Return the panel and the clicked row's item, or nulls.</summary>
    private (AdminViewModel? Admin, T? Item) Row<T>(object? sender) where T : class => (DataContext as AdminViewModel, (sender as Control)?.DataContext as T);

    /// <summary>Delete the clicked report.</summary>
    private void OnDeleteReportClick(object? sender, RoutedEventArgs e)
    {
        // The row's report
        if (Row<AdminReportItem>(sender) is (AdminViewModel admin, AdminReportItem item)) _ = admin.DeleteReportAsync(item);
    }

    /// <summary>Suspend the clicked report's user.</summary>
    private void OnSuspendReportedClick(object? sender, RoutedEventArgs e)
    {
        // The row's user
        if (Row<AdminReportItem>(sender) is (AdminViewModel admin, AdminReportItem item)) _ = admin.SuspendReportedAsync(item);
    }

    /// <summary>Ask to shut the clicked report's room down.</summary>
    private void OnShutDownRoomClick(object? sender, RoutedEventArgs e)
    {
        // The row's room
        if (Row<AdminReportItem>(sender) is (AdminViewModel admin, AdminReportItem item)) admin.ShutDownRoom(item);
    }

    /// <summary>Show the clicked account's actions.</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        // The row's account
        if (Row<AdminAccountItem>(sender) is (AdminViewModel admin, AdminAccountItem item)) _ = admin.SelectAccountAsync(item);
    }

    /// <summary>Delete the clicked token.</summary>
    private void OnDeleteTokenClick(object? sender, RoutedEventArgs e)
    {
        // The row's token
        if (Row<AdminTokenItem>(sender) is (AdminViewModel admin, AdminTokenItem item)) _ = admin.DeleteTokenAsync(item);
    }

    /// <summary>Copy the token just made.</summary>
    private async void OnCopyTokenClick(object? sender, RoutedEventArgs e)
    {
        // Assert a token and a clipboard
        if (DataContext is not AdminViewModel { HasCreatedToken: true } admin || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        try
        {
            await clipboard.SetTextAsync(admin.CreatedToken);
            admin.Message = "Token copied.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            admin.Message = $"Copying failed: {exception.Message}";
        }
    }
}
