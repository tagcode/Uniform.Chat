namespace Uniform.Chat.Avalonia.Tests;

using System.Text.Json;
using global::Avalonia.Headless.XUnit;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Protocol.Client;

/// <summary>Tests of push through UnifiedPush: the pusher follows the distributor's endpoint, the app's own background connection stops while push works, and push messages wake the sync.</summary>
public class PushTests
{
    /// <summary>A distributor in memory: registering gives an endpoint at once.</summary>
    private sealed class FakePush : IPushService
    {
        /// <summary>The endpoint registering gives.</summary>
        public const string GivenEndpoint = "https://push.example.org/up/abc123";

        public string? Distributor { get; private set; }
        public string? Endpoint { get; private set; }
        public event Action? Changed;
        public Func<byte[], Task>? MessageHandler { get; set; }
        public IReadOnlyList<PushDistributor> GetDistributors() => [new("org.example.ntfy", "ntfy")];

        public void Register(string distributor)
        {
            Distributor = distributor;
            Endpoint = GivenEndpoint;
            Changed?.Invoke();
        }

        public void Unregister()
        {
            Distributor = null;
            Endpoint = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Choosing a distributor sets the pusher and stops the background connection; choosing the own connection removes the pusher and starts it again; a push message completes after a sync.</summary>
    [AvaloniaFact]
    public async Task FollowDistributor()
    {
        // Arrange: a platform with a push service and a counted background connection
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient kim = await server.RegisterAsync("kim");
        FakePush push = new();
        int kept = 0;
        GuiPlatform platform = server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName) with
        {
            Push = push,
            KeepConnected = () => { Interlocked.Increment(ref kept); return new Stopper(() => Interlocked.Decrement(ref kept)); },
        };
        MainViewModel model = new(platform, new AvaloniaUiDispatcher());

        try
        {
            await ChatFlowTests.SignInAsync(model, kim.UserId!);
            Assert.Equal(1, kept);
            Assert.Equal(["Own background connection", "UnifiedPush: ntfy"], model.Settings.PushChoices);

            // Act and assert: a distributor
            model.Settings.PushChoiceIndex = 1;
            await ChatFlowTests.WaitUntilAsync(() => kept == 0, "the background connection to stop");
            await WaitForPushersAsync(kim, [FakePush.GivenEndpoint]);
            Assert.StartsWith("Messages arrive through ntfy", model.Settings.PushStatus);

            // Act and assert: a message wakes the sync
            await push.MessageHandler!(JsonSerializer.SerializeToUtf8Bytes(new { notification = new { event_id = "$x", room_id = "!y" } })).WaitAsync(TimeSpan.FromSeconds(15));

            // Act and assert: back to the own connection
            model.Settings.PushChoiceIndex = 0;
            await ChatFlowTests.WaitUntilAsync(() => kept == 1, "the background connection to start again");
            await WaitForPushersAsync(kim, []);
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Wait until the account's pushers have exactly <paramref name="keys"/>.</summary>
    private static async Task WaitForPushersAsync(MatrixClient client, IReadOnlyList<string> keys)
    {
        // Ask until they match
        for (int i = 0; i < 100; i++)
        {
            JsonElement pushers = await client.GetPushersAsync();
            List<string> found = [.. pushers.EnumerateArray().Select(p => p.GetProperty("pushkey").GetString()!)];
            if (found.SequenceEqual(keys)) return;
            await Task.Delay(100);
        }
        // On never matching
        Assert.Fail($"The pushers did not become [{string.Join(", ", keys)}].");
    }

    /// <summary>Runs an action when disposed.</summary>
    private sealed class Stopper(Action stop) : IDisposable
    {
        /// <summary>Run it.</summary>
        public void Dispose() => stop();
    }
}
