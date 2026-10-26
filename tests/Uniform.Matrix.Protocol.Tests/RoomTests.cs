namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for room, event and profile endpoints of <see cref="MatrixClient"/>.</summary>
public class RoomTests
{
    private static readonly Uri Homeserver = new("https://hs.example");
    private static readonly MatrixSession Session = new() { Homeserver = Homeserver, UserId = "@alice:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Create a logged-in client whose every request returns <paramref name="json"/>.</summary>
    private static (MatrixClient Client, StubHandler Handler) Create(string json = "{}")
    {
        // Create client answering every request with json
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, json));
        // Return client and handler
        return (new MatrixClient(new HttpClient(handler), Session), handler);
    }

    /// <summary>Join escapes the alias and sends via servers in both parameter names.</summary>
    [Fact]
    public async Task CanJoinByAliasViaServers()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"room_id":"!r:hs.example"}""");

        // Act
        string roomId = await client.JoinRoomAsync("#general:other.example", ["other.example"], cancellationToken: Ct);

        // Assert
        Assert.Equal("!r:hs.example", roomId);
        Uri uri = handler.Requests[0].Request.RequestUri!;
        Assert.Equal("/_matrix/client/v3/join/%23general%3Aother.example", uri.AbsolutePath);
        Assert.Equal("?via=other.example&server_name=other.example", uri.Query);
        Assert.Equal("{}", handler.Requests[0].Body);
    }

    /// <summary>Send uses PUT with escaped room ID and a fresh transaction ID per message.</summary>
    [Fact]
    public async Task CanSendTextWithUniqueTransactionIds()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$e"}""");

        // Act
        string eventId = await client.SendTextAsync("!r:hs.example", "hello", Ct);
        await client.SendTextAsync("!r:hs.example", "again", Ct);

        // Assert request
        Assert.Equal("$e", eventId);
        HttpRequestMessage first = handler.Requests[0].Request;
        Assert.Equal(HttpMethod.Put, first.Method);
        Assert.StartsWith("/_matrix/client/v3/rooms/%21r%3Ahs.example/send/m.room.message/", first.RequestUri!.AbsolutePath);
        Assert.NotEqual(first.RequestUri, handler.Requests[1].Request.RequestUri);

        // Assert body
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("m.text", body.RootElement.GetProperty("msgtype").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("body").GetString());
    }

    /// <summary>Given transaction ID is used as is, for idempotent retries.</summary>
    [Fact]
    public async Task CanSendWithGivenTransactionId()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"event_id":"$e"}""");

        // Act
        await client.SendMessageAsync("!r:hs.example", new MessageContent { MessageType = MessageTypes.Emote, Body = "waves" }, "txn1", Ct);

        // Assert
        Assert.EndsWith("/send/m.room.message/txn1", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    /// <summary>Messages query carries pagination parameters.</summary>
    [Fact]
    public async Task CanPaginateMessages()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"start":"s","end":"e","chunk":[{"type":"m.room.message","sender":"@b:hs.example","event_id":"$1","origin_server_ts":1,"content":{"msgtype":"m.text","body":"old"}}]}""");

        // Act
        MessagesResponse page = await client.GetMessagesAsync("!r:hs.example", from: "t1", limit: 20, cancellationToken: Ct);

        // Assert
        Assert.Equal("?from=t1&dir=b&limit=20", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal("e", page.End);
        Assert.Equal("$1", Assert.Single(page.Chunk).EventId);
    }

    /// <summary>Membership actions post the target user and reason.</summary>
    [Fact]
    public async Task CanKickWithReason()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create();

        // Act
        await client.KickAsync("!r:hs.example", "@bob:hs.example", "spam", Ct);

        // Assert
        Assert.EndsWith("/rooms/%21r%3Ahs.example/kick", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"user_id":"@bob:hs.example","reason":"spam"}""", handler.Requests[0].Body);
    }

    /// <summary>State event content is read as the requested type.</summary>
    [Fact]
    public async Task CanGetStateEventContent()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"topic":"Welcome"}""");

        // Act
        TopicContent topic = await client.GetStateEventAsync<TopicContent>("!r:hs.example", EventTypes.RoomTopic, cancellationToken: Ct);

        // Assert
        Assert.Equal("Welcome", topic.Topic);
        Assert.EndsWith("/state/m.room.topic/", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    /// <summary>Missing state event maps to null.</summary>
    [Fact]
    public async Task CanTryGetMissingStateEvent()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.NotFound, """{"errcode":"M_NOT_FOUND"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        JsonElement? content = await client.TryGetStateEventAsync("!r:hs.example", EventTypes.RoomName, cancellationToken: Ct);

        // Assert
        Assert.Null(content);
    }

    /// <summary>Typing uses the own user ID and omits timeout when stopping.</summary>
    [Fact]
    public async Task CanSetTyping()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create();

        // Act
        await client.SetTypingAsync("!r:hs.example", true, TimeSpan.FromSeconds(10), Ct);
        await client.SetTypingAsync("!r:hs.example", false, cancellationToken: Ct);

        // Assert
        Assert.EndsWith("/typing/%40alice%3Ahs.example", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"typing":true,"timeout":10000}""", handler.Requests[0].Body);
        Assert.Equal("""{"typing":false}""", handler.Requests[1].Body);
    }

    /// <summary>Read markers use dotted keys.</summary>
    [Fact]
    public async Task CanSetReadMarkers()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create();

        // Act
        await client.SetReadMarkersAsync("!r:hs.example", "$a", "$b", Ct);

        // Assert
        Assert.Equal("""{"m.fully_read":"$a","m.read":"$b"}""", handler.Requests[0].Body);
    }

    /// <summary>Create room sends the request in snake_case.</summary>
    [Fact]
    public async Task CanCreateRoom()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create("""{"room_id":"!new:hs.example"}""");
        CreateRoomRequest request = new() { Name = "Test", Preset = RoomPresets.PrivateChat, RoomAliasName = "test", Invite = ["@bob:hs.example"] };

        // Act
        string roomId = await client.CreateRoomAsync(request, Ct);

        // Assert
        Assert.Equal("!new:hs.example", roomId);
        Assert.Equal("""{"name":"Test","room_alias_name":"test","invite":["@bob:hs.example"],"preset":"private_chat"}""", handler.Requests[0].Body);
    }

    /// <summary>Display name uses the wire name <c>displayname</c>.</summary>
    [Fact]
    public async Task CanSetDisplayName()
    {
        // Arrange
        (MatrixClient client, StubHandler handler) = Create();

        // Act
        await client.SetDisplayNameAsync("Alice", Ct);

        // Assert
        Assert.EndsWith("/profile/%40alice%3Ahs.example/displayname", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"displayname":"Alice"}""", handler.Requests[0].Body);
    }

    /// <summary>Endpoints needing the own user ID throw when logged out.</summary>
    [Fact]
    public async Task CanRejectUserEndpointsWhenLoggedOut()
    {
        // Arrange
        MatrixClient client = new(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), Homeserver);

        // Act and assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetPresenceAsync(PresenceStates.Online, cancellationToken: Ct));
    }
}
