namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Storage;

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
    internal sealed class Network : HttpMessageHandler
    {
        /// <summary>Servers by host name.</summary>
        public Dictionary<string, HttpMessageHandler> Servers { get; } = [];
        /// <summary>Transactions to fail by host, as if the server were down.</summary>
        public Dictionary<string, int> FailTransactions { get; } = [];
        /// <summary>Hosts that refuse transactions with 400, so senders drop them and events go missing.</summary>
        public HashSet<string> RefuseTransactions { get; } = [];

        /// <summary>Forward to the server of the host, or fail as an unknown host does.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // By host, whatever the port
            if (!Servers.TryGetValue(request.RequestUri!.Host, out HttpMessageHandler? server)) throw new HttpRequestException($"No such host {request.RequestUri.Host}");
            request.Headers.TryAddWithoutValidation(RawTargetHeader, request.RequestUri.PathAndQuery);
            lock (FailTransactions)
            {
                if (request.RequestUri.AbsolutePath.Contains("/federation/v1/send/") && RefuseTransactions.Contains(request.RequestUri.Host))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"errcode\":\"M_BAD_JSON\",\"error\":\"refused by the test\"}") });
                }
                if (request.RequestUri.AbsolutePath.Contains("/federation/v1/send/") && FailTransactions.GetValueOrDefault(request.RequestUri.Host) is int failures and > 0)
                {
                    FailTransactions[request.RequestUri.Host] = failures - 1;
                    throw new HttpRequestException($"{request.RequestUri.Host} is down");
                }
            }
            // Return the server's answer; a stopped server fails like a closed port
            return SendToAsync(server, request, cancellationToken);
        }

        /// <summary>Send to a test server, turning the errors of one that stopped into the connection errors of a real network.</summary>
        private static async Task<HttpResponseMessage> SendToAsync(HttpMessageHandler server, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Stopped servers throw their own errors
            try
            {
                return await new HttpMessageInvoker(server, false).SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // On error as a refused connection
                throw new HttpRequestException($"{request.RequestUri!.Host} is not running", ex);
            }
        }

        /// <summary>Keep working when the client factory lets go of the handler.</summary>
        protected override void Dispose(bool disposing) { }
    }

    /// <summary>Start a federating server named <paramref name="serverName"/> on <paramref name="network"/>, in <paramref name="dataDirectory"/> when given, e.g. to restart it, with <paramref name="extra"/> settings.</summary>
    internal static async Task<TestHomeserver> StartAsync(string serverName, Network network, string? dataDirectory = null, IReadOnlyDictionary<string, string?>? extra = null)
    {
        // Federation on, its client on the test network; a given data directory outlives the server
        Dictionary<string, string?> settings = new() { ["Server:ServerName"] = serverName, ["Server:Registration"] = "Open", ["Federation:Enabled"] = "true" };
        if (dataDirectory is not null) settings["Storage:DataDirectory"] = dataDirectory;
        foreach ((string key, string? value) in extra ?? new Dictionary<string, string?>()) settings[key] = value;
        TestHomeserver server = await TestHomeserver.StartAsync(
            settings,
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

    /// <summary>After a join, messages of either server reach the other through transactions.</summary>
    [Fact]
    public async Task ExchangeMessages()
    {
        // Arrange: alice of a.test in bob's room on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);

        // Act
        await bob.SendTextAsync(roomId, "hello from b", Ct);
        IReadOnlyList<string> atA = await WaitForBodiesAsync(alice, roomId, 1);
        await alice.SendTextAsync(roomId, "hi from a", Ct);
        IReadOnlyList<string> atB = await WaitForBodiesAsync(bob, roomId, 2);

        // Assert: same history on both servers
        Assert.Equal(["hello from b"], atA);
        Assert.Equal(["hello from b", "hi from a"], atB);
        Assert.Equal(["hello from b", "hi from a"], await WaitForBodiesAsync(alice, roomId, 2));
    }

    /// <summary>A user invited from another server sees the invite with the room's name, joins through the inviting server and talks; another rejects, and the invite ends as left.</summary>
    [Fact]
    public async Task InviteAcrossServers()
    {
        // Arrange: alice's private room on a.test, bob and carol on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        MatrixClient carol = b.CreateClient();
        await carol.RegisterAsync("carol", "password", cancellationToken: Ct);
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PrivateChat, Name = "Plans" }, Ct);
        using HttpClient bobHttp = b.CreateHttpClient();
        bobHttp.DefaultRequestHeaders.Authorization = new("Bearer", bob.AccessToken);
        using HttpClient carolHttp = b.CreateHttpClient();
        carolHttp.DefaultRequestHeaders.Authorization = new("Bearer", carol.AccessToken);

        // Act: invites, bob's view, bob joins and writes; carol rejects
        await alice.InviteAsync(roomId, "@bob:b.test", cancellationToken: Ct);
        await alice.InviteAsync(roomId, "@carol:b.test", cancellationToken: Ct);
        JsonObject bobSync = (await bobHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/sync?timeout=0", Ct))!;
        JsonObject carolBefore = (await carolHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/sync?timeout=0", Ct))!;
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await bob.SendTextAsync(roomId, "thanks for the invite", Ct);
        IReadOnlyList<string> atA = await WaitForBodiesAsync(alice, roomId, 1);
        await carol.LeaveRoomAsync(roomId, cancellationToken: Ct);
        JsonObject carolAfter = (await carolHttp.GetFromJsonAsync<JsonObject>($"/_matrix/client/v3/sync?timeout=0&since={carolBefore["next_batch"]}", Ct))!;
        JsonObject carolLater = (await carolHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/sync?timeout=0", Ct))!;

        // Assert
        JsonArray inviteState = bobSync["rooms"]!["invite"]![roomId]!["invite_state"]!["events"]!.AsArray();
        Assert.Contains(inviteState, e => e!["type"]!.GetValue<string>() == "m.room.name" && e["content"]!["name"]!.GetValue<string>() == "Plans");
        Assert.Contains(inviteState, e => e!["type"]!.GetValue<string>() == "m.room.member" && e["state_key"]!.GetValue<string>() == "@bob:b.test" && e["sender"]!.GetValue<string>() == "@alice:a.test");
        Assert.Equal(["@alice:a.test", "@bob:b.test"], (await alice.GetJoinedMembersAsync(roomId, Ct)).Keys.Order());
        Assert.Equal(["thanks for the invite"], atA);
        Assert.True(carolAfter["rooms"]!["leave"]!.AsObject().ContainsKey(roomId));
        Assert.False(carolLater["rooms"]!["invite"]!.AsObject().ContainsKey(roomId));
        string? carolAtA = null;
        for (int i = 0; i < 50 && carolAtA != "leave"; i++)
        {
            carolAtA = (await alice.TryGetStateEventAsync(roomId, "m.room.member", "@carol:b.test", Ct))?.GetProperty("membership").GetString();
            if (carolAtA != "leave") await Task.Delay(100, Ct);
        }
        Assert.Equal("leave", carolAtA);
    }

    /// <summary>A message to a server that is down for a moment arrives once a retry gets through.</summary>
    [Fact]
    public async Task RetryTransactions()
    {
        // Arrange: alice of a.test in bob's room, then b.test fails the next transaction
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        lock (network.FailTransactions) network.FailTransactions["b.test"] = 1;

        // Act
        await alice.SendTextAsync(roomId, "sent while b was down", Ct);
        IReadOnlyList<string> atB = await WaitForBodiesAsync(bob, roomId, 1, attempts: 100);

        // Assert
        Assert.Equal(["sent while b was down"], atB);
        Assert.Equal(0, network.FailTransactions["b.test"]);
    }

    /// <summary>What waits for a server that is down is kept in the outbox until it is sent, so a restart would not lose it.</summary>
    [Fact]
    public async Task KeepOutboxUntilSent()
    {
        // Arrange: alice of a.test in bob's room, then b.test down for a while
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        await WaitForOutboxAsync(a, 0);
        lock (network.FailTransactions) network.FailTransactions["b.test"] = 2;

        // Act: a message while b is down, then wait for the retry
        await alice.SendTextAsync(roomId, "kept for b", Ct);
        int waiting = await WaitForOutboxAsync(a, 1);
        IReadOnlyList<string> atB = await WaitForBodiesAsync(bob, roomId, 1, attempts: 150);
        int after = await WaitForOutboxAsync(a, 0);

        // Assert
        Assert.Equal(1, waiting);
        Assert.Equal(["kept for b"], atB);
        Assert.Equal(0, after);
    }

    /// <summary>What waited for a server that was down is sent after a restart.</summary>
    [Fact]
    public async Task ResumeOutboxAfterRestart()
    {
        // Arrange: a.test in a data directory of its own, alice in bob's room, b.test down
        Network network = new();
        string directory = Directory.CreateTempSubdirectory("matrix-restart-").FullName;
        try
        {
            await using TestHomeserver b = await StartAsync("b.test", network);
            MatrixClient bob = b.CreateClient();
            await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
            string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
            await using (TestHomeserver a = await StartAsync("a.test", network, directory))
            {
                MatrixClient alice = a.CreateClient();
                await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
                await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
                await WaitForOutboxAsync(a, 0);
                lock (network.FailTransactions) network.FailTransactions["b.test"] = 1000;

                // Act: a message while b is down, kept in the outbox, then a.test stops
                await alice.SendTextAsync(roomId, "survives a restart", Ct);
                Assert.True(await WaitForOutboxPduAsync(a));
            }
            lock (network.FailTransactions) network.FailTransactions["b.test"] = 0;
            await using TestHomeserver restarted = await StartAsync("a.test", network, directory);
            IReadOnlyList<string> atB = await WaitForBodiesAsync(bob, roomId, 1, attempts: 300);

            // Assert
            Assert.Equal(["survives a restart"], atB);
            Assert.Equal(0, await WaitForOutboxAsync(restarted, 0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Events that never arrived are fetched with get_missing_events when a later one builds on them, so the history has no gap.</summary>
    [Fact]
    public async Task FillGaps()
    {
        // Arrange: alice of a.test in bob's room
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        await WaitForOutboxAsync(b, 0);

        // Act: two messages a.test refuses, which b.test then drops, and one that arrives
        lock (network.FailTransactions) network.RefuseTransactions.Add("a.test");
        await bob.SendTextAsync(roomId, "lost one", Ct);
        await bob.SendTextAsync(roomId, "lost two", Ct);
        await Task.Delay(500, Ct);
        await WaitForOutboxAsync(b, 0);
        lock (network.FailTransactions) network.RefuseTransactions.Remove("a.test");
        await bob.SendTextAsync(roomId, "after the gap", Ct);
        IReadOnlyList<string> atA = await WaitForBodiesAsync(alice, roomId, 3, attempts: 100);

        // Assert: all three, in order
        Assert.Equal(["lost one", "lost two", "after the gap"], atA);
    }

    /// <summary>Return whether a PDU is in the outbox, waiting a while for one.</summary>
    private static async Task<bool> WaitForOutboxPduAsync(TestHomeserver server)
    {
        // Poll the table
        IDbContextFactory<ServerDbContext> contexts = server.Services.GetRequiredService<IDbContextFactory<ServerDbContext>>();
        for (int i = 0; i < 300; i++)
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(Ct);
            if (await db.FederationOutbox.AnyAsync(o => !o.IsEdu, Ct)) return true;
            await Task.Delay(50, Ct);
        }
        // Return none
        return false;
    }

    /// <summary>Return the number of outbox rows once it is <paramref name="expected"/>, or after a while what it is.</summary>
    private static async Task<int> WaitForOutboxAsync(TestHomeserver server, int expected)
    {
        // Poll the table
        IDbContextFactory<ServerDbContext> contexts = server.Services.GetRequiredService<IDbContextFactory<ServerDbContext>>();
        int count = -1;
        for (int i = 0; i < 300 && count != expected; i++)
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(Ct);
            count = await db.FederationOutbox.CountAsync(Ct);
            if (count != expected) await Task.Delay(50, Ct);
        }
        // Return count
        return count;
    }

    /// <summary>Typing and public read receipts of one server's users are seen on the other.</summary>
    [Fact]
    public async Task ShareTypingAndReceipts()
    {
        // Arrange: alice of a.test in bob's room, with a message of bob's
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        string eventId = await bob.SendTextAsync(roomId, "read me", Ct);
        await WaitForBodiesAsync(alice, roomId, 1);
        using HttpClient aliceHttp = a.CreateHttpClient();
        aliceHttp.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
        using HttpClient bobHttp = b.CreateHttpClient();
        bobHttp.DefaultRequestHeaders.Authorization = new("Bearer", bob.AccessToken);
        string room = Uri.EscapeDataString(roomId);

        // Act: alice types and reads bob's message
        await aliceHttp.PutAsJsonAsync($"/_matrix/client/v3/rooms/{room}/typing/%40alice%3Aa.test", new { typing = true, timeout = 30000 }, Ct);
        await aliceHttp.PostAsJsonAsync($"/_matrix/client/v3/rooms/{room}/receipt/m.read/{Uri.EscapeDataString(eventId)}", new { }, Ct);
        JsonObject? typing = null, receipt = null;
        for (int i = 0; i < 50 && (typing is null || receipt is null); i++)
        {
            JsonObject sync = (await bobHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/sync?timeout=0", Ct))!;
            JsonArray ephemeral = sync["rooms"]?["join"]?[roomId]?["ephemeral"]?["events"] as JsonArray ?? [];
            typing ??= ephemeral.OfType<JsonObject>().FirstOrDefault(e => e["type"]!.GetValue<string>() == "m.typing" && e["content"]!["user_ids"]!.AsArray().Any(u => u!.GetValue<string>() == "@alice:a.test"));
            receipt ??= ephemeral.OfType<JsonObject>().FirstOrDefault(e => e["type"]!.GetValue<string>() == "m.receipt" && e["content"]?[eventId]?["m.read"]?["@alice:a.test"] is not null);
            if (typing is null || receipt is null) await Task.Delay(100, Ct);
        }

        // Assert
        Assert.NotNull(typing);
        Assert.NotNull(receipt);
    }

    /// <summary>A server in the room gets history to fill gaps: missing events oldest first, backfill newest first, single events and the state at an event; a server without members gets nothing.</summary>
    [Fact]
    public async Task ServeHistory()
    {
        // Arrange: alice of a.test in bob's room with three messages, and c.test outside it
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        await using TestHomeserver c = await StartAsync("c.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        string one = await bob.SendTextAsync(roomId, "one", Ct);
        string two = await bob.SendTextAsync(roomId, "two", Ct);
        string three = await bob.SendTextAsync(roomId, "three", Ct);
        FederationClient fromA = a.Services.GetRequiredService<FederationClient>();
        FederationClient fromC = c.Services.GetRequiredService<FederationClient>();
        string room = Uri.EscapeDataString(roomId);

        // Act
        JsonNode? missing = await fromA.SendAsync(HttpMethod.Post, "b.test", $"/_matrix/federation/v1/get_missing_events/{room}", new JsonObject { ["earliest_events"] = new JsonArray(one), ["latest_events"] = new JsonArray(three), ["limit"] = 10 }, Ct);
        JsonNode? backfill = await fromA.SendAsync(HttpMethod.Get, "b.test", $"/_matrix/federation/v1/backfill/{room}?v={Uri.EscapeDataString(three)}&limit=2", null, Ct);
        JsonNode? single = await fromA.SendAsync(HttpMethod.Get, "b.test", $"/_matrix/federation/v1/event/{Uri.EscapeDataString(two)}", null, Ct);
        JsonNode? stateIds = await fromA.SendAsync(HttpMethod.Get, "b.test", $"/_matrix/federation/v1/state_ids/{room}?event_id={Uri.EscapeDataString(three)}", null, Ct);
        MatrixException outsider = await Assert.ThrowsAsync<MatrixException>(() => fromC.SendAsync(HttpMethod.Get, "b.test", $"/_matrix/federation/v1/event/{Uri.EscapeDataString(two)}", null, Ct));

        // Assert
        Assert.Equal(["two"], missing!["events"]!.AsArray().Select(e => e!["content"]!["body"]!.GetValue<string>()));
        Assert.Equal(["three", "two"], backfill!["pdus"]!.AsArray().Select(e => e!["content"]!["body"]!.GetValue<string>()));
        Assert.Equal("two", single!["pdus"]![0]!["content"]!["body"]!.GetValue<string>());
        Assert.True(stateIds!["pdu_ids"]!.AsArray().Count >= 5);
        Assert.NotEmpty(stateIds["auth_chain_ids"]!.AsArray());
        Assert.Equal(ErrorCodes.Forbidden, outsider.Errcode);
    }

    /// <summary>A user joins a room by an alias of the other server, which names the room and its servers; unknown aliases are not found.</summary>
    [Fact]
    public async Task JoinByRemoteAlias()
    {
        // Arrange: bob's room with an alias on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, RoomAliasName = "lobby" }, Ct);
        using HttpClient aliceHttp = a.CreateHttpClient();
        aliceHttp.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);

        // Act
        JsonObject resolved = (await aliceHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/directory/room/%23lobby%3Ab.test", Ct))!;
        string joined = await alice.JoinRoomAsync("#lobby:b.test", cancellationToken: Ct);
        HttpStatusCode unknown = (await aliceHttp.GetAsync("/_matrix/client/v3/directory/room/%23nothing%3Ab.test", Ct)).StatusCode;

        // Assert
        Assert.Equal(roomId, resolved["room_id"]!.GetValue<string>());
        Assert.Contains("b.test", resolved["servers"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(roomId, joined);
        Assert.Equal(["@alice:a.test", "@bob:b.test"], (await bob.GetJoinedMembersAsync(roomId, Ct)).Keys.Order());
        Assert.Equal(HttpStatusCode.NotFound, unknown);
    }

    /// <summary>A version 12 room, whose ID names no server, is joined through <c>via</c>, and messages flow both ways.</summary>
    [Fact]
    public async Task JoinVersion12Room()
    {
        // Arrange: bob's version 12 room on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, RoomVersion = "12" }, Ct);

        // Act
        await alice.JoinRoomAsync(roomId, ["b.test"], cancellationToken: Ct);
        await bob.SendTextAsync(roomId, "from b", Ct);
        await WaitForBodiesAsync(alice, roomId, 1);
        await alice.SendTextAsync(roomId, "from a", Ct);
        IReadOnlyList<string> atB = await WaitForBodiesAsync(bob, roomId, 2);

        // Assert
        Assert.DoesNotContain(":", roomId);
        Assert.Equal(["from b", "from a"], atB);
    }

    /// <summary>A user who syncs on one server is seen online on the other, with their status message.</summary>
    [Fact]
    public async Task SharePresence()
    {
        // Arrange: alice of a.test in bob's room
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await alice.JoinRoomAsync(roomId, cancellationToken: Ct);
        using HttpClient aliceHttp = a.CreateHttpClient();
        aliceHttp.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
        using HttpClient bobHttp = b.CreateHttpClient();
        bobHttp.DefaultRequestHeaders.Authorization = new("Bearer", bob.AccessToken);

        // Act: alice sets a status, then bob syncs until he sees it
        await aliceHttp.PutAsJsonAsync("/_matrix/client/v3/presence/%40alice%3Aa.test/status", new { presence = "online", status_msg = "cooking" }, Ct);
        JsonObject? seen = null;
        for (int i = 0; i < 50 && seen is null; i++)
        {
            JsonObject sync = (await bobHttp.GetFromJsonAsync<JsonObject>("/_matrix/client/v3/sync?timeout=0", Ct))!;
            seen = (sync["presence"]?["events"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(e => e["sender"]!.GetValue<string>() == "@alice:a.test" && e["content"]?["status_msg"]?.GetValue<string>() == "cooking");
            if (seen is null) await Task.Delay(100, Ct);
        }

        // Assert
        Assert.NotNull(seen);
        Assert.Equal("online", seen["content"]!["presence"]!.GetValue<string>());
    }

    /// <summary>A user downloads and thumbnails media of the other server, which is fetched once through the federation media API and kept; unknown media is not found.</summary>
    [Fact]
    public async Task FetchRemoteMedia()
    {
        // Arrange: bob's image on b.test
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient alice = a.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        byte[] image = MediaTests.Png(64, 48, transparent: false);
        MxcUri uri = await bob.UploadMediaAsync(new MemoryStream(image), "image/png", "photo.png", Ct);
        using HttpClient aliceHttp = a.CreateHttpClient();
        aliceHttp.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
        string path = $"b.test/{uri.MediaId}";

        // Act: download twice, with b.test refusing everything the second time, then a thumbnail
        using HttpResponseMessage first = await aliceHttp.GetAsync($"/_matrix/client/v1/media/download/{path}", Ct);
        byte[] firstBytes = await first.Content.ReadAsByteArrayAsync(Ct);
        network.Servers.Remove("b.test", out HttpMessageHandler? bHandler);
        using HttpResponseMessage cached = await aliceHttp.GetAsync($"/_matrix/client/v1/media/download/{path}", Ct);
        byte[] cachedBytes = await cached.Content.ReadAsByteArrayAsync(Ct);
        using HttpResponseMessage thumbnail = await aliceHttp.GetAsync($"/_matrix/client/v1/media/thumbnail/{path}?width=32&height=32&method=scale", Ct);
        network.Servers["b.test"] = bHandler!;
        HttpStatusCode unknown = (await aliceHttp.GetAsync("/_matrix/client/v1/media/download/b.test/nothing", Ct)).StatusCode;

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(image, firstBytes);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Contains("photo.png", first.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(image, cachedBytes);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.StartsWith("image/", thumbnail.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, unknown);
    }

    /// <summary>Another server sees a public room's hierarchy, with private children it has no member in listed as inaccessible.</summary>
    [Fact]
    public async Task ServeHierarchy()
    {
        // Arrange: bob's public room on b.test with a public and a private child
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network);
        MatrixClient bob = b.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string space = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Home" }, Ct);
        string open = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Kitchen" }, Ct);
        string closed = await bob.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PrivateChat }, Ct);
        foreach (string child in new[] { open, closed }) await bob.SendStateEventAsync(space, "m.space.child", new { via = new[] { "b.test" } }, child, Ct);

        // Act
        JsonNode? hierarchy = await a.Services.GetRequiredService<FederationClient>().SendAsync(HttpMethod.Get, "b.test", $"/_matrix/federation/v1/hierarchy/{Uri.EscapeDataString(space)}", null, Ct);

        // Assert
        Assert.Equal("Home", hierarchy!["room"]!["name"]!.GetValue<string>());
        Assert.Equal(2, hierarchy["room"]!["children_state"]!.AsArray().Count);
        Assert.Equal([open], hierarchy["children"]!.AsArray().Select(c => c!["room_id"]!.GetValue<string>()));
        Assert.Equal([closed], hierarchy["inaccessible_children"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    /// <summary>Return the message bodies of a room, oldest first, once there are <paramref name="count"/>; fail after a few seconds.</summary>
    private static async Task<IReadOnlyList<string>> WaitForBodiesAsync(MatrixClient client, string roomId, int count, int attempts = 50)
    {
        // Poll, as transactions are sent in the background
        for (int attempt = 0; ; attempt++)
        {
            MessagesResponse page = await client.GetMessagesAsync(roomId, limit: 50, cancellationToken: Ct);
            List<string> bodies = [.. page.Chunk.Where(e => e.Type == "m.room.message").Select(e => e.Content.GetProperty("body").GetString()!).Reverse()];
            if (bodies.Count >= count || attempt == attempts) return bodies;
            await Task.Delay(100, Ct);
        }
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

    /// <summary>A key server answers for others with their own signature and its own; a server that cannot reach the origin checks its requests with keys from the key server, and keeps them.</summary>
    [Fact]
    public async Task UseKeyServer()
    {
        // Arrange: c.test knows a.test's keys; b.test trusts c.test as its key server
        Network network = new();
        await using TestHomeserver a = await StartAsync("a.test", network);
        await using TestHomeserver c = await StartAsync("c.test", network);
        await using TestHomeserver b = await StartAsync("b.test", network, extra: new Dictionary<string, string?> { ["Federation:TrustedKeyServers"] = "c.test" });
        await b.CreateClient().RegisterAsync("bob", "password", cancellationToken: Ct);
        ServerKeys aKeys = a.Services.GetRequiredService<ServerKeys>();
        ServerKeys cKeys = c.Services.GetRequiredService<ServerKeys>();
        using HttpClient cHttp = c.CreateHttpClient();
        JsonObject notarized = (await (await cHttp.PostAsJsonAsync("/_matrix/key/v2/query", new JsonObject { ["server_keys"] = new JsonObject { ["a.test"] = new JsonObject() } }, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        // Act: a.test goes away, then signs a request to b.test anyway
        network.Servers.Remove("a.test", out _);
        using HttpClient bHttp = b.CreateHttpClient();
        const string uri = "/_matrix/federation/v1/query/profile?user_id=%40bob%3Ab.test";
        HttpStatusCode viaKeyServer = await SendAsync(bHttp, uri, FederationAuth.Sign("GET", uri, "a.test", "b.test", null, aKeys.KeyId, aKeys.Secret).ToString());
        IDbContextFactory<ServerDbContext> contexts = b.Services.GetRequiredService<IDbContextFactory<ServerDbContext>>();
        await using ServerDbContext db = await contexts.CreateDbContextAsync(Ct);
        bool kept = await db.ServerKeyResponses.AnyAsync(k => k.ServerName == "a.test", Ct);

        // Assert: the answer has both signatures
        JsonObject entry = notarized["server_keys"]![0]!.AsObject();
        Assert.Equal("a.test", entry["server_name"]!.GetValue<string>());
        Assert.True(Ed25519.Verify(aKeys.PublicKey, CanonicalJson.SigningForm(entry), CanonicalJson.GetSignature(entry, "a.test", aKeys.KeyId)!));
        Assert.True(Ed25519.Verify(cKeys.PublicKey, CanonicalJson.SigningForm(entry), CanonicalJson.GetSignature(entry, "c.test", cKeys.KeyId)!));
        Assert.Equal(HttpStatusCode.OK, viaKeyServer);
        Assert.True(kept);
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

    /// <summary>The server announces where to reach it for federation, and for clients its address and call service, only when configured.</summary>
    [Fact]
    public async Task ServeWellKnown()
    {
        // Arrange: one server with delegation and a public address, one without
        await using TestHomeserver set = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Federation:WellKnownServer"] = "example.org:443", ["Server:PublicBaseUrl"] = "https://example.org", ["Calls:LiveKitServiceUrl"] = "https://rtc.example.org/jwt" });
        await using TestHomeserver unset = await TestHomeserver.StartAsync();
        using HttpClient http = set.CreateHttpClient();
        using HttpClient plain = unset.CreateHttpClient();

        // Act
        JsonObject server = (await http.GetFromJsonAsync<JsonObject>("/.well-known/matrix/server", Ct))!;
        JsonObject client = (await http.GetFromJsonAsync<JsonObject>("/.well-known/matrix/client", Ct))!;
        HttpStatusCode none = (await plain.GetAsync("/.well-known/matrix/server", Ct)).StatusCode;

        // Assert
        Assert.Equal("example.org:443", server["m.server"]!.GetValue<string>());
        Assert.Equal("https://example.org", client["m.homeserver"]!["base_url"]!.GetValue<string>());
        Assert.Equal("https://rtc.example.org/jwt", client["org.matrix.msc4143.rtc_foci"]![0]!["livekit_service_url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, none);
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
