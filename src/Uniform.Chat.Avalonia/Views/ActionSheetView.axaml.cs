namespace Uniform.Chat.Avalonia.Views;

using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>The details and actions of a message or room, as a sheet: a title, details, quick reactions and a row per action.</summary>
public partial class ActionSheetView : UserControl
{
    /// <summary>Create the view.</summary>
    public ActionSheetView() => InitializeComponent();

    /// <summary>Run the clicked action, or show its choices.</summary>
    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        // The row's action
        if (DataContext is not ActionSheetViewModel sheet || (sender as Control)?.DataContext is not SheetAction action) return;
        sheet.ChooseCommand.Execute(action);
    }

    /// <summary>Send the clicked reaction.</summary>
    private void OnReactionClick(object? sender, RoutedEventArgs e)
    {
        // The button's reaction
        if (DataContext is not ActionSheetViewModel sheet || (sender as Control)?.DataContext is not string key) return;
        sheet.ReactCommand.Execute(key);
    }
}
