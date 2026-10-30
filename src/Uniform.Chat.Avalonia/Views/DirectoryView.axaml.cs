namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using global::Avalonia.Controls;
using global::Avalonia.Input;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;

/// <summary>The room directory: server and search fields, and the rooms found with a Join button each.</summary>
public partial class DirectoryView : UserControl
{
    /// <summary>Build the view; Enter in either field searches.</summary>
    public DirectoryView()
    {
        // Enter searches
        InitializeComponent();
        ServerBox.KeyDown += OnFieldKeyDown;
        SearchBox.KeyDown += OnFieldKeyDown;
    }

    /// <summary>Search on Enter, closing the keyboard.</summary>
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter only; leaving the field closes a phone's keyboard, which would cover the results
        if (e.Key != Key.Enter || DataContext is not DirectoryViewModel directory) return;
        e.Handled = true;
        Results.Focus();
        directory.SearchCommand.Execute(null);
    }
}
