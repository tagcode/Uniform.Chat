namespace Uniform.Chat.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Uniform.LiveKit.Hosting;
using Uniform.LiveKit.Protocol;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Server;

/// <summary>Tests of the MatrixRTC authorization service inside the homeserver.</summary>
public sealed class EmbeddedLiveKitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Announce the service at the homeserver's public URL, give a member of the room a token with the homeserver's own OpenID token, creating the room in LiveKit, and refuse a user who is not joined.</summary>
    [Fact]
    public async Task IssueTokensToLocalUsers()
    {
        // A homeserver with LiveKit, whose room service is faked
        string directory = Directory.CreateTempSubdirectory("uniform-chat-livekit-").FullName;
        FakeLiveKit livekit = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Chat.Server.Tests", EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:ServerName"] = "test.local",
            ["Server:PublicBaseUrl"] = "https://matrix.test.local/",
            ["Server:Registration"] = "Open",
            ["Storage:DataDirectory"] = directory,
            ["LiveKit:Enabled"] = "true",
            ["LiveKit:Url"] = "wss://livekit.test.local",
            ["LiveKit:ApiKey"] = "key",
            ["LiveKit:ApiSecret"] = "secret",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.AddMatrixServer();
        builder.AddEmbeddedLiveKit();
        builder.Services.AddHttpClient(LiveKitHostingExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => livekit);
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        app.MapEmbeddedLiveKit();
        await app.StartAsync(Ct);
        try
        {
            // Alice in her room, and Mallory outside it
            Uri address = new(app.Urls.First() + "/");
            MatrixClient alice = new(new HttpClient { BaseAddress = address }, address);
            await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
            MatrixClient mallory = new(new HttpClient { BaseAddress = address }, address);
            await mallory.RegisterAsync("mallory", "password", cancellationToken: Ct);
            string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Call" }, Ct);
            using HttpClient http = new() { BaseAddress = address };
            JsonElement transports = await GetAsync(http, alice, "/_matrix/client/unstable/org.matrix.msc4143/rtc/transports");

            // A token for her call, none for him
            HttpResponseMessage response = await RequestTokenAsync(http, alice, "alice", roomId);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            SfuResponse? answer = await response.Content.ReadFromJsonAsync<SfuResponse>(Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, (await RequestTokenAsync(http, mallory, "mallory", roomId)).StatusCode);

            // Announced, issued with full access, and the room created
            Assert.Equal("https://matrix.test.local/livekit/jwt", transports.GetProperty("rtc_transports")[0].GetProperty("livekit_service_url").GetString());
            Assert.Equal("wss://livekit.test.local", answer!.Url);
            Assert.True(AccessToken.TryRead(answer.Jwt, "secret", DateTimeOffset.UtcNow, out AccessToken? claims));
            Assert.True(claims!.Video!.CanPublish);
            // Done
            Assert.Equal(["https://livekit.test.local/twirp/livekit.RoomService/CreateRoom"], livekit.Requests);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Return the JSON at <paramref name="path"/> as <paramref name="user"/>.</summary>
    private static async Task<JsonElement> GetAsync(HttpClient http, MatrixClient user, string path)
    {
        // Done
        using HttpRequestMessage request = new(HttpMethod.Get, path) { Headers = { Authorization = new("Bearer", user.AccessToken) } };
        return await (await http.SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Ask a LiveKit token to <paramref name="roomId"/>'s call with an OpenID token of <paramref name="user"/>, whose localpart is <paramref name="localpart"/>.</summary>
    private static async Task<HttpResponseMessage> RequestTokenAsync(HttpClient http, MatrixClient user, string localpart, string roomId)
    {
        // The user's OpenID token
        using HttpRequestMessage request = new(HttpMethod.Post, $"/_matrix/client/v3/user/%40{localpart}%3Atest.local/openid/request_token") { Headers = { Authorization = new("Bearer", user.AccessToken) }, Content = JsonContent.Create(new { }) };
        JsonElement openId = await (await http.SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        // Return answer
        return await http.PostAsJsonAsync("/livekit/jwt/get_token", new { room_id = roomId, slot_id = "m.call#ROOM", openid_token = openId, member = new { id = "m", claimed_user_id = $"@{localpart}:test.local", claimed_device_id = user.DeviceId } }, Ct);
    }

    /// <summary>Turn the service on in a running server, as the dashboard's Apply does: the endpoint answers and the homeserver announces it, without a restart.</summary>
    [Fact]
    public async Task ApplyWithoutRestart()
    {
        // A homeserver with LiveKit off
        string directory = Directory.CreateTempSubdirectory("uniform-chat-livekit-").FullName;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Chat.Server.Tests", EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:ServerName"] = "test.local", ["Server:PublicBaseUrl"] = "https://matrix.test.local/", ["Server:Registration"] = "Open", ["Storage:DataDirectory"] = directory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.AddMatrixServer();
        builder.AddEmbeddedLiveKit();
        WebApplication app = builder.Build();
        app.MapMatrixServer();
        app.MapEmbeddedLiveKit();
        await app.StartAsync(Ct);
        try
        {
            // Off: nothing announced, no service
            Uri address = new(app.Urls.First() + "/");
            MatrixClient alice = new(new HttpClient { BaseAddress = address }, address);
            await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
            using HttpClient http = new() { BaseAddress = address };
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/livekit/jwt/healthz", Ct)).StatusCode);
            Assert.Empty((await http.GetFromJsonAsync<JsonElement>("/_matrix/client/unstable/org.matrix.msc4143/rtc/transports", Ct)).GetProperty("rtc_transports").EnumerateArray());

            // Applied
            IConfigurationRoot configuration = (IConfigurationRoot)app.Configuration;
            configuration["LiveKit:Enabled"] = "true";
            configuration["LiveKit:Url"] = "wss://livekit.test.local";
            configuration["LiveKit:ApiKey"] = "key";
            configuration["LiveKit:ApiSecret"] = "secret";
            configuration.Reload();

            // Done
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/livekit/jwt/healthz", Ct)).StatusCode);
            JsonElement transports = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/unstable/org.matrix.msc4143/rtc/transports", Ct);
            Assert.Equal("https://matrix.test.local/livekit/jwt", transports.GetProperty("rtc_transports")[0].GetProperty("livekit_service_url").GetString());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Answers LiveKit's room service, and records the requests.</summary>
    private sealed class FakeLiveKit : HttpMessageHandler
    {
        /// <summary>Request URLs in order.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>Answer with an empty room.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Done
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { name = "room" }) });
        }
    }
}
