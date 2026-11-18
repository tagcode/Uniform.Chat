namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Controls;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.VisualTree;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using static Uniform.Chat.Avalonia.Tests.ChatFlowTests;

/// <summary>The open room stays open while the room list reorders, e.g. as a message sent in it makes it the most recent.</summary>
public class RoomOrderTests
{
    /// <summary>Sending in the room lowest in the list moves it to the top; it stays open throughout, on a phone-sized window and on a wide one.</summary>
    [AvaloniaTheory]
    [InlineData(400, 800)]
    [InlineData(1000, 680)]
    public async Task KeepOpenRoomWhenListReorders(double width, double height)
    {
        // Arrange: three rooms, the oldest open, shown in a window
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string oldest = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Oldest", Preset = RoomPresets.PublicChat });
        string middle = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Middle", Preset = RoomPresets.PublicChat });
        string newest = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Newest", Preset = RoomPresets.PublicChat });
        foreach (string room in new[] { oldest, middle, newest })
        {
            await alice.JoinRoomAsync(room);
            await bob.SendTextAsync(room, $"hello {room}");
        }
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        Window window = new() { Width = width, Height = height, Content = new MainView { DataContext = model } };

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            window.Show();
            await WaitUntilAsync(() => chat.Rooms.Count == 3 && chat.Rooms[^1].RoomId == oldest, "the rooms, the oldest last");
            RoomItemViewModel open = chat.Rooms[^1];
            chat.SelectedRoom = open;
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == $"hello {oldest}"), "the oldest room open");
            bool cleared = false;
            chat.Messages.Items.CollectionChanged += (_, e) => cleared |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;

            // Act: send there, which makes it the most recent
            chat.Composer = "mine";
            await chat.SendCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.Rooms[0].RoomId == oldest && chat.Messages.Items.Any(m => m.Text == "mine" && !m.IsOutbox), "the room moved to the top with the message");
            await Task.Delay(300);

            // Done: still open, its messages never cleared to load again
            Assert.Same(open, chat.SelectedRoom);
            Assert.False(cleared);
            Assert.Same(open, window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RoomList").SelectedItem);
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }
}
