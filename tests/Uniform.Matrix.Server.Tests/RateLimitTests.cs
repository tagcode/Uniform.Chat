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

    /// <summary>Behind a trusted proxy, logins count per forwarded client address; an untrusted sender's header is ignored.</summary>
    [Fact]
    public async Task CountForwardedAddresses()
    {
        // Arrange: one login at once per address, nginx at 10.0.0.1
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["RateLimits:LoginBurst"] = "1", ["RateLimits:LoginsPerMinute"] = "1", ["Server:TrustedProxies"] = "10.0.0.1" });

        // Act: two clients through the proxy, then one forging the header from elsewhere, twice
        int first = await LoginStatusAsync(server, "10.0.0.1", "203.0.113.1");
        int second = await LoginStatusAsync(server, "10.0.0.1", "203.0.113.2");
        int again = await LoginStatusAsync(server, "10.0.0.1", "203.0.113.1");
        int forged = await LoginStatusAsync(server, "198.51.100.7", "203.0.113.3");
        int forgedAgain = await LoginStatusAsync(server, "198.51.100.7", "203.0.113.4");

        // Assert: each client address has its own bucket; the forger is counted by its own address
        Assert.Equal([false, false, true, false, true], new[] { first, second, again, forged, forgedAgain }.Select(s => s == 429));
    }

    /// <summary>Send a login with a wrong password from <paramref name="connection"/> forwarding for <paramref name="client"/>; return the status.</summary>
    private static async Task<int> LoginStatusAsync(TestHomeserver server, string connection, string client)
    {
        // A request with the connection's address and the forwarded header
        Microsoft.AspNetCore.Http.HttpContext context = await server.SendAsync(http =>
        {
            http.Request.Method = "POST";
            http.Request.Path = "/_matrix/client/v3/login";
            http.Request.ContentType = "application/json";
            http.Request.Headers["X-Forwarded-For"] = client;
            http.Connection.RemoteIpAddress = IPAddress.Parse(connection);
            byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"m.login.password\",\"identifier\":{\"type\":\"m.id.user\",\"user\":\"nobody\"},\"password\":\"x\"}");
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentLength = body.Length;
        });
        // Return status
        return context.Response.StatusCode;
    }
}
