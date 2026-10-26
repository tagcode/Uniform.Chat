namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for account data, directory and rich message endpoints of <see cref="MatrixClient"/>.</summary>
public class AccountDataAndDirectoryTests
{
    private static readonly MatrixSession Session = new() { Homeserver = new Uri("https://hs.example"), UserId = "@alice:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Create a logged-in client answering via <paramref name="respond"/>.</summary>
    private static (MatrixClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        // Create
        StubHandler handler = new(respond);
        // Return client and handler
        return (new MatrixClient(new HttpClient(handler), Session), handler);
    }

    /// <summary>Create a logged-in client answering every request with <paramref name="json"/>.</summary>
    private static (MatrixClient Client, StubHandler Handler) Create(string json = "{}") => Create(_ => StubHandler.Json(HttpStatusCode.OK, json));

    /// <summary>Parse the body of request <paramref name="index"/>.</summary>
    private static JsonElement Body(StubHandler handler, int index = 0) => JsonDocument.Parse(handler.Requests[index].Body!).RootElement;

    /// <summary>Account data uses the own user ID and the type in the path.</summary>
    [Fact]
    public async Task CanSetAndGetAccountData()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"theme":"dark"}""");

        // Act
        await client.SetAccountDataAsync("fi.example.settings", new { theme = "dark" }, Ct);
        JsonElement content = await client.GetAccountDataAsync<JsonElement>("fi.example.settings", Ct);
        await client.SetRoomAccountDataAsync("!r:hs.example", "fi.example.note", new { text = "x" }, Ct);

        // Assert
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Request.Method);
        Assert.Equal("/_matrix/client/v3/user/%40alice%3Ahs.example/account_data/fi.example.settings", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("dark", content.GetProperty("theme").GetString());
        Assert.Equal("/_matrix/client/v3/user/%40alice%3Ahs.example/rooms/%21r%3Ahs.example/account_data/fi.example.note", handler.Requests[2].Request.RequestUri!.AbsolutePath);
    }

    /// <summary>Missing account data maps to null.</summary>
    [Fact]
    public async Task CanTryGetMissingAccountData()
    {
        // Arrange
        (MatrixClient client, StubHandler _) = Create(_ => StubHandler.Json(HttpStatusCode.NotFound, """{"errcode":"M_NOT_FOUND"}"""));

        // Act and assert
        Assert.Null(await client.TryGetAccountDataAsync<TagContent>("fi.example.none", Ct));
        Assert.Empty(await client.GetDirectRoomsAsync(Ct));
    }

    /// <summary>Tags are set with order and deleted.</summary>
    [Fact]
    public async Task CanSetAndDeleteTag()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create();

        // Act
        await client.SetTagAsync("!r:hs.example", RoomTags.Favourite, 0.5, Ct);
        await client.DeleteTagAsync("!r:hs.example", RoomTags.Favourite, Ct);

        // Assert
        Assert.EndsWith("/rooms/%21r%3Ahs.example/tags/m.favourite", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"order":0.5}""", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Request.Method);
    }

    /// <summary>Ignoring a user reads, extends and writes back the list.</summary>
    [Fact]
    public async Task CanIgnoreUser()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create(request => request.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"ignored_users":{"@old:hs.example":{}}}""")
            : StubHandler.Json(HttpStatusCode.OK, "{}"));

        // Act
        await client.IgnoreUserAsync("@spam:hs.example", Ct);

        // Assert
        JsonElement users = Body(handler, 1).GetProperty("ignored_users");
        Assert.Equal(JsonValueKind.Object, users.GetProperty("@old:hs.example").ValueKind);
        Assert.Equal(JsonValueKind.Object, users.GetProperty("@spam:hs.example").ValueKind);
    }

    /// <summary>Unignoring a user not in the list writes nothing.</summary>
    [Fact]
    public async Task CanSkipUnchangedIgnoreList()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"ignored_users":{}}""");

        // Act
        await client.UnignoreUserAsync("@nobody:hs.example", Ct);

        // Assert
        Assert.Single(handler.Requests);
    }

    /// <summary>Direct room creation invites the user and records the room in m.direct.</summary>
    [Fact]
    public async Task CanCreateDirectRoom()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create(request => request.RequestUri!.AbsolutePath switch
        {
            string p when p.EndsWith("/createRoom") => StubHandler.Json(HttpStatusCode.OK, """{"room_id":"!dm:hs.example"}"""),
            _ when request.Method == HttpMethod.Get => StubHandler.Json(HttpStatusCode.OK, """{"@bob:hs.example":["!old:hs.example"]}"""),
            _ => StubHandler.Json(HttpStatusCode.OK, "{}"),
        });

        // Act
        string roomId = await client.CreateDirectRoomAsync("@bob:hs.example", cancellationToken: Ct);

        // Assert create
        Assert.Equal("!dm:hs.example", roomId);
        JsonElement create = Body(handler, 0);
        Assert.True(create.GetProperty("is_direct").GetBoolean());
        Assert.Equal("@bob:hs.example", create.GetProperty("invite")[0].GetString());

        // Assert direct map
        Assert.EndsWith("/account_data/m.direct", handler.Requests[2].Request.RequestUri!.AbsolutePath);
        Assert.Equal(["!old:hs.example", "!dm:hs.example"], Body(handler, 2).GetProperty("@bob:hs.example").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>Room directory uses GET without a search term and POST with one.</summary>
    [Fact]
    public async Task CanListAndSearchPublicRooms()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"chunk":[{"room_id":"!p:hs.example","name":"Lobby","num_joined_members":42,"world_readable":true,"guest_can_join":false}],"next_batch":"n"}""");

        // Act
        PublicRoomsResponse list = await client.GetPublicRoomsAsync(limit: 10, cancellationToken: Ct);
        await client.GetPublicRoomsAsync(server: "other.example", searchTerm: "lob", cancellationToken: Ct);

        // Assert list
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Request.Method);
        Assert.Equal("?limit=10", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal(42, Assert.Single(list.Chunk).NumJoinedMembers);
        Assert.Equal("n", list.NextBatch);

        // Assert search
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Request.Method);
        Assert.Equal("?server=other.example", handler.Requests[1].Request.RequestUri!.Query);
        Assert.Equal("""{"filter":{"generic_search_term":"lob"}}""", handler.Requests[1].Body);
    }

    /// <summary>User directory search posts the term.</summary>
    [Fact]
    public async Task CanSearchUsers()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"results":[{"user_id":"@bob:hs.example","display_name":"Bob"}],"limited":false}""");

        // Act
        UserDirectoryResponse result = await client.SearchUsersAsync("bo", 5, Ct);

        // Assert
        Assert.Equal("""{"search_term":"bo","limit":5}""", handler.Requests[0].Body);
        Assert.Equal("Bob", Assert.Single(result.Results).DisplayName);
    }

    /// <summary>Reply references the event and mentions its sender.</summary>
    [Fact]
    public async Task CanSendReply()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$r"}""");
        ClientEvent original = new() { Type = EventTypes.RoomMessage, EventId = "$orig", Sender = "@bob:hs.example", OriginServerTs = 1 };

        // Act
        await client.SendReplyAsync("!r:hs.example", original, "agreed", Ct);

        // Assert
        JsonElement body = Body(handler);
        Assert.Equal("agreed", body.GetProperty("body").GetString());
        Assert.Equal("$orig", body.GetProperty("m.relates_to").GetProperty("m.in_reply_to").GetProperty("event_id").GetString());
        Assert.Equal("@bob:hs.example", body.GetProperty("m.mentions").GetProperty("user_ids")[0].GetString());
    }

    /// <summary>Edit carries new content and a replace relation.</summary>
    [Fact]
    public async Task CanSendEdit()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$e"}""");

        // Act
        await client.SendEditAsync("!r:hs.example", "$orig", "fixed", Ct);

        // Assert
        JsonElement body = Body(handler);
        Assert.Equal("* fixed", body.GetProperty("body").GetString());
        Assert.Equal("fixed", body.GetProperty("m.new_content").GetProperty("body").GetString());
        Assert.Equal(RelationTypes.Replace, body.GetProperty("m.relates_to").GetProperty("rel_type").GetString());
        Assert.Equal("$orig", body.GetProperty("m.relates_to").GetProperty("event_id").GetString());
    }

    /// <summary>Reaction is an m.reaction annotation with the key.</summary>
    [Fact]
    public async Task CanSendReaction()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$x"}""");

        // Act
        await client.SendReactionAsync("!r:hs.example", "$orig", "👍", Ct);

        // Assert
        Assert.Contains("/send/m.reaction/", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        JsonElement relation = Body(handler).GetProperty("m.relates_to");
        Assert.Equal(RelationTypes.Annotation, relation.GetProperty("rel_type").GetString());
        Assert.Equal("👍", relation.GetProperty("key").GetString());
    }

    /// <summary>HTML message has format, formatted body and mentions.</summary>
    [Fact]
    public async Task CanSendHtmlWithMentions()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$h"}""");

        // Act
        await client.SendHtmlAsync("!r:hs.example", "hi Bob", "hi <a href=\"https://matrix.to/#/@bob:hs.example\">Bob</a>", new Mentions { UserIds = ["@bob:hs.example"] }, Ct);

        // Assert
        JsonElement body = Body(handler);
        Assert.Equal(MessageFormats.Html, body.GetProperty("format").GetString());
        Assert.StartsWith("hi <a href", body.GetProperty("formatted_body").GetString());
        Assert.Equal("@bob:hs.example", body.GetProperty("m.mentions").GetProperty("user_ids")[0].GetString());
    }

    /// <summary>Sync parses to-device events, device lists, knocks and receipts.</summary>
    [Fact]
    public async Task CanParseExtendedSync()
    {
        // Arrange
        (MatrixClient client, StubHandler _) = Create("""
        {
          "next_batch": "b",
          "to_device": { "events": [ { "type": "m.room_key_request", "sender": "@bob:hs.example", "content": {} } ] },
          "device_lists": { "changed": ["@bob:hs.example"], "left": [] },
          "rooms": {
            "knock": { "!k:hs.example": { "knock_state": { "events": [ { "type": "m.room.name", "state_key": "", "sender": "@x:hs.example", "content": { "name": "Door" } } ] } } },
            "join": { "!r:hs.example": { "ephemeral": { "events": [ { "type": "m.receipt", "content": { "$e": { "m.read": { "@bob:hs.example": { "ts": 5, "thread_id": "main" } } } } } ] } } }
          }
        }
        """);

        // Act
        SyncResponse sync = await client.SyncAsync(cancellationToken: Ct);

        // Assert
        Assert.Equal("@bob:hs.example", Assert.Single(sync.ToDevice.Events).Sender);
        Assert.Equal(["@bob:hs.example"], sync.DeviceLists.Changed);
        Assert.Single(sync.Rooms.Knock["!k:hs.example"].KnockState.Events);
        Assert.True(Assert.Single(sync.Rooms.Join["!r:hs.example"].Ephemeral.Events).TryGetContent(out ReceiptContent? receipts));
        (string eventId, string type, string userId, ReceiptInfo info) = Assert.Single(receipts.Flatten());
        Assert.Equal(("$e", ReceiptTypes.Read, "@bob:hs.example", 5L, "main"), (eventId, type, userId, info.Ts, info.ThreadId));
    }
}
