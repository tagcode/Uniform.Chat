namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using System.Collections.Specialized;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;

/// <summary>The chat screen: the room list and the open room; the code here handles scrolling, keys and the narrow layout.</summary>
public partial class ChatView : UserControl
{
    private const double NarrowWidth = 720;
    private static readonly string[] QuickReactions = ["👍", "❤️", "😂", "🎉", "😮", "😢"];
    private ChatViewModel? model;
    private ScrollViewer? scroller;
    private bool followNewest = true;

    /// <summary>Create the view.</summary>
    public ChatView()
    {
        // Files can be dropped on the open room
        InitializeComponent();
        RoomArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        RoomArea.AddHandler(DragDrop.DropEvent, OnDrop);
        RoomArea.AddHandler(RichText.LinkClickedEvent, OnLinkClicked);

        // Keys before the text boxes take them: a multi-line box handles Enter and paste itself
        ComposerBox.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        RoomBox.AddHandler(KeyDownEvent, OnRoomBoxKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Follow the new view model's messages.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        // Rewire the message collection
        base.OnDataContextChanged(e);
        if (model is not null) model.Messages.Items.CollectionChanged -= OnMessagesChanged;
        model = DataContext as ChatViewModel;
        if (model is not null) model.Messages.Items.CollectionChanged += OnMessagesChanged;
    }

    /// <summary>Hook scrolling and the back gesture once shown.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        // Scrolling of the message list, and Android's back button
        base.OnLoaded(e);
        scroller = MessageList.FindDescendantOfType<ScrollViewer>();
        if (scroller is not null) scroller.ScrollChanged += OnScrollChanged;
        if (TopLevel.GetTopLevel(this) is TopLevel top) top.BackRequested += OnBackRequested;
    }

    /// <summary>Unhook scrolling and the back gesture.</summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // Undo OnLoaded
        base.OnUnloaded(e);
        if (scroller is not null) scroller.ScrollChanged -= OnScrollChanged;
        if (TopLevel.GetTopLevel(this) is TopLevel top) top.BackRequested -= OnBackRequested;
    }

    /// <summary>Switch to one pane when the window gets narrow.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // Phones are always narrow
        base.OnSizeChanged(e);
        if (model is null || App.Platform.SinglePane) return;
        model.IsNarrow = e.NewSize.Width < NarrowWidth;
    }

    /// <summary>Send with Enter; Shift+Enter starts a new line, Esc stops replying or editing.</summary>
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        // Paste of files or an image sends them; text pastes as usual
        if (model is null) return;
        if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        // Esc cancels a reply or edit
        if (e.Key == Key.Escape && model.IsComposingSpecial)
        {
            e.Handled = true;
            model.CancelComposeCommand.Execute(null);
            return;
        }

        // Plain Enter only
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (model.SendCommand.CanExecute(null)) model.SendCommand.Execute(null);
    }

    /// <summary>Join, chat or create with Enter in the room box.</summary>
    private void OnRoomBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter only
        if (e.Key != Key.Enter || model is null) return;
        e.Handled = true;
        if (model.OpenTargetCommand.CanExecute(null)) model.OpenTargetCommand.Execute(null);
    }

    /// <summary>Send files or an image from the clipboard to the open room, or paste its text into the message box.</summary>
    private async Task PasteAsync()
    {
        // Assert a clipboard
        if (model is null || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        try
        {
            // Copied files, e.g. from a file manager
            if (model.SelectedRoom is { IsInvite: false } && await clipboard.TryGetFilesAsync() is { Length: > 0 } items)
            {
                foreach (IStorageFile file in items.OfType<IStorageFile>())
                {
                    await using Stream content = await file.OpenReadAsync();
                    await model.SendFileAsync(content, file.Name);
                }
                return;
            }

            // A copied image, e.g. a screenshot, sent as PNG
            if (model.SelectedRoom is { IsInvite: false } && await clipboard.TryGetBitmapAsync() is Bitmap image)
            {
                using MemoryStream png = new();
                image.Save(png, new PngBitmapEncoderOptions());
                png.Position = 0;
                await model.SendFileAsync(png, $"image-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                return;
            }

            // Text, as the box would paste it
            ComposerBox.Paste();
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this
            model.Notice = $"Pasting failed: {exception.Message}";
        }
    }

    /// <summary>Keep the newest message in view while at the bottom; a new room starts at the bottom.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset is a new room
        if (e.Action == NotifyCollectionChangedAction.Reset) followNewest = true;
        if (!followNewest) return;
        Dispatcher.UIThread.Post(() => scroller?.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Remember whether the user is at the bottom, and load older messages at the top.</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Bottom within a few pixels follows new messages
        if (scroller is null || model is null) return;
        followNewest = scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 24;
        if (scroller.Offset.Y > 0 || e.OffsetDelta.Y >= 0 || !model.HasOpenRoom) return;
        if (model.LoadOlderCommand.CanExecute(null)) model.LoadOlderCommand.Execute(null);
    }

    /// <summary>Show the actions of the message under the pointer: reply, react, edit, delete and copy; right-click or long-press.</summary>
    private void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Assert a message
        if (model is null || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: MessageViewModel message } item) return;
        e.Handled = true;
        ChatViewModel chat = model;
        MenuFlyout menu = new();

        // What applies to this message
        if (message.CanAnswer)
        {
            menu.Items.Add(CreateMenuItem("Reply", () => { chat.StartReply(message); ComposerBox.Focus(); }));
            MenuItem react = new() { Header = "React" };
            foreach (string key in QuickReactions) react.Items.Add(CreateMenuItem(key, () => _ = chat.ReactAsync(message, key)));
            menu.Items.Add(react);
        }
        if (message.CanEdit) menu.Items.Add(CreateMenuItem("Edit", () => { chat.StartEdit(message); ComposerBox.Focus(); ComposerBox.CaretIndex = ComposerBox.Text?.Length ?? 0; }));
        menu.Items.Add(CreateMenuItem("Copy text", () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(message.Text)));
        if (message.CanDelete)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Delete", () => _ = chat.DeleteAsync(message)));
        }
        // Done
        MessageMenu = menu;
        menu.ShowAt(item, showAtPointer: true);
    }

    /// <summary>The message menu shown last, for tests.</summary>
    internal MenuFlyout? MessageMenu { get; private set; }

    /// <summary>Return a menu item that runs <paramref name="action"/>.</summary>
    private static MenuItem CreateMenuItem(string header, Action action)
    {
        // Click runs the action
        MenuItem item = new() { Header = header };
        item.Click += (_, _) => action();
        // Return item
        return item;
    }

    /// <summary>Follow a link in a message: Matrix links inside the app, web and mail links in the system's apps.</summary>
    private async void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        // Matrix links first; other schemes, such as file:, are not opened
        if (model is null || model.OpenMatrixLink(e.Href)) return;
        if (!Uri.TryCreate(e.Href, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http" or "mailto") || TopLevel.GetTopLevel(this) is not TopLevel top) return;
        try
        {
            if (!await top.Launcher.LaunchUriAsync(uri)) model.Notice = $"No app opens {e.Href} here.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            model.Notice = $"Opening {e.Href} failed: {exception.Message}";
        }
    }

    /// <summary>Ask the member of the clicked row to verify.</summary>
    private void OnVerifyMemberClick(object? sender, RoutedEventArgs e)
    {
        // The row's member
        if (model is not null && (sender as Control)?.DataContext is MemberItem member) _ = model.VerifyMemberAsync(member);
    }

    /// <summary>Pick a file and send it to the open room.</summary>
    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        // Assert a room and a picker
        if (model is null || TopLevel.GetTopLevel(this)?.StorageProvider is not IStorageProvider storage) return;
        try
        {
            // One message per file
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Send a file", AllowMultiple = true });
            foreach (IStorageFile file in files)
            {
                await using Stream content = await file.OpenReadAsync();
                await model.SendFileAsync(content, file.Name);
            }
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            model.Notice = $"Sending the file failed: {exception.Message}";
        }
    }

    /// <summary>Open an attachment in the app the system uses for its type.</summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        // The button's message
        if ((sender as Control)?.DataContext is MessageViewModel message) _ = OpenAttachmentAsync(message);
    }

    /// <summary>Open an image by clicking its preview.</summary>
    private void OnImageReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Left click or tap only
        if (e.InitialPressMouseButton != MouseButton.Left || (sender as Control)?.DataContext is not MessageViewModel message) return;
        e.Handled = true;
        _ = OpenAttachmentAsync(message);
    }

    /// <summary>Download an attachment and open it with the system; suggest saving where opening is not possible.</summary>
    private async Task OpenAttachmentAsync(MessageViewModel message)
    {
        // Assert a launcher
        if (model is null || TopLevel.GetTopLevel(this) is not TopLevel top) return;
        try
        {
            // A copy with the original name, so the system finds an app by its extension
            if (await model.DownloadForOpeningAsync(message) is not string path) return;
            IStorageFile? file = await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (file is null || !await top.Launcher.LaunchFileAsync(file)) model.Notice = $"No app opens {message.FileName} here; use Save… instead.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            model.Notice = $"Opening {message.FileName} failed: {exception.Message}";
        }
    }

    /// <summary>Accept files dragged over the open room.</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Files only, into a joined room
        e.DragEffects = model?.SelectedRoom is { IsInvite: false } && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Send files dropped on the open room.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Assert files
        if (model is null || e.DataTransfer.TryGetFiles() is not { } items) return;
        try
        {
            // One message per file
            foreach (IStorageFile file in items.OfType<IStorageFile>())
            {
                await using Stream content = await file.OpenReadAsync();
                await model.SendFileAsync(content, file.Name);
            }
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            model.Notice = $"Sending the file failed: {exception.Message}";
        }
    }

    /// <summary>Download an attachment and save it where the user chooses.</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Assert an attachment and a picker
        if (model is null || (sender as Control)?.DataContext is not MessageViewModel message || TopLevel.GetTopLevel(this)?.StorageProvider is not IStorageProvider storage) return;
        try
        {
            // Copy from the media cache to the chosen file
            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save file", SuggestedFileName = message.FileName });
            if (target is null || await model.DownloadAsync(message) is not string path) return;
            await using (Stream source = File.OpenRead(path))
            await using (Stream destination = await target.OpenWriteAsync())
                await source.CopyToAsync(destination);
            model.Notice = $"Saved {target.Name}.";
        }
        catch (Exception exception)
        {
            // On error tell, since nothing awaits this handler
            model.Notice = $"Saving the file failed: {exception.Message}";
        }
    }

    /// <summary>Go back one step with Android's back button: close a panel, or the room on narrow screens; otherwise the app closes.</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        // Handled when something was closed
        if (model?.GoBack() == true) e.Handled = true;
    }
}
