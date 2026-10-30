namespace Uniform.Chat.Avalonia.Views;

using System.ComponentModel;
using global::Avalonia.Controls;
using Uniform.Chat.Avalonia.ViewModels;

/// <summary>Desktop window around <see cref="MainView"/>; its title counts unread rooms, and its focus decides whether notifications show.</summary>
public partial class MainWindow : Window
{
    private MainViewModel? model;
    private ChatViewModel? chat;

    /// <summary>Create the window.</summary>
    public MainWindow()
    {
        // Focus changes tell the chat whether the user is looking
        InitializeComponent();
        Activated += (_, _) => SetActive(true);
        Deactivated += (_, _) => SetActive(false);
    }

    /// <summary>Follow the app's pages.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        // Swap the page subscription
        base.OnDataContextChanged(e);
        if (model is not null) model.PropertyChanged -= OnModelChanged;
        model = DataContext as MainViewModel;
        if (model is not null) model.PropertyChanged += OnModelChanged;
        Follow(model?.Page as ChatViewModel);
    }

    /// <summary>Follow the chat page once signed in.</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Page changes only
        if (e.PropertyName == nameof(MainViewModel.Page)) Follow(model?.Page as ChatViewModel);
    }

    /// <summary>Watch <paramref name="next"/> for unread rooms instead of the previous chat page.</summary>
    private void Follow(ChatViewModel? next)
    {
        // Swap, then show the current state
        if (chat is not null) chat.PropertyChanged -= OnChatChanged;
        chat = next;
        if (chat is not null)
        {
            chat.PropertyChanged += OnChatChanged;
            chat.IsActive = IsActive;
        }
        UpdateTitle();
    }

    /// <summary>Update the title when the unread count changes.</summary>
    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Unread rooms only
        if (e.PropertyName == nameof(ChatViewModel.UnreadRooms)) UpdateTitle();
    }

    /// <summary>Tell the chat whether the window has the focus.</summary>
    private void SetActive(bool active)
    {
        // Notifications show while inactive
        if (chat is not null) chat.IsActive = active;
    }

    /// <summary>Show <c>Uniform Chat (3)</c> while three rooms have unread messages or are invites.</summary>
    private void UpdateTitle() => Title = chat is { UnreadRooms: > 0 } open ? $"Uniform Chat ({open.UnreadRooms})" : "Uniform Chat";
}
