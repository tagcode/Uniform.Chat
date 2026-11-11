namespace Uniform.LiveKit.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Uniform.LiveKit.Hosting;
using Uniform.LiveKit.Protocol;
using Uniform.LiveKit.Server;

/// <summary>Tests of the service's endpoints in a host, and of the standalone app's settings.</summary>
public sealed class HostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Start a host of the service under <c>/livekit/jwt</c>, reaching homeservers and LiveKit through <paramref name="network"/>.</summary>
    private static async Task<WebApplication> StartAsync(FakeNetwork network, bool enabled = true)
    {
        // Settings, and the fake network under the HTTP client
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.LiveKit.Tests", EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["LiveKit:Enabled"] = enabled.ToString(), ["LiveKit:Url"] = "wss://livekit.example.org", ["LiveKit:ApiKey"] = "key", ["LiveKit:ApiSecret"] = "secret", ["LiveKit:FullAccessHomeservers"] = "example.org" });
        builder.AddLiveKitAuthorization();
        builder.Services.AddHttpClient(LiveKitHostingExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => network);
        WebApplication app = builder.Build();
        app.MapLiveKitAuthorization("/livekit/jwt");
        await app.StartAsync(Ct);
        // Return host
        return app;
    }

    /// <summary>Answer <c>/get_token</c> and <c>/sfu/get</c> with LiveKit's URL and a token, preflights with CORS, and failures in Matrix style.</summary>
    [Fact]
    public async Task ServeTokens()
    {
        // A host and Alice's token
        FakeNetwork network = new();
        network.Users["alice-token"] = "@alice:example.org";
        await using WebApplication app = await StartAsync(network);
        HttpClient http = app.GetTestClient();
        object openId = new { access_token = "alice-token", token_type = "Bearer", matrix_server_name = "example.org", expires_in = 3600 };

        // Both requests give a token
        HttpResponseMessage response = await http.PostAsJsonAsync("/livekit/jwt/get_token", new { room_id = "!room:example.org", slot_id = "m.call#ROOM", openid_token = openId, member = new { id = "m", claimed_user_id = "@alice:example.org", claimed_device_id = "D" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        SfuResponse? answer = await response.Content.ReadFromJsonAsync<SfuResponse>(Ct);
        Assert.Equal("wss://livekit.example.org", answer!.Url);
        Assert.True(AccessToken.TryRead(answer.Jwt, "secret", DateTimeOffset.UtcNow, out _));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/livekit/jwt/sfu/get", new { room = "!room:example.org", device_id = "D", openid_token = openId }, Ct)).StatusCode);

        // Preflight, other methods, bad bodies and refusals
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/livekit/jwt/get_token"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync("/livekit/jwt/get_token", Ct)).StatusCode);
        HttpResponseMessage bad = await http.PostAsync("/livekit/jwt/get_token", new StringContent("{", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(AuthorizationError.BadJson, JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errcode").GetString());
        HttpResponseMessage refused = await http.PostAsJsonAsync("/livekit/jwt/sfu/get", new { room = "!room:example.org", device_id = "D", openid_token = new { access_token = "nope", matrix_server_name = "example.org" } }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        // Done
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/livekit/jwt/healthz", Ct)).StatusCode);
    }

    /// <summary>Answer nothing but M_UNRECOGNIZED while disabled.</summary>
    [Fact]
    public async Task StayOffWhenDisabled()
    {
        // A disabled host
        await using WebApplication app = await StartAsync(new FakeNetwork(), enabled: false);
        HttpResponseMessage response = await app.GetTestClient().PostAsJsonAsync("/livekit/jwt/get_token", new { }, Ct);

        // Done
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(AuthorizationError.Unrecognized, JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errcode").GetString());
    }

    /// <summary>Read lk-jwt-service's environment variables as settings, and tell what is missing.</summary>
    [Fact]
    public void ReadCompatibleEnvironment()
    {
        // Variables of a container
        Dictionary<string, string> environment = new() { ["LIVEKIT_URL"] = "wss://livekit.example.org", ["LIVEKIT_KEY"] = "key", ["LIVEKIT_SECRET"] = "secret", ["LIVEKIT_FULL_ACCESS_HOMESERVERS"] = "example.org,other.org", ["LIVEKIT_JWT_BIND"] = ":8081" };
        Dictionary<string, string?> settings = CompatibleEnvironment.Read(name => environment.GetValueOrDefault(name));

        // Done
        Assert.Equal("wss://livekit.example.org", settings["LiveKit:Url"]);
        Assert.Equal("secret", settings["LiveKit:ApiSecret"]);
        Assert.Equal("http://0.0.0.0:8081", settings["Urls"]);
        LiveKitOptions options = new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(LiveKitOptions.Section).Get<LiveKitOptions>()!;
        Assert.Null(CompatibleEnvironment.Missing(options));
        Assert.Equal(["example.org", "other.org"], options.ToAuthorizer().FullAccessHomeservers);
        Assert.Contains("secret", CompatibleEnvironment.Missing(new LiveKitOptions { Url = "wss://x", ApiKey = "k" }));
    }
}
