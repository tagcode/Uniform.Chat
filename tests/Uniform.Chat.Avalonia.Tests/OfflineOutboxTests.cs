namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Headless.XUnit;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using static Uniform.Chat.Avalonia.Tests.ChatFlowTests;

/// <summary>Messages written without network wait in the outbox, where they can be edited and cancelled, and files can be queued too.</summary>
public class OfflineOutboxTests
{
    /// <summary>With the server gone: a message waits, its edit changes it in place, cancelling removes it, and a pasted file waits by its name.</summary>
    [AvaloniaFact]
    public async Task EditCancelAndQueueOffline()
    {
        // Signed in to a room, then the server goes away
        LocalServer server = await LocalServer.StartAsync();
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Notes" });
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == roomId);
            await server.DisposeAsync();

            // Act: write, then edit it while it waits
            chat.Composer = "tpyo";
            await chat.SendCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == "tpyo" && m.IsPending), "the waiting message");
            MessageViewModel waiting = chat.Messages.Items.First(m => m.Text == "tpyo");
            chat.CreateMessageActions(waiting).Actions.First(a => a.Label == "Edit").Run!();
            Assert.Equal("tpyo", chat.Composer);
            chat.Composer = "typo fixed";
            await chat.SendCommand.ExecuteAsync(null);

            // Assert: changed in place, stored as changed
            await WaitUntilAsync(() => waiting.Text == "typo fixed", "the edit");
            Assert.Contains("typo fixed", (await model.Connection!.Store.GetUnsentAsync(roomId)).Single().ContentJson);

            // Act: a pasted file, then cancel the text
            using (MemoryStream picture = new([1, 2, 3, 4])) await chat.SendFileAsync(picture, "photo.png");
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text.StartsWith("📎 photo.png", StringComparison.Ordinal)), "the waiting file");
            MessageViewModel file = chat.Messages.Items.First(m => m.Text.StartsWith("📎 photo.png", StringComparison.Ordinal));
            Assert.DoesNotContain(chat.CreateMessageActions(file).Actions, a => a.Label == "Edit");
            chat.CreateMessageActions(waiting).Actions.First(a => a.Label == "Cancel sending").Run!();

            // Done: only the file waits
            await WaitUntilAsync(() => !chat.Messages.Items.Contains(waiting), "the cancelled message gone");
            Assert.Single(await model.Connection!.Store.GetUnsentAsync(roomId));
        }
        finally
        {
            await model.StopAsync();
        }
    }
}
