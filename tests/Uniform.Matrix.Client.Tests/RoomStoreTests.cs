namespace Uniform.Matrix.Client.Tests;

using System.Net;
using System.Text.Json;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Tests;

/// <summary>Unit tests for <see cref="RoomStore"/> and <see cref="Room"/>.</summary>
public class RoomStoreTests
{
    private const string Me = "@me:hs.example";
    private const string RoomId = "!r:hs.example";
    private static int eventCounter;

    /// <summary>Create a room event with JSON <paramref name="content"/>.</summary>
    private static ClientEvent Event(string type, string sender, string content, string? stateKey = null, string? eventId = null) => new()
    {
        Type = type,
        Sender = sender,
        EventId = eventId ?? $"$e{Interlocked.Increment(ref eventCounter)}",
        OriginServerTs = 1,
        StateKey = stateKey,
        Content = JsonDocument.Parse(content).RootElement.Clone(),
    };

    /// <summary>Create an event with <paramref name="content"/> serialized as JSON.</summary>
    private static ClientEvent Event(string type, string sender, object content, string? stateKey = null, string? eventId = null) =>
        Event(type, sender, JsonSerializer.Serialize(content, content.GetType(), MatrixJson.Options), stateKey, eventId);

    /// <summary>Create a member event of <paramref name="userId"/>.</summary>
    private static ClientEvent Member(string userId, string membership = Memberships.Join, string? displayName = null) =>
        Event(EventTypes.RoomMember, userId, new MemberContent { Membership = membership, DisplayName = displayName }, userId);

    /// <summary>Create a text message event.</summary>
    private static ClientEvent Text(string sender, string body, string? eventId = null) =>
        Event(EventTypes.RoomMessage, sender, new MessageContent { MessageType = MessageTypes.Text, Body = body }, eventId: eventId);

    /// <summary>Create a basic event such as account data or ephemeral.</summary>
    private static MatrixEvent Basic(string type, object content) => new() { Type = type, Content = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options) };

    /// <summary>Create a sync with one joined room update.</summary>
    private static SyncResponse Sync(JoinedRoom room, string batch = "b", IReadOnlyList<MatrixEvent>? accountData = null) => new()
    {
        NextBatch = batch,
        Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { [RoomId] = room } },
        AccountData = new EventList<MatrixEvent> { Events = accountData ?? [] },
    };

    /// <summary>Create a joined room update.</summary>
    private static JoinedRoom Joined(IReadOnlyList<ClientEvent>? state = null, IReadOnlyList<ClientEvent>? timeline = null, bool limited = false, string? prevBatch = null, RoomSummary? summary = null, IReadOnlyList<MatrixEvent>? ephemeral = null) => new()
    {
        State = new EventList<ClientEvent> { Events = state ?? [] },
        Timeline = new Timeline { Events = timeline ?? [], Limited = limited, PrevBatch = prevBatch },
        Summary = summary,
        Ephemeral = new EventList<MatrixEvent> { Events = ephemeral ?? [] },
    };

    /// <summary>Apply a sync with one joined room and return the room.</summary>
    private static Room ApplyJoined(RoomStore store, JoinedRoom update)
    {
        // Apply
        store.Apply(Sync(update));
        // Return room
        return store.Rooms[RoomId];
    }

    /// <summary>Room name wins over alias, alias over member names.</summary>
    [Fact]
    public void CanNameRoomByNameThenAlias()
    {
        // Arrange
        RoomStore store = new(Me);
        ClientEvent alias = Event(EventTypes.RoomCanonicalAlias, Me, new CanonicalAliasContent { Alias = "#general:hs.example" }, "");

        // Act and assert alias
        Room room = ApplyJoined(store, Joined(state: [Member(Me), Member("@alice:hs.example", displayName: "Alice"), alias]));
        Assert.Equal("#general:hs.example", room.DisplayName);

        // Act and assert name
        ApplyJoined(store, Joined(timeline: [Event(EventTypes.RoomName, Me, new NameContent { Name = "General" }, "")]));
        Assert.Equal("General", room.DisplayName);
    }

    /// <summary>Unnamed rooms are named after other members.</summary>
    [Theory]
    [InlineData(0, "Empty Room")]
    [InlineData(1, "U0")]
    [InlineData(2, "U0 and U1")]
    [InlineData(3, "U0, U1 and U2")]
    [InlineData(7, "U0, U1, U2, U3, U4 and 2 others")]
    public void CanNameRoomByMembers(int others, string expected)
    {
        // Arrange
        RoomStore store = new(Me);
        List<ClientEvent> state = [Member(Me, displayName: "Me")];
        for (int i = 0; i < others; i++) state.Add(Member($"@u{i}:hs.example", displayName: $"U{i}"));

        // Act
        Room room = ApplyJoined(store, Joined(state: state));

        // Assert
        Assert.Equal(expected, room.DisplayName);
    }

    /// <summary>Summary heroes and counts name a room whose members are not loaded.</summary>
    [Fact]
    public void CanNameRoomFromSummary()
    {
        // Arrange
        RoomStore store = new(Me);
        RoomSummary summary = new() { Heroes = ["@alice:hs.example"], JoinedMemberCount = 1, InvitedMemberCount = 0 };

        // Act
        Room room = ApplyJoined(store, Joined(summary: summary));
        // Assert left-alone room
        Assert.Equal("Empty Room (was @alice:hs.example)", room.DisplayName);

        // Act: later summary only updates counts; heroes are kept
        ApplyJoined(store, Joined(summary: new RoomSummary { JoinedMemberCount = 5 }));
        // Assert
        Assert.Equal("@alice:hs.example and 3 others", room.DisplayName);
    }

    /// <summary>Shared display names get the user ID appended until no longer shared.</summary>
    [Fact]
    public void CanDisambiguateSharedDisplayNames()
    {
        // Arrange
        RoomStore store = new(Me);
        Room room = ApplyJoined(store, Joined(state: [Member(Me), Member("@b1:hs.example", displayName: "Bob"), Member("@b2:hs.example", displayName: "Bob"), Member("@c:hs.example")]));

        // Assert shared and missing names
        Assert.Equal("Bob (@b1:hs.example)", room.GetMemberDisplayName("@b1:hs.example"));
        Assert.Equal("@c:hs.example", room.GetMemberDisplayName("@c:hs.example"));
        Assert.Equal("@unknown:hs.example", room.GetMemberDisplayName("@unknown:hs.example"));

        // Act: one Bob leaves
        ApplyJoined(store, Joined(timeline: [Member("@b2:hs.example", Memberships.Leave, "Bob")]));

        // Assert
        Assert.Equal("Bob", room.GetMemberDisplayName("@b1:hs.example"));
    }

    /// <summary>Timeline state events apply over state before the timeline.</summary>
    [Fact]
    public void CanApplyTimelineStateOverState()
    {
        // Arrange
        RoomStore store = new(Me);
        ClientEvent oldTopic = Event(EventTypes.RoomTopic, Me, new TopicContent { Topic = "old" }, "");
        ClientEvent newTopic = Event(EventTypes.RoomTopic, Me, new TopicContent { Topic = "new" }, "");

        // Act
        Room room = ApplyJoined(store, Joined(state: [oldTopic], timeline: [Text(Me, "hi"), newTopic]));

        // Assert
        Assert.Equal("new", room.Topic);
        Assert.Equal(2, room.Timeline.Count);
    }

    /// <summary>A limited timeline clears old events and resets the history token.</summary>
    [Fact]
    public void CanClearTimelineOnGap()
    {
        // Arrange
        RoomStore store = new(Me);
        Room room = ApplyJoined(store, Joined(timeline: [Text(Me, "1"), Text(Me, "2")], prevBatch: "p1"));
        Assert.Equal("p1", room.HistoryToken);

        // Act: unlimited batch keeps history token
        ApplyJoined(store, Joined(timeline: [Text(Me, "3")], prevBatch: "p2"));
        // Assert
        Assert.Equal("p1", room.HistoryToken);
        Assert.Equal(3, room.Timeline.Count);

        // Act: gap
        SyncChanges changes = store.Apply(Sync(Joined(timeline: [Text(Me, "9")], limited: true, prevBatch: "p3")));
        // Assert
        Assert.Equal("9", Assert.Single(room.Timeline).TryGetContent(out MessageContent? m) ? m.Body : null);
        Assert.Equal("p3", room.HistoryToken);
        Assert.True(Assert.Single(changes.Timeline).Limited);
    }

    /// <summary>Timeline is trimmed to <see cref="RoomStore.MaxTimelineLength"/>, dropping oldest.</summary>
    [Fact]
    public void CanTrimTimeline()
    {
        // Arrange
        RoomStore store = new(Me) { MaxTimelineLength = 2 };

        // Act
        Room room = ApplyJoined(store, Joined(timeline: [Text(Me, "1", "$1"), Text(Me, "2", "$2"), Text(Me, "3", "$3")]));

        // Assert
        Assert.Equal(["$2", "$3"], room.Timeline.Select(e => e.EventId));
    }

    /// <summary>Redaction strips content of the target event in the timeline.</summary>
    [Fact]
    public void CanRedactTimelineEvent()
    {
        // Arrange
        RoomStore store = new(Me);
        Room room = ApplyJoined(store, Joined(timeline: [Text(Me, "oops", "$bad")]));
        ClientEvent redaction = Event(EventTypes.RoomRedaction, Me, new RedactionContent { Redacts = "$bad", Reason = "typo" });

        // Act
        ApplyJoined(store, Joined(timeline: [redaction]));

        // Assert
        Assert.True(room.TryGetEvent("$bad", out ClientEvent? redacted));
        Assert.False(redacted.TryGetContent(out MessageContent? m) && m.Body is not null);
        Assert.Equal(redaction.EventId, redacted.Unsigned?.RedactedBecause?.EventId);
    }

    /// <summary>Typing, receipts and unread counts are tracked and reported.</summary>
    [Fact]
    public void CanTrackTypingReceiptsAndUnread()
    {
        // Arrange
        RoomStore store = new(Me);
        MatrixEvent typing = Basic(EventTypes.Typing, new TypingContent { UserIds = ["@alice:hs.example"] });
        ReceiptContent receipts = new() { ["$x"] = new() { [ReceiptTypes.Read] = new() { ["@alice:hs.example"] = new ReceiptInfo { Ts = 1 } } } };
        JoinedRoom update = Joined(ephemeral: [typing, Basic(EventTypes.Receipt, receipts)]) with { UnreadNotifications = new UnreadNotificationCounts { NotificationCount = 3, HighlightCount = 1 } };

        // Act
        SyncChanges changes = store.Apply(Sync(update));
        Room room = store.Rooms[RoomId];

        // Assert
        Assert.Equal(["@alice:hs.example"], room.TypingUserIds);
        Assert.Equal("$x", room.ReadReceipts["@alice:hs.example"]);
        Assert.Equal(3, room.NotificationCount);
        Assert.Equal(1, room.HighlightCount);
        Assert.Same(room, Assert.Single(changes.TypingChanged));
        Assert.Same(room, Assert.Single(changes.UnreadChanged));

        // Act: same typing again is not a change
        SyncChanges again = store.Apply(Sync(Joined(ephemeral: [typing])));
        // Assert
        Assert.Empty(again.TypingChanged);
    }

    /// <summary>Direct rooms and ignored users come from global account data.</summary>
    [Fact]
    public void CanTrackDirectRoomsAndIgnoredUsers()
    {
        // Arrange
        RoomStore store = new(Me);
        Room room = ApplyJoined(store, Joined(state: [Member(Me)]));
        Assert.False(room.IsDirect);
        DirectContent direct = new() { ["@alice:hs.example"] = [RoomId] };
        IgnoredUserListContent ignored = new() { IgnoredUsers = new Dictionary<string, object> { ["@spam:hs.example"] = new() } };

        // Act
        store.Apply(Sync(Joined(), accountData: [Basic(EventTypes.Direct, direct), Basic(EventTypes.IgnoredUserList, ignored)]));

        // Assert
        Assert.True(room.IsDirect);
        Assert.Equal("@alice:hs.example", room.DirectUserId);
        Assert.Contains("@spam:hs.example", store.IgnoredUsers);
    }

    /// <summary>Invite shows stripped state; joining replaces it with full state.</summary>
    [Fact]
    public void CanMoveFromInviteToJoin()
    {
        // Arrange
        RoomStore store = new(Me);
        StrippedStateEvent name = new() { Type = EventTypes.RoomName, StateKey = "", Sender = "@alice:hs.example", Content = JsonSerializer.SerializeToElement(new NameContent { Name = "Secret" }, MatrixJson.Options) };
        StrippedStateEvent invite = new() { Type = EventTypes.RoomMember, StateKey = Me, Sender = "@alice:hs.example", Content = JsonSerializer.SerializeToElement(new MemberContent { Membership = Memberships.Invite, IsDirect = true }, MatrixJson.Options) };
        SyncResponse inviteSync = new() { NextBatch = "b1", Rooms = new SyncRooms { Invite = new Dictionary<string, InvitedRoom> { [RoomId] = new() { InviteState = new EventList<StrippedStateEvent> { Events = [name, invite] } } } } };

        // Act: invite
        SyncChanges invited = store.Apply(inviteSync);
        Room room = store.Rooms[RoomId];

        // Assert
        Assert.Same(room, Assert.Single(invited.Invited));
        Assert.Equal(Memberships.Invite, room.Membership);
        Assert.Equal("Secret", room.DisplayName);
        Assert.Equal("@alice:hs.example", room.InviterUserId);
        Assert.True(room.IsDirect);

        // Act: join with full state lacking the name
        SyncChanges joined = store.Apply(Sync(Joined(state: [Member(Me), Member("@alice:hs.example", displayName: "Alice")])));

        // Assert
        Assert.Same(room, Assert.Single(joined.Joined));
        Assert.Equal("Alice", room.DisplayName);
        Assert.Null(room.InviterUserId);
    }

    /// <summary>Leaving reports the room once and keeps it known.</summary>
    [Fact]
    public void CanLeaveRoom()
    {
        // Arrange
        RoomStore store = new(Me);
        ApplyJoined(store, Joined(state: [Member(Me)]));
        SyncResponse leave = new() { NextBatch = "b2", Rooms = new SyncRooms { Leave = new Dictionary<string, LeftRoom> { [RoomId] = new() { Timeline = new Timeline { Events = [Member(Me, Memberships.Leave)] } } } } };

        // Act
        SyncChanges changes = store.Apply(leave);

        // Assert
        Room room = Assert.Single(changes.Left);
        Assert.Equal(Memberships.Leave, room.Membership);
        Assert.Equal("b2", store.NextBatch);
    }

    /// <summary>Backfill prepends older events and learns lazy-loaded senders.</summary>
    [Fact]
    public async Task CanBackfillHistory()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """
        {
          "start": "p1", "end": null,
          "chunk": [
            { "type": "m.room.message", "sender": "@old:hs.example", "event_id": "$o2", "origin_server_ts": 2, "content": { "msgtype": "m.text", "body": "second" } },
            { "type": "m.room.message", "sender": "@old:hs.example", "event_id": "$o1", "origin_server_ts": 1, "content": { "msgtype": "m.text", "body": "first" } }
          ],
          "state": [ { "type": "m.room.member", "state_key": "@old:hs.example", "sender": "@old:hs.example", "event_id": "$m", "origin_server_ts": 0, "content": { "membership": "join", "displayname": "Oldie" } } ]
        }
        """));
        MatrixClient client = new(new HttpClient(handler), new MatrixSession { Homeserver = new Uri("https://hs.example"), UserId = Me, DeviceId = "D", AccessToken = "t" });
        RoomStore store = new(Me);
        Room room = ApplyJoined(store, Joined(timeline: [Text(Me, "now", "$n")], prevBatch: "p1"));

        // Act
        IReadOnlyList<ClientEvent> older = await store.BackfillAsync(client, room, 10, ct);

        // Assert
        Assert.Contains("from=p1", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal(["$o1", "$o2"], older.Select(e => e.EventId));
        Assert.Equal(["$o1", "$o2", "$n"], room.Timeline.Select(e => e.EventId));
        Assert.Equal("Oldie", room.GetMemberDisplayName("@old:hs.example"));
        Assert.False(room.HasMoreHistory);
        Assert.Empty(await store.BackfillAsync(client, room, 10, ct));
        Assert.Single(handler.Requests);
    }
}
