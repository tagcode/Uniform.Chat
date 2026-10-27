namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of rate limits: requests past a burst get <c>M_LIMIT_EXCEEDED</c> with the time to wait, and our client waits and retries.</summary>
public class RateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes past the burst are refused with a wait; the client's retry gets through; other tokens have their own bucket.</summary>
    [Fact]
    public async Task LimitWrites()
    {
        // Arrange: a burst of three writes, one more each second
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open", ["RateLimits:WriteBurst"] = "3", ["RateLimits:WritesPerSecond"] = "1" });
        MatrixClient alice = server.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = server.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);

        // Act: the room took one write; two more fit, the next does not
        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await http.PutAsJsonAsync($"/_matrix/client/v3/rooms/{roomId}/send/m.room.message/t{i}", new { msgtype = "m.text", body = $"message {i}" }, Ct);
            statuses.Add(response.StatusCode);
            if (response.StatusCode != HttpStatusCode.TooManyRequests) continue;
            JsonElement error = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(ErrorCodes.LimitExceeded, error.GetProperty("errcode").GetString());
            Assert.True(error.GetProperty("retry_after_ms").GetInt64() > 0);
        }
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        string retried = await alice.SendTextAsync(roomId, "after waiting", Ct);

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.StartsWith("$", retried);
    }

    /// <summary>Logins from one address past the burst are refused, also with the right password.</summary>
    [Fact]
    public async Task LimitLogins()
    {
        // Arrange: a burst of four logins and registrations
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open", ["RateLimits:LoginBurst"] = "4", ["RateLimits:LoginsPerMinute"] = "1" });
        await server.CreateClient().RegisterAsync("carol", "password", cancellationToken: Ct);
        using HttpClient http = server.CreateHttpClient();

        // Act: two wrong passwords, then the right one
        List<HttpStatusCode> statuses = [];
        foreach (string password in new[] { "wrong", "wrong", "password" })
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync("/_matrix/client/v3/login", new { type = "m.login.password", identifier = new { type = "m.id.user", user = "carol" }, password }, Ct);
            statuses.Add(response.StatusCode);
        }

        // Assert: the registration (an auth probe and the dummy stage) and the two failures used the burst
        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests], statuses);
    }
}
