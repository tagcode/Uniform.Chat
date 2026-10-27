namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Keys;

/// <summary>Tests of federation between two servers in memory, <c>a.test</c> and <c>b.test</c>, whose federation clients reach each other through <see cref="Network"/>.</summary>
public class FederationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Header that carries the request target as sent, which the test server does not keep but Kestrel does.</summary>
    private const string RawTargetHeader = "X-Test-Raw-Target";

    /// <summary>Gives requests their raw target from <see cref="RawTargetHeader"/>, as Kestrel would, so signatures are checked over the target as sent.</summary>
    private sealed class RawTargetFilter : IStartupFilter
    {
        /// <summary>Set the raw target before the server's pipeline.</summary>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // From the header, when the network set one
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers[RawTargetHeader] is [string raw] && context.Features.Get<IHttpRequestFeature>() is IHttpRequestFeature request) request.RawTarget = raw;
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Routes requests by host name to test servers; servers join after starting, as each needs the other.</summary>
    private sealed class Network : HttpMessageHandler
    {
        /// <summary>Servers by host name.</summary>
        public Dictionary<string, HttpMessageHandler> Servers { get; } = [];

        /// <summary>Forward to the server of the host, or fail as an unknown host does.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // By host, whatever the port
            if (!Servers.TryGetValue(request.RequestUri!.Host, out HttpMessageHandler? server)) throw new HttpRequestException($"No such host {request.RequestUri.Host}");
            request.Headers.TryAddWithoutValidation(RawTargetHeader, request.RequestUri.PathAndQuery);
            // Return the server's answer
            return new HttpMessageInvoker(server, false).SendAsync(request, cancellationToken);
        }

        /// <summary>Keep working when the client factory lets go of the handler.</summary>
        protected override void Dispose(bool disposing) { }
    }

    /// <summary>Start a federating server named <paramref name="serverName"/> on <paramref name="network"/>.</summary>
    private static async Task<TestHomeserver> StartAsync(string serverName, Network network)
    {
        // Federation on, its client on the test network
        TestHomeserver server = await TestHomeserver.StartAsync(
            new Dictionary<string, string?> { ["Server:ServerName"] = serverName, ["Server:Registration"] = "Open", ["Federation:Enabled"] = "true" },
            configureServices: services =>
            {
                services.AddHttpClient(FederationClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => network);
                services.AddTransient<IStartupFilter, RawTargetFilter>();
            });
        network.Servers[serverName] = server.CreateHandler();
        // Return server
        return server;
    }

    /// <summary>A user of one server sees the profile of a user of the other, which their server asks for with a signed request.</summary>
    [Fact]
    public async Task RemoteProfile()
    {
        // Arrange
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        await bob.SetDisplayNameAsync("Bob of B", Ct);

        // Act
        ProfileResponse profile = await alice.GetProfileAsync("@bob:b.test", Ct);
        MatrixException missing = await Assert.ThrowsAsync<MatrixException>(() => alice.GetProfileAsync("@nobody:b.test", Ct));
        MatrixException unreachable = await Assert.ThrowsAsync<MatrixException>(() => alice.GetProfileAsync("@carol:c.test", Ct));

        // Assert
        Assert.Equal("Bob of B", profile.DisplayName);
        Assert.Equal(ErrorCodes.NotFound, missing.Errcode);
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
    }

    /// <summary>A user of one server joins a room of the other: both servers then have them as a member, and the joining server has the room's state.</summary>
    [Fact]
    public async Task JoinRemoteRoom()
    {
        // Arrange: bob's public room on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Kitchen" }, Ct);

        // Act
        string joined = await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        string again = await alice.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Assert: membership on both sides, the state here
        Assert.Equal([roomId, roomId], [joined, again]);
        Assert.Contains(roomId, await alice.GetJoinedRoomsAsync(Ct));
        Assert.Equal(["@alice:a.test", "@bob:b.test"], (await alice.GetJoinedMembersAsync(roomId, Ct)).Keys.Order());
        Assert.Equal(["@alice:a.test", "@bob:b.test"], (await bob.GetJoinedMembersAsync(roomId, Ct)).Keys.Order());
        Assert.Equal("Kitchen", (await alice.TryGetStateEventAsync(roomId, "m.room.name", cancellationToken: Ct))?.GetProperty("name").GetString());
    }

    /// <summary>Joining a room the other server does not have, or a private room, fails with that server's error.</summary>
    [Fact]
    public async Task RefuseRemoteJoins()
    {
        // Arrange: bob's private room
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string privateRoom = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PrivateChat }, Ct);

        // Act
        MatrixException unknown = await Assert.ThrowsAsync<MatrixException>(() => alice.JoinRoomAsync("!nothing:b.test", cancellationToken: Ct));
        MatrixException forbidden = await Assert.ThrowsAsync<MatrixException>(() => alice.JoinRoomAsync(privateRoom, cancellationToken: Ct));

        // Assert
        Assert.Equal(ErrorCodes.NotFound, unknown.Errcode);
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Errcode);
        Assert.DoesNotContain(privateRoom, await alice.GetJoinedRoomsAsync(Ct));
    }

    /// <summary>The key response names the server and is signed by its key.</summary>
    [Fact]
    public async Task ServeKeys()
    {
        // Arrange
        Network network = new();
        await using TestHomeserver b = await StartAsync("b.test", network);
        using HttpClient http = b.CreateHttpClient();
        ServerKeys keys = b.Services.GetRequiredService<ServerKeys>();

        // Act
        JsonObject response = (await http.GetFromJsonAsync<JsonObject>("/_matrix/key/v2/server", Ct))!;
        JsonObject version = (await http.GetFromJsonAsync<JsonObject>("/_matrix/federation/v1/version", Ct))!;

        // Assert
        Assert.Equal("b.test", response["server_name"]!.GetValue<string>());
        Assert.Equal(keys.PublicKey, response["verify_keys"]![keys.KeyId]!["key"]!.GetValue<string>());
        Assert.True(Ed25519.Verify(keys.PublicKey, CanonicalJson.SigningForm(response), CanonicalJson.GetSignature(response, "b.test", keys.KeyId)!));
        Assert.True(response["valid_until_ts"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal("Uniform.Matrix", version["server"]!["name"]!.GetValue<string>());
    }

    /// <summary>Unsigned requests, requests for another destination and signatures that do not match are refused with <c>M_UNAUTHORIZED</c>.</summary>
    [Fact]
    public async Task RefuseBadSignatures()
    {
        // Arrange: a.test's key signs requests sent by hand
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        await b.CreateClient().RegisterAsync("bob", "password", cancellationToken: Ct);
        ServerKeys keys = a.Services.GetRequiredService<ServerKeys>();
        using HttpClient http = b.CreateHttpClient();
        const string uri = "/_matrix/federation/v1/query/profile?user_id=%40bob%3Ab.test";

        // Act
        HttpStatusCode valid = await SendAsync(http, uri, FederationAuth.Sign("GET", uri, "a.test", "b.test", null, keys.KeyId, keys.Secret).ToString());
        HttpStatusCode unsigned = await SendAsync(http, uri, null);
        HttpStatusCode otherDestination = await SendAsync(http, uri, FederationAuth.Sign("GET", uri, "a.test", "c.test", null, keys.KeyId, keys.Secret).ToString());
        HttpStatusCode otherUri = await SendAsync(http, uri, FederationAuth.Sign("GET", uri + "&field=displayname", "a.test", "b.test", null, keys.KeyId, keys.Secret).ToString());
        HttpStatusCode unknownKey = await SendAsync(http, uri, FederationAuth.Sign("GET", uri, "a.test", "b.test", null, "ed25519:other", keys.Secret).ToString());

        // Assert
        Assert.Equal(HttpStatusCode.OK, valid);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized], [unsigned, otherDestination, otherUri, unknownKey]);
    }

    /// <summary>With federation off, which is the default, federation endpoints are not there and remote profiles are not asked for.</summary>
    [Fact]
    public async Task OffByDefault()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
        using HttpClient http = server.CreateHttpClient();
        MatrixClient alice = server.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);

        // Act
        using HttpResponseMessage keys = await http.GetAsync("/_matrix/key/v2/server", Ct);
        MatrixException remote = await Assert.ThrowsAsync<MatrixException>(() => alice.GetProfileAsync("@bob:b.test", Ct));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, keys.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, remote.Errcode);
    }

    /// <summary>Send a GET with an optional authorization; return the status.</summary>
    private static async Task<HttpStatusCode> SendAsync(HttpClient http, string uri, string? authorization)
    {
        // Request
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using HttpResponseMessage response = await http.SendAsync(request, Ct);
        // Return status
        return response.StatusCode;
    }
}
