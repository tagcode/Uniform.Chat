namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of simplified sliding sync (MSC4186), as Element X uses it.</summary>
public class SlidingSyncTests
{
    private const string Path = "/_matrix/client/unstable/org.matrix.simplified_msc3575/sync";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Return a request with one list of the first ten rooms, room names and own membership, five timeline events and the usual extensions.</summary>
    private static JsonObject Request() => JsonNode.Parse("""
        {
          "lists": { "all": { "ranges": [[0, 9]], "required_state": [["m.room.name", ""], ["m.room.member", "$ME"]], "timeline_limit": 5 } },
          "extensions": { "to_device": { "enabled": true }, "e2ee": { "enabled": true }, "account_data": { "enabled": true }, "receipts": { "enabled": true }, "typing": { "enabled": true } }
        }
        """)!.AsObject();

    /// <summary>Send a request; return the answer.</summary>
    private static async Task<JsonObject> SyncAsync(HttpClient http, JsonObject request, string? pos = null, int timeout = 0)
    {
        // Position and timeout in the query
        using HttpResponseMessage response = await http.PostAsJsonAsync($"{Path}?timeout={timeout}{(pos is null ? "" : "&pos=" + pos)}", request, Ct);
        response.EnsureSuccessStatusCode();
        // Return answer
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    /// <summary>Start a server with alice, who has two named rooms with messages, the garden busier last; return them.</summary>
    private static async Task<(TestHomeserver Server, MatrixClient Alice, HttpClient Http, string Kitchen, string Garden)> StartAsync()
    {
        // Two rooms, the garden written to last
        TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
        MatrixClient alice = server.CreateClient();
        await alice.RegisterAsync("alice", "password", cancellationToken: Ct);
        string kitchen = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" }, Ct);
        string garden = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Garden" }, Ct);
        await alice.SendTextAsync(kitchen, "soup", Ct);
        await alice.SendTextAsync(garden, "roses", Ct);
        HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
        // Return parts
        return (server, alice, http, kitchen, garden);
    }

    /// <summary>The first answer has every room of the window, most recent first, with name, own membership, timeline and counts; the next has only what changed.</summary>
    [Fact]
    public async Task SyncRooms()
    {
        // Arrange
        (TestHomeserver server, MatrixClient alice, HttpClient http, string kitchen, string garden) = await StartAsync();
        await using TestHomeserver owned = server;
        using HttpClient client = http;

        // Act: start, write to the kitchen, continue
        JsonObject first = await SyncAsync(http, Request());
        await alice.SendTextAsync(kitchen, "more soup", Ct);
        JsonObject next = await SyncAsync(http, Request(), first["pos"]!.GetValue<string>());

        // Assert: both rooms at first, the garden's activity newer
        Assert.Equal(2, first["lists"]!["all"]!["count"]!.GetValue<int>());
        JsonObject gardenRoom = first["rooms"]![garden]!.AsObject();
        Assert.True(gardenRoom["initial"]!.GetValue<bool>());
        Assert.Equal("Garden", gardenRoom["name"]!.GetValue<string>());
        Assert.Contains(gardenRoom["required_state"]!.AsArray(), e => e!["type"]!.GetValue<string>() == "m.room.member" && e["state_key"]!.GetValue<string>() == "@alice:test.local");
        Assert.DoesNotContain(gardenRoom["required_state"]!.AsArray(), e => e!["type"]!.GetValue<string>() == "m.room.power_levels");
        Assert.Equal("roses", gardenRoom["timeline"]!.AsArray().Last()!["content"]!["body"]!.GetValue<string>());
        Assert.True(gardenRoom["bump_stamp"]!.GetValue<long>() > first["rooms"]![kitchen]!["bump_stamp"]!.GetValue<long>());
        Assert.Equal(1, gardenRoom["joined_count"]!.GetValue<int>());
        Assert.NotNull(first["extensions"]!["e2ee"]!["device_one_time_keys_count"]);

        // Assert: then only the kitchen, with its new message live
        Assert.Equal([kitchen], next["rooms"]!.AsObject().Select(r => r.Key));
        JsonObject kitchenRoom = next["rooms"]![kitchen]!.AsObject();
        Assert.False(kitchenRoom["initial"]!.GetValue<bool>());
        Assert.Equal(["more soup"], kitchenRoom["timeline"]!.AsArray().Select(e => e!["content"]!["body"]!.GetValue<string>()));
        Assert.Equal(1, kitchenRoom["num_live"]!.GetValue<int>());
    }

    /// <summary>A request with a position waits for news, and answers when a message arrives.</summary>
    [Fact]
    public async Task WaitForNews()
    {
        // Arrange
        (TestHomeserver server, MatrixClient alice, HttpClient http, _, string garden) = await StartAsync();
        await using TestHomeserver owned = server;
        using HttpClient client = http;
        JsonObject first = await SyncAsync(http, Request());

        // Act: wait up to ten seconds while a message is sent
        Task<JsonObject> waiting = SyncAsync(http, Request(), first["pos"]!.GetValue<string>(), timeout: 10000);
        await Task.Delay(300, Ct);
        bool waited = !waiting.IsCompleted;
        await alice.SendTextAsync(garden, "tulips", Ct);
        JsonObject answer = await waiting;

        // Assert
        Assert.True(waited);
        Assert.Equal("tulips", answer["rooms"]![garden]!["timeline"]!.AsArray().Last()!["content"]!["body"]!.GetValue<string>());
    }

    /// <summary>Invites come with their stripped state; an unknown position, as after a restart, gets <c>M_UNKNOWN_POS</c>.</summary>
    [Fact]
    public async Task ShowInvitesAndRefuseUnknownPositions()
    {
        // Arrange: bob invites alice to his room
        (TestHomeserver server, _, HttpClient http, _, _) = await StartAsync();
        await using TestHomeserver owned = server;
        using HttpClient client = http;
        MatrixClient bob = server.CreateClient();
        await bob.RegisterAsync("bob", "password", cancellationToken: Ct);
        string plans = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Plans", Invite = ["@alice:test.local"] }, Ct);

        // Act
        JsonObject first = await SyncAsync(http, Request());
        using HttpResponseMessage unknown = await http.PostAsJsonAsync($"{Path}?pos=s999999", Request(), Ct);
        JsonObject error = (await unknown.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

        // Assert
        Assert.Equal(3, first["lists"]!["all"]!["count"]!.GetValue<int>());
        Assert.Contains(first["rooms"]![plans]!["invite_state"]!.AsArray(), e => e!["type"]!.GetValue<string>() == "m.room.name" && e["content"]!["name"]!.GetValue<string>() == "Plans");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("M_UNKNOWN_POS", error["errcode"]!.GetValue<string>());
    }
}
