namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.VisualTree;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>The text size setting scales the whole chat page: the titles, the messages and the smaller texts around them.</summary>
public class TextSizeTests
{
    /// <summary>Each text size makes the app name, the room title, a message, its time, a heading and the message field larger or smaller together, at once.</summary>
    [AvaloniaFact]
    public async Task ScaleChatPage()
    {
        // Arrange: Alice in Bob's room with a message, shown in a window
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await bob.SendTextAsync(roomId, "hello");
        await bob.SendEventAsync(roomId, EventTypes.RoomMessage, new { msgtype = "m.text", body = "Title", format = "org.matrix.custom.html", formatted_body = "<h1>Title</h1>" });
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        Window window = new() { Width = 900, Height = 600, Content = new MainView { DataContext = model } };

        try
        {
            ChatViewModel chat = await ChatFlowTests.SignInAsync(model, alice.UserId!);
            await ChatFlowTests.WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == roomId), "the room");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == roomId);
            window.Show();
            await ChatFlowTests.WaitUntilAsync(() => Texts(window).OfType<RichText>().Any(t => t.Html == "<h1>Title</h1>"), "the messages");

            // Act and assert: every size, smallest to largest, grows each text, the heading of a formatted message too
            double[] previous = [0, 0, 0, 0, 0, 0];
            for (int index = 0; index < model.Settings.TextSizes.Count; index++)
            {
                model.Settings.TextSizeIndex = index;
                await Task.Delay(100);
                TextBlock[] texts = [.. Texts(window)];
                double[] sizes =
                [
                    texts.Single(t => t.Text == "Uniform Chat" && t.Classes.Contains("title")).FontSize,
                    texts.Single(t => t.Text == "Kitchen" && t.Classes.Contains("title")).FontSize,
                    texts.OfType<RichText>().Single(t => t.Body == "hello").FontSize,
                    texts.First(t => t.Classes.Contains("time")).FontSize,
                    texts.OfType<RichText>().Single(t => t.Html == "<h1>Title</h1>").Inlines!.OfType<Run>().First().FontSize,
                    window.GetVisualDescendants().OfType<TextBox>().First().FontSize,
                ];
                Assert.Equal(model.Settings.TextSize, sizes[2]);
                for (int i = 0; i < sizes.Length; i++) Assert.True(sizes[i] > previous[i], $"Text {i} at size {index}: {sizes[i]}, before {previous[i]}");
                previous = sizes;
            }
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>Return the texts shown in <paramref name="window"/>.</summary>
    private static IEnumerable<TextBlock> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>();
}
