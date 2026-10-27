namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of what calls need: OpenID tokens, the MatrixRTC service address, delayed events and TURN credentials.</summary>
public class CallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Secret = "test-secret";

    /// <summary>Start a server with calls configured and alice logged in; return both and an HTTP client with her token.</summary>
    private static async Task<(TestHomeserver Server, MatrixClient Alice, HttpClient Http)> StartAsync(TimeProvider? time = null)
    {
        // Call settings, federation on for OpenID checks
        TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?>
        {
            ["Server:Registration"] = "Open",
            ["Federation:Enabled"] = "true",
            ["Calls:LiveKitServiceUrl"] = "https://rtc.example.org/livekit/jwt",
            ["Calls:TurnUris"] = "turn:turn.example.org:3478?transport=udp, turns:turn.example.org:5349",
            ["Calls:TurnSharedSecret"] = Secret,
        }, time);
        MatrixClient alice = server.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
        // Return parts
        return (server, alice, http);
    }

    /// <summary>An OpenID token names its user to services until it expires; unknown tokens name no one.</summary>
    [Fact]
    public async Task CheckOpenIdTokens()
    {
        // Arrange
        FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        (TestHomeserver server, _, HttpClient http) = await StartAsync(time);
        await using TestHomeserver owned = server;
        using HttpClient service = server.CreateHttpClient();

        // Act
        JsonElement token = await (await http.PostAsJsonAsync("/_matrix/client/v3/user/%40alice%3Atest.local/openid/request_token", new { }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        string accessToken = token.GetProperty("access_token").GetString()!;
        JsonElement user = await service.GetFromJsonAsync<JsonElement>($"/_matrix/federation/v1/openid/userinfo?access_token={accessToken}", Ct);
        HttpStatusCode unknown = (await service.GetAsync("/_matrix/federation/v1/openid/userinfo?access_token=nope", Ct)).StatusCode;
        time.Advance(TimeSpan.FromHours(2));
        HttpStatusCode expired = (await service.GetAsync($"/_matrix/federation/v1/openid/userinfo?access_token={accessToken}", Ct)).StatusCode;
        HttpStatusCode otherUser = (await http.PostAsJsonAsync("/_matrix/client/v3/user/%40bob%3Atest.local/openid/request_token", new { }, Ct)).StatusCode;

        // Assert
        Assert.Equal("test.local", token.GetProperty("matrix_server_name").GetString());
        Assert.Equal(3600, token.GetProperty("expires_in").GetInt64());
        Assert.Equal("@alice:test.local", user.GetProperty("sub").GetString());
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], [unknown, expired, otherUser]);
    }

    /// <summary>Clients find the LiveKit service through rtc/transports and the well-known, and get TURN credentials coturn accepts.</summary>
    [Fact]
    public async Task AnnounceServices()
    {
        // Arrange
        (TestHomeserver server, _, HttpClient http) = await StartAsync();
        await using TestHomeserver owned = server;

        // Act
        JsonElement transports = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/unstable/org.matrix.msc4143/rtc/transports", Ct);
        JsonElement turn = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/v3/voip/turnServer", Ct);
        JsonElement versions = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/versions", Ct);

        // Assert: the coturn scheme is HMAC-SHA1 of the username with the secret
        Assert.Equal("https://rtc.example.org/livekit/jwt", transports.GetProperty("rtc_transports")[0].GetProperty("livekit_service_url").GetString());
        string username = turn.GetProperty("username").GetString()!;
        Assert.EndsWith(":@alice:test.local", username);
        Assert.Equal(Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(username))), turn.GetProperty("password").GetString());
        Assert.Equal(["turn:turn.example.org:3478?transport=udp", "turns:turn.example.org:5349"], turn.GetProperty("uris").EnumerateArray().Select(u => u.GetString()));
        Assert.True(versions.GetProperty("unstable_features").GetProperty("org.matrix.msc4140").GetBoolean());
    }

    /// <summary>A delayed state event is sent after its delay; restarting postpones it, cancelling drops it, and sending sends it at once.</summary>
    [Fact]
    public async Task SendDelayedEvents()
    {
        // Arrange
        FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        (TestHomeserver server, MatrixClient alice, HttpClient http) = await StartAsync(time);
        await using TestHomeserver owned = server;
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        string room = Uri.EscapeDataString(roomId);

        // Act: three delayed topics of 10 seconds
        string first = await DelayAsync(http, room, "late");
        string cancelled = await DelayAsync(http, room, "never");
        string now = await DelayAsync(http, room, "now");
        await http.PostAsJsonAsync($"/_matrix/client/unstable/org.matrix.msc4140/delayed_events/{cancelled}", new { action = "cancel" }, Ct);
        await http.PostAsync($"/_matrix/client/unstable/org.matrix.msc4140/delayed_events/{now}/send", null, Ct);
        string? afterSend = await TopicAsync(alice, roomId);
        time.Advance(TimeSpan.FromSeconds(6));
        await http.PostAsJsonAsync($"/_matrix/client/unstable/org.matrix.msc4140/delayed_events/{first}", new { action = "restart" }, Ct);
        time.Advance(TimeSpan.FromSeconds(6));
        string? afterRestart = await TopicAsync(alice, roomId);
        JsonElement listed = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/unstable/org.matrix.msc4140/delayed_events", Ct);
        time.Advance(TimeSpan.FromSeconds(5));
        string? afterDelay = await WaitForTopicAsync(alice, roomId, "late");
        HttpStatusCode gone = (await http.PostAsJsonAsync($"/_matrix/client/unstable/org.matrix.msc4140/delayed_events/{first}", new { action = "send" }, Ct)).StatusCode;

        // Assert
        Assert.Equal("now", afterSend);
        Assert.Equal("now", afterRestart);
        Assert.Equal([first], listed.GetProperty("delayed_events").EnumerateArray().Select(e => e.GetProperty("delay_id").GetString()));
        Assert.Equal("late", afterDelay);
        Assert.Equal(HttpStatusCode.NotFound, gone);
    }

    /// <summary>Schedule a topic in 10 seconds; return its delay ID.</summary>
    private static async Task<string> DelayAsync(HttpClient http, string room, string topic)
    {
        // State with the delay parameter
        using HttpResponseMessage response = await http.PutAsJsonAsync($"/_matrix/client/v3/rooms/{room}/state/m.room.topic/?org.matrix.msc4140.delay=10000", new { topic }, Ct);
        // Return ID
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("delay_id").GetString()!;
    }

    /// <summary>Return the room's topic, or null.</summary>
    private static async Task<string?> TopicAsync(MatrixClient client, string roomId) => (await client.TryGetStateEventAsync(roomId, "m.room.topic", cancellationToken: Ct))?.GetProperty("topic").GetString();

    /// <summary>Return the topic once it is <paramref name="expected"/>, as timers send in the background; or what it is after a while.</summary>
    private static async Task<string?> WaitForTopicAsync(MatrixClient client, string roomId, string expected)
    {
        // Poll briefly
        string? topic = null;
        for (int i = 0; i < 50 && topic != expected; i++)
        {
            topic = await TopicAsync(client, roomId);
            if (topic != expected) await Task.Delay(50, Ct);
        }
        // Return topic
        return topic;
    }
}
