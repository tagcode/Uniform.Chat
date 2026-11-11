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
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Server;

/// <summary>Tests of the MatrixRTC authorization service inside the homeserver.</summary>
public sealed class EmbeddedLiveKitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Announce the service at the homeserver's public URL, and give a local user a token with the homeserver's own OpenID token, creating the room in LiveKit.</summary>
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
            // Alice and her OpenID token
            Uri address = new(app.Urls.First() + "/");
            MatrixClient alice = new(new HttpClient { BaseAddress = address }, address);
            await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
            using HttpClient http = new() { BaseAddress = address };
            http.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
            JsonElement transports = await http.GetFromJsonAsync<JsonElement>("/_matrix/client/unstable/org.matrix.msc4143/rtc/transports", Ct);
            JsonElement openId = await (await http.PostAsJsonAsync("/_matrix/client/v3/user/%40alice%3Atest.local/openid/request_token", new { }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

            // A token for her call
            HttpResponseMessage response = await http.PostAsJsonAsync("/livekit/jwt/get_token", new { room_id = "!room:test.local", slot_id = "m.call#ROOM", openid_token = openId, member = new { id = "m", claimed_user_id = "@alice:test.local", claimed_device_id = alice.DeviceId } }, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            SfuResponse? answer = await response.Content.ReadFromJsonAsync<SfuResponse>(Ct);

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
