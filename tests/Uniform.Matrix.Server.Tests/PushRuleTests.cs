namespace Uniform.Matrix.Server.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;

/// <summary>Tests of push rules and the unread counts of sync, through our own client.</summary>
public class PushRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };

    /// <summary>Register a user and return its client.</summary>
    private static async Task<MatrixClient> UserAsync(TestHomeserver server, string name)
    {
        // Register logs in
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync(name, "password", cancellationToken: Ct);
        // Return client
        return client;
    }

    /// <summary>Return Alice's unread counts of <paramref name="roomId"/> from an initial sync.</summary>
    private static async Task<(int? Notifications, int? Highlights)> CountsAsync(MatrixClient client, string roomId)
    {
        // Fresh sync
        SyncResponse sync = await client.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);
        UnreadNotificationCounts? counts = sync.Rooms?.Join[roomId].UnreadNotifications;
        // Return counts
        return (counts?.NotificationCount, counts?.HighlightCount);
    }

    /// <summary>Messages by others count until read; mentions also count as highlights; own messages and a read receipt clear the counts.</summary>
    [Fact]
    public async Task CountUnread()
    {
        // Arrange: Alice and Bob in a room, with three members so it is not a one-to-one chat
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act: two messages, one mentioning Alice, and an edit and a reaction that do not count
        string hello = await bob.SendTextAsync(roomId, "hello", Ct);
        string mention = await bob.SendHtmlAsync(roomId, "alice: look", "alice: look", new Mentions { UserIds = [alice.UserId!] }, Ct);
        await bob.SendEventAsync(roomId, EventTypes.RoomMessage, MatrixClient.CreateEditContent(hello, "hello there"), cancellationToken: Ct);
        await bob.SendEventAsync(roomId, EventTypes.Reaction, MatrixClient.CreateReactionContent(mention, "👍"), cancellationToken: Ct);

        // Assert
        Assert.Equal((2, 1), await CountsAsync(alice, roomId));
        Assert.Equal((0, 0), await CountsAsync(bob, roomId));

        // Act and assert: a read receipt clears; a new message counts again; Alice's own message clears
        await alice.SendReceiptAsync(roomId, mention, cancellationToken: Ct);
        Assert.Equal((0, 0), await CountsAsync(alice, roomId));
        await carol.SendTextAsync(roomId, "and me", Ct);
        Assert.Equal((1, 0), await CountsAsync(alice, roomId));
        await alice.SendTextAsync(roomId, "hi both", Ct);
        Assert.Equal((0, 0), await CountsAsync(alice, roomId));
    }

    /// <summary>The rules come with the server's defaults in sync and from the API; a muted room stops counting; default rules can be disabled but not deleted.</summary>
    [Fact]
    public async Task ChangeRules()
    {
        // Arrange: three members, as one-to-one rooms have rules of their own
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Assert: defaults in the initial sync and the API
        SyncResponse first = await alice.SyncAsync(timeout: TimeSpan.Zero, cancellationToken: Ct);
        Assert.Contains(first.AccountData?.Events ?? [], e => e.Type == "m.push_rules");
        JsonElement rules = await alice.GetPushRulesAsync(Ct);
        Assert.Equal(".m.rule.master", rules.GetProperty("override")[0].GetProperty("rule_id").GetString());

        // Act: mute the room, and disable the message rule
        await alice.SetPushRuleAsync("room", roomId, new { actions = Array.Empty<object>() }, cancellationToken: Ct);
        await bob.SendTextAsync(roomId, "you will not hear this", Ct);
        (int? muted, _) = await CountsAsync(alice, roomId);
        await alice.DeletePushRuleAsync("room", roomId, Ct);
        await alice.SetPushRuleEnabledAsync("underride", ".m.rule.message", false, Ct);
        (int? disabled, _) = await CountsAsync(alice, roomId);
        await alice.SetPushRuleEnabledAsync("underride", ".m.rule.message", true, Ct);
        (int? enabledAgain, _) = await CountsAsync(alice, roomId);

        // Assert
        Assert.Equal(0, muted);
        Assert.Equal(0, disabled);
        Assert.Equal(1, enabledAgain);
        MatrixException notDeletable = await Assert.ThrowsAsync<MatrixException>(() => alice.DeletePushRuleAsync("underride", ".m.rule.message", Ct));
        Assert.Equal(ErrorCodes.InvalidParam, notDeletable.Errcode);
        MatrixException reserved = await Assert.ThrowsAsync<MatrixException>(() => alice.SetPushRuleAsync("override", ".m.rule.mine", new { actions = new[] { "notify" } }, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.InvalidParam, reserved.Errcode);
    }

    /// <summary>A fake push gateway: records the notifications posted to it and answers with <see cref="Rejected"/>.</summary>
    private sealed class Gateway : HttpMessageHandler
    {
        /// <summary>Bodies of the notifications so far.</summary>
        public List<JsonElement> Notifications { get; } = [];
        /// <summary>Push keys to reject in the answers.</summary>
        public List<string> Rejected { get; } = [];

        /// <summary>Record the body and answer.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Body, then the answer
            JsonElement body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            lock (Notifications) Notifications.Add(body);
            // Return rejected keys
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(new { rejected = Rejected }) };
        }
    }

    /// <summary>Wait until the gateway has <paramref name="count"/> notifications.</summary>
    private static async Task WaitForAsync(Gateway gateway, int count)
    {
        // Poll; the notifier works in the background
        for (int i = 0; i < 100; i++)
        {
            lock (gateway.Notifications) if (gateway.Notifications.Count >= count) return;
            await Task.Delay(50, Ct);
        }
        // On error fail
        Assert.Fail($"The gateway got fewer than {count} notifications.");
    }

    /// <summary>A pusher gets notifications of messages by others, with counts, tweaks and names; a rejected key deletes the pusher; bad gateway URLs are refused, and logging out deletes the pusher.</summary>
    [Fact]
    public async Task PushToGateway()
    {
        // Arrange: a fake gateway, and Alice with a pusher in a room with Bob and Carol
        Gateway gateway = new();
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open, configureServices: services => services.AddHttpClient(PushNotifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway));
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Lounge", Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);
        object Pusher(string key) => new { pushkey = key, kind = "http", app_id = "io.example.app", app_display_name = "Example", device_display_name = "Phone", lang = "en", data = new { url = "https://push.example.org/_matrix/push/v1/notify" } };
        await alice.SetPusherAsync(Pusher("key-1"), Ct);

        // Act: an ordinary message, then a mention
        await bob.SendTextAsync(roomId, "hello", Ct);
        await WaitForAsync(gateway, 1);
        await bob.SendHtmlAsync(roomId, "alice: look", "alice: look", new Mentions { UserIds = [alice.UserId!] }, Ct);
        await WaitForAsync(gateway, 2);

        // Assert: what the gateway got
        JsonElement first = gateway.Notifications[0].GetProperty("notification");
        Assert.Equal(roomId, first.GetProperty("room_id").GetString());
        Assert.Equal(bob.UserId, first.GetProperty("sender").GetString());
        Assert.Equal("Lounge", first.GetProperty("room_name").GetString());
        Assert.Equal("hello", first.GetProperty("content").GetProperty("body").GetString());
        Assert.Equal(1, first.GetProperty("counts").GetProperty("unread").GetInt32());
        JsonElement device = first.GetProperty("devices")[0];
        Assert.Equal("key-1", device.GetProperty("pushkey").GetString());
        Assert.False(device.GetProperty("data").TryGetProperty("url", out _));
        JsonElement second = gateway.Notifications[1].GetProperty("notification");
        Assert.True(second.GetProperty("devices")[0].GetProperty("tweaks").GetProperty("highlight").GetBoolean());
        Assert.Equal("high", second.GetProperty("prio").GetString());
        Assert.Equal(2, second.GetProperty("counts").GetProperty("unread").GetInt32());

        // Act and assert: the gateway rejects the key, which deletes the pusher
        gateway.Rejected.Add("key-1");
        await carol.SendTextAsync(roomId, "me too", Ct);
        await WaitForAsync(gateway, 3);
        for (int i = 0; i < 50 && (await alice.GetPushersAsync(Ct)).GetArrayLength() > 0; i++) await Task.Delay(50, Ct);
        Assert.Equal(0, (await alice.GetPushersAsync(Ct)).GetArrayLength());

        // Act and assert: bad URLs are refused; logging out deletes a pusher
        MatrixException badUrl = await Assert.ThrowsAsync<MatrixException>(() => alice.SetPusherAsync(new { pushkey = "k", kind = "http", app_id = "a", app_display_name = "A", device_display_name = "D", lang = "en", data = new { url = "https://push.example.org/elsewhere" } }, Ct));
        Assert.Equal(ErrorCodes.InvalidParam, badUrl.Errcode);
        await alice.SetPusherAsync(Pusher("key-2"), Ct);
        Assert.Equal(1, (await alice.GetPushersAsync(Ct)).GetArrayLength());
        MatrixClient aliceAgain = server.CreateClient();
        await aliceAgain.LoginWithPasswordAsync("alice", "password", cancellationToken: Ct);
        await alice.LogoutAsync(Ct);
        Assert.Equal(0, (await aliceAgain.GetPushersAsync(Ct)).GetArrayLength());
    }

    /// <summary>The gateway client refuses private and loopback addresses unless the setting allows them, before connecting.</summary>
    [Fact]
    public async Task RefusePrivateGateways()
    {
        // Arrange: the real handler with the default setting
        IOptionsMonitor<PushOptions> options = new StaticOptions(new PushOptions());
        using HttpClient http = new(PushNotifier.CreateHandler(options));

        // Act
        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("http://127.0.0.1:9/_matrix/push/v1/notify", new StringContent("{}"), Ct));

        // Assert: refused by the check, not by a failed connection
        Assert.Contains("private address", refused.ToString());
    }

    /// <summary>Options that never change.</summary>
    private sealed class StaticOptions(PushOptions value) : IOptionsMonitor<PushOptions>
    {
        /// <summary>The value.</summary>
        public PushOptions CurrentValue => value;

        /// <summary>The value, for any name.</summary>
        public PushOptions Get(string? name) => value;

        /// <summary>No changes to listen to.</summary>
        public IDisposable? OnChange(Action<PushOptions, string?> listener) => null;
    }
}
