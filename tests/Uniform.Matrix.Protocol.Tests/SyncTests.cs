namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for sync and <see cref="MatrixClient.SyncLoopAsync"/>.</summary>
public class SyncTests
{
    private static readonly Uri Homeserver = new("https://hs.example");
    private static readonly MatrixSession Session = new() { Homeserver = Homeserver, UserId = "@alice:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Trimmed from the spec example of GET /sync.
    private const string SyncJson = """
    {
      "next_batch": "s72595_4483_1934",
      "presence": { "events": [ { "type": "m.presence", "sender": "@bob:hs.example", "content": { "presence": "online", "currently_active": true } } ] },
      "rooms": {
        "join": {
          "!room:hs.example": {
            "summary": { "m.heroes": ["@bob:hs.example"], "m.joined_member_count": 2, "m.invited_member_count": 0 },
            "state": { "events": [ { "type": "m.room.member", "state_key": "@bob:hs.example", "sender": "@bob:hs.example", "event_id": "$m1", "origin_server_ts": 1432735824653, "content": { "membership": "join", "displayname": "Bob" } } ] },
            "timeline": {
              "limited": true,
              "prev_batch": "t34-23535_0_0",
              "events": [
                { "type": "m.room.message", "sender": "@bob:hs.example", "event_id": "$e1", "origin_server_ts": 1432735824653, "unsigned": { "age": 1234 }, "content": { "msgtype": "m.text", "body": "hello" } },
                { "type": "m.room.message", "sender": "@bob:hs.example", "event_id": "$e2", "origin_server_ts": 1432735824654, "content": { "msgtype": "m.text", "body": "* hi", "m.new_content": { "msgtype": "m.text", "body": "hi" }, "m.relates_to": { "rel_type": "m.replace", "event_id": "$e1" } } }
              ]
            },
            "ephemeral": { "events": [ { "type": "m.typing", "content": { "user_ids": ["@bob:hs.example"] } } ] },
            "unread_notifications": { "highlight_count": 1, "notification_count": 2 }
          }
        },
        "invite": {
          "!invite:hs.example": { "invite_state": { "events": [ { "type": "m.room.name", "state_key": "", "sender": "@carol:hs.example", "content": { "name": "Carol's room" } } ] } }
        }
      }
    }
    """;

    /// <summary>Spec-shaped sync response deserializes into typed models.</summary>
    [Fact]
    public async Task CanParseSyncResponse()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, SyncJson));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        SyncResponse sync = await client.SyncAsync(cancellationToken: Ct);

        // Assert batch and presence
        Assert.Equal("s72595_4483_1934", sync.NextBatch);
        PresenceEvent presence = Assert.Single(sync.Presence.Events);
        Assert.True(presence.TryGetContent(out PresenceContent? presenceContent));
        Assert.Equal(PresenceStates.Online, presenceContent.Presence);

        // Assert joined room
        JoinedRoom room = sync.Rooms.Join["!room:hs.example"];
        Assert.Equal(2, room.Summary?.JoinedMemberCount);
        Assert.Equal(["@bob:hs.example"], room.Summary?.Heroes);
        Assert.True(room.Timeline.Limited);
        Assert.Equal("t34-23535_0_0", room.Timeline.PrevBatch);
        Assert.Equal(1, room.UnreadNotifications?.HighlightCount);

        // Assert state and timeline content
        ClientEvent member = Assert.Single(room.State.Events);
        Assert.True(member.IsState);
        Assert.True(member.TryGetContent(out MemberContent? memberContent));
        Assert.Equal("Bob", memberContent.DisplayName);
        Assert.True(room.Timeline.Events[0].TryGetContent(out MessageContent? message));
        Assert.Equal("hello", message.Body);
        Assert.Equal(1234, room.Timeline.Events[0].Unsigned?.Age);
        Assert.True(room.Timeline.Events[1].TryGetContent(out MessageContent? edit));
        Assert.Equal("m.replace", edit.RelatesTo?.RelType);
        Assert.Equal("hi", edit.NewContent?.Body);

        // Assert ephemeral and invite
        Assert.True(Assert.Single(room.Ephemeral.Events).TryGetContent(out TypingContent? typing));
        Assert.Equal(["@bob:hs.example"], typing.UserIds);
        StrippedStateEvent inviteName = Assert.Single(sync.Rooms.Invite["!invite:hs.example"].InviteState.Events);
        Assert.True(inviteName.TryGetContent(out NameContent? name));
        Assert.Equal("Carol's room", name.Name);
        Assert.Empty(sync.Rooms.Leave);
    }

    /// <summary>Sync parameters are sent as query string.</summary>
    [Fact]
    public async Task CanSendSyncParameters()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"next_batch":"b"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);
        Filter filter = new() { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 10 } } };

        // Act
        await client.SyncAsync("a", TimeSpan.FromSeconds(30), filter.ToJson(), fullState: true, setPresence: PresenceStates.Offline, cancellationToken: Ct);

        // Assert
        string query = handler.Requests[0].Request.RequestUri!.Query;
        Assert.Contains("since=a", query);
        Assert.Contains("timeout=30000", query);
        Assert.Contains("filter=%7B%22room%22%3A%7B%22timeline%22%3A%7B%22limit%22%3A10%7D%7D%7D", query);
        Assert.Contains("full_state=true", query);
        Assert.Contains("set_presence=offline", query);
    }

    /// <summary>Sync loop passes each next_batch as since and retry transient failures.</summary>
    [Fact]
    public async Task CanLoopAndRetryTransientFailures()
    {
        // Arrange: success, server error, success
        int calls = 0;
        StubHandler handler = new(_ => ++calls switch
        {
            1 => StubHandler.Json(HttpStatusCode.OK, """{"next_batch":"b1"}"""),
            2 => StubHandler.Json(HttpStatusCode.BadGateway, """{"errcode":"M_UNKNOWN","error":"upstream"}"""),
            _ => StubHandler.Json(HttpStatusCode.OK, """{"next_batch":"b2"}"""),
        });
        MatrixClient client = new(new HttpClient(handler), Session) { SyncRetryDelay = TimeSpan.Zero };
        List<Exception> retries = [];

        // Act
        List<string> batches = [];
        await foreach (SyncResponse sync in client.SyncLoopAsync(onRetry: (ex, _) => retries.Add(ex), cancellationToken: Ct))
        {
            batches.Add(sync.NextBatch);
            if (batches.Count == 2) break;
        }

        // Assert
        Assert.Equal(["b1", "b2"], batches);
        Assert.IsType<MatrixException>(Assert.Single(retries));
        Assert.DoesNotContain("since", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Contains("since=b1", handler.Requests[1].Request.RequestUri!.Query);
        Assert.Contains("since=b1", handler.Requests[2].Request.RequestUri!.Query);
    }

    /// <summary>Sync loop stops on an invalid token instead of retrying.</summary>
    [Fact]
    public async Task CanStopLoopOnUnknownToken()
    {
        // Arrange
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.Unauthorized, """{"errcode":"M_UNKNOWN_TOKEN","error":"Invalid token"}"""));
        MatrixClient client = new(new HttpClient(handler), Session);

        // Act
        MatrixException ex = await Assert.ThrowsAsync<MatrixException>(async () =>
        {
            await foreach (SyncResponse _ in client.SyncLoopAsync(cancellationToken: Ct)) { }
        });

        // Assert
        Assert.Equal(ErrorCodes.UnknownToken, ex.Errcode);
        Assert.Single(handler.Requests);
    }
}
