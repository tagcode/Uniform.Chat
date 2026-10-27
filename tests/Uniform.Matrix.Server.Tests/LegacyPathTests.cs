namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Tests of the <c>r0</c> paths older clients use.</summary>
public class LegacyPathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Logging in and syncing work through <c>r0</c> paths as through <c>v3</c>; the unstable key backup path reaches key backup (none yet, so not found), and bridges are listed as none.</summary>
    [Fact]
    public async Task ServeR0Paths()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
        await server.CreateClient().RegisterAsync("alice", "password", cancellationToken: Ct);
        using HttpClient http = server.CreateHttpClient();

        // Act: the login flows, a login and a sync, all on r0
        JsonElement flows = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/r0/login", Ct);
        using HttpResponseMessage login = await http.PostAsJsonAsync("/_matrix/client/r0/login", new { type = "m.login.password", identifier = new { type = "m.id.user", user = "alice" }, password = "password" }, Ct);
        string token = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        JsonElement sync = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/r0/sync?timeout=0", Ct);
        HttpStatusCode backup = (await http.GetAsync("/_matrix/client/unstable/room_keys/version", Ct)).StatusCode;
        JsonElement protocols = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/r0/thirdparty/protocols", Ct);

        // Assert
        Assert.Equal("m.login.password", flows.GetProperty("flows")[0].GetProperty("type").GetString());
        Assert.True(sync.TryGetProperty("next_batch", out _));
        Assert.Equal(HttpStatusCode.NotFound, backup);
        Assert.Equal(JsonValueKind.Object, protocols.ValueKind);
    }
}
