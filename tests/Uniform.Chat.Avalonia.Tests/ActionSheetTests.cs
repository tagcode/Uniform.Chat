namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Headless;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Input;
using global::Avalonia.VisualTree;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>A long press or right-click on a message or a room shows its details and actions: a sheet from the bottom on phones, a menu on desktops.</summary>
public class ActionSheetTests
{
    /// <summary>On a phone, a long press on someone's message shows a sheet with its sender and reactions; a reaction is sent, and report reasons are a step further, which the back button returns from.</summary>
    [AvaloniaFact]
    public async Task ActOnMessageOnPhone()
    {
        // Arrange: Alice reads Bob's message on a narrow screen
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await bob.SendTextAsync(roomId, "hello");
        (MainViewModel model, Window window, ChatViewModel chat) = await OpenAsync(server, alice, narrow: true);

        try
        {
            await ChatFlowTests.WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == roomId);
            await ChatFlowTests.WaitUntilAsync(() => Row<MessageViewModel>(window, m => m.Text == "hello") is not null, "the message");

            // Act: long press on the message
            Press(Row<MessageViewModel>(window, m => m.Text == "hello")!);

            // Assert: a sheet with the sender, the actions for others' messages and reactions
            ActionSheetViewModel sheet = Assert.IsType<ActionSheetViewModel>(chat.ActionSheet);
            Assert.Equal("bob", sheet.Title);
            Assert.Contains(sheet.Details, d => d.Label == "From" && d.Value.Contains(bob.UserId!));
            Assert.Equal(["Reply", "Copy text", "Copy link", "Report message", "Report sender", "Block sender"], sheet.Actions.Select(a => a.Label));
            Assert.True(sheet.HasReactions);
            await CaptureAsync(window, "message-sheet.png");

            // Act and assert: report reasons are a step further, and back returns, then closes
            sheet.ChooseCommand.Execute(sheet.Actions.Single(a => a.Label == "Report message"));
            Assert.True(sheet.IsChoosing);
            Assert.Equal(ChatViewModel.ReportReasons, sheet.Shown.Select(a => a.Label));
            Assert.True(chat.GoBack());
            Assert.False(sheet.IsChoosing);
            Assert.Same(sheet, chat.ActionSheet);
            Assert.True(chat.GoBack());
            Assert.Null(chat.ActionSheet);

            // Act and assert: a reaction from the sheet reaches the message and closes the sheet
            Press(Row<MessageViewModel>(window, m => m.Text == "hello")!);
            chat.ActionSheet!.ReactCommand.Execute("👍");
            Assert.Null(chat.ActionSheet);
            await ChatFlowTests.WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == "hello" && m.Reactions.Contains("👍")), "the reaction");
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>On a phone, a long press on a room shows its details; marking it read clears its count without opening it, and a favourite moves first in the list.</summary>
    [AvaloniaFact]
    public async Task ActOnRoomOnPhone()
    {
        // Arrange: Alice in two rooms, one with unread messages
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string first = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Aardvarks", Preset = RoomPresets.PublicChat });
        string second = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Zebras", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(first);
        await alice.JoinRoomAsync(second);
        (MainViewModel model, Window window, ChatViewModel chat) = await OpenAsync(server, alice, narrow: true);

        try
        {
            await ChatFlowTests.WaitUntilAsync(() => Row<RoomItemViewModel>(window, r => r.RoomId == second) is not null, "the rooms");
            await bob.SendTextAsync(second, "one");
            await bob.SendTextAsync(second, "two");
            RoomItemViewModel zebras = chat.Rooms.First(r => r.RoomId == second);
            await ChatFlowTests.WaitUntilAsync(() => zebras.Unread == 2, "two unread");

            // Act: long press on the room
            Press(Row<RoomItemViewModel>(window, r => r.RoomId == second)!);

            // Assert: its details and actions
            ActionSheetViewModel sheet = Assert.IsType<ActionSheetViewModel>(chat.ActionSheet);
            Assert.Equal("Zebras", sheet.Title);
            Assert.Contains(sheet.Details, d => d.Label == "Members" && d.Value == "2");
            Assert.Contains(sheet.Details, d => d.Label == "Room ID" && d.Value == second);
            Assert.Equal(["Mark as read", "Favourite", "Low priority", "Copy link", "Report room", "Leave room"], sheet.Actions.Select(a => a.Label));
            await CaptureAsync(window, "room-sheet.png");

            // Act and assert: read without opening, also after the next sync
            sheet.ChooseCommand.Execute(sheet.Actions.Single(a => a.Label == "Mark as read"));
            Assert.Null(chat.ActionSheet);
            Assert.Equal(0, zebras.Unread);
            Assert.Null(chat.SelectedRoom);
            await bob.SendTextAsync(first, "elsewhere");
            await ChatFlowTests.WaitUntilAsync(() => chat.Rooms.First(r => r.RoomId == first).Unread == 1, "a later sync");
            Assert.Equal(0, zebras.Unread);

            // Act and assert: a favourite moves first
            Press(Row<RoomItemViewModel>(window, r => r.RoomId == second)!);
            chat.ActionSheet!.ChooseCommand.Execute(chat.ActionSheet.Actions.Single(a => a.Label == "Favourite"));
            await ChatFlowTests.WaitUntilAsync(() => zebras.IsFavourite && chat.Rooms[0] == zebras, "the favourite first");
            Press(Row<RoomItemViewModel>(window, r => r.RoomId == second)!);
            Assert.Contains(chat.ActionSheet!.Actions, a => a.Label == "Remove from favourites");
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>On a desktop, a right-click on a room shows a menu of its actions, whose last item shows the details in the sheet.</summary>
    [AvaloniaFact]
    public async Task ShowRoomMenuOnDesktop()
    {
        // Arrange: Alice in a room, on a wide screen
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        (MainViewModel model, Window window, ChatViewModel chat) = await OpenAsync(server, alice, narrow: false);

        try
        {
            await ChatFlowTests.WaitUntilAsync(() => Row<RoomItemViewModel>(window, r => r.RoomId == roomId) is not null, "the room");

            // Act: right-click on the room
            ListBoxItem row = Row<RoomItemViewModel>(window, r => r.RoomId == roomId)!;
            Point center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Right);
            window.MouseUp(center, MouseButton.Right);

            // Assert: a menu, not the sheet, with the details last
            ChatView view = window.GetVisualDescendants().OfType<ChatView>().Single();
            Assert.Null(chat.ActionSheet);
            Assert.NotNull(view.ContextMenuShown);
            MenuItem[] items = [.. view.ContextMenuShown.Items.OfType<MenuItem>()];
            Assert.Equal(["Favourite", "Low priority", "Copy link", "Report room", "Leave room", "Details…"], items.Select(i => i.Header as string));
            Assert.Equal(ChatViewModel.ReportReasons, items.Single(i => i.Header as string == "Report room").Items.OfType<MenuItem>().Select(i => i.Header as string));

            // Act and assert: the details open the sheet
            items[^1].RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("Kitchen", chat.ActionSheet?.Title);
            view.ContextMenuShown.Hide();
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>A long press on a message's text itself, not only beside it, shows the message's sheet, not the text's own Copy menu, which the text would otherwise show for its selected word.</summary>
    [AvaloniaFact]
    public async Task ShowMessageSheetFromText()
    {
        // Arrange: Bob's message on a phone
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await bob.SendTextAsync(roomId, "hello there");
        (MainViewModel model, Window window, ChatViewModel chat) = await OpenAsync(server, alice, narrow: true);

        try
        {
            await ChatFlowTests.WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == roomId);
            await ChatFlowTests.WaitUntilAsync(() => window.GetVisualDescendants().OfType<RichText>().Any(t => t.Body == "hello there"), "the message");

            // Act: a long press on the text, which selects a word first, as on a phone
            RichText text = window.GetVisualDescendants().OfType<RichText>().First(t => t.Body == "hello there");
            text.SelectionStart = 0;
            text.SelectionEnd = 5;
            text.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = text });

            // Assert: the message's sheet
            Assert.Equal("bob", chat.ActionSheet?.Title);
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>Sign <paramref name="user"/> in, in a shown window on a narrow or wide screen, and return the model, the window and the chat page.</summary>
    private static async Task<(MainViewModel Model, Window Window, ChatViewModel Chat)> OpenAsync(LocalServer server, MatrixClient user, bool narrow)
    {
        // A window of a phone's or a desktop's width
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        Window window = new() { Width = narrow ? 420 : 1000, Height = 760, Content = new MainView { DataContext = model } };
        ChatViewModel chat = await ChatFlowTests.SignInAsync(model, user.UserId!);
        window.Show();
        await ChatFlowTests.WaitUntilAsync(() => chat.IsNarrow == narrow, "the layout");
        // Return them
        return (model, window, chat);
    }

    /// <summary>Return the shown row whose item of type <typeparamref name="T"/> matches <paramref name="match"/>, or null.</summary>
    private static ListBoxItem? Row<T>(Window window, Func<T, bool> match) => window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is T item && match(item));

    /// <summary>Press long on <paramref name="row"/>, which Avalonia turns into a context request as a right-click is.</summary>
    private static void Press(ListBoxItem row) => row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });

    /// <summary>Capture <paramref name="window"/> into the screenshot folder, when one is given.</summary>
    private static async Task CaptureAsync(Window window, string fileName)
    {
        // After layout and rendering
        await Task.Delay(200);
        if (Environment.GetEnvironmentVariable("UNIFORM_MATRIX_SCREENSHOTS") is not string folder) return;
        // Done
        window.CaptureRenderedFrame()?.Save(Path.Combine(folder, fileName), new global::Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
