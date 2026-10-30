namespace Uniform.Chat.Avalonia.Tests;

using System.Text.Json;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of <see cref="MessageList"/>: how events become timeline items.</summary>
public class MessageListTests
{
    private const string Me = "@me:hs.example";
    private const string Bob = "@bob:hs.example";
    private const string RoomId = "!room:hs.example";
    private static int counter;

    /// <summary>Create an event with <paramref name="content"/> as JSON.</summary>
    private static ClientEvent Event(string type, string sender, object content, string? stateKey = null, long minute = 0) => new()
    {
        Type = type,
        Sender = sender,
        EventId = $"$e{Interlocked.Increment(ref counter)}",
        OriginServerTs = 1_700_000_000_000 + minute * 60_000,
        StateKey = stateKey,
        Content = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options),
    };

    /// <summary>Create a text message.</summary>
    private static ClientEvent Text(string sender, string body, long minute = 0) => Event(EventTypes.RoomMessage, sender, new MessageContent { MessageType = MessageTypes.Text, Body = body }, minute: minute);

    /// <summary>Return a room where Bob is joined as "Bob".</summary>
    private static Room CreateRoom()
    {
        // One sync with both members
        RoomStore store = new(Me);
        ClientEvent[] state = [Event(EventTypes.RoomMember, Me, new MemberContent { Membership = Memberships.Join, DisplayName = "Me" }, Me), Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" }, Bob)];
        store.Apply(new SyncResponse { NextBatch = "b", Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { [RoomId] = new JoinedRoom { State = new EventList<ClientEvent> { Events = state } } } } });
        store.TryGetRoom(RoomId, out Room? room);
        // Return room
        return room!;
    }

    /// <summary>Messages show sender names; membership changes become info lines; events without a description are left out.</summary>
    [Fact]
    public void CreateItems()
    {
        // Arrange
        Room room = CreateRoom();
        MessageList list = new(Me);

        // Act
        list.Reset(room, [Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" }, Bob), Text(Bob, "hello"), Text(Me, "hi"), Event("m.room.power_levels", Bob, new { users_default = 0 }, "")]);

        // Assert
        Assert.Equal(["Bob joined", "hello", "hi"], list.Items.Select(i => i.Text));
        Assert.True(list.Items[0].IsInfo);
        Assert.Equal("Bob", list.Items[1].SenderName);
        Assert.True(list.Items[2].IsOwn);
    }

    /// <summary>Edits, reactions and deletions change the message they refer to instead of adding items.</summary>
    [Fact]
    public void ApplyChanges()
    {
        // Arrange
        Room room = CreateRoom();
        MessageList list = new(Me);
        ClientEvent original = Text(Bob, "helo");
        ClientEvent other = Text(Bob, "second");
        list.Reset(room, [original, other]);

        // Act
        MessageContent edit = new() { MessageType = MessageTypes.Text, Body = "* hello", NewContent = new MessageContent { MessageType = MessageTypes.Text, Body = "hello" }, RelatesTo = new Relation { RelType = RelationTypes.Replace, EventId = original.EventId } };
        list.Append(room, [
            Event(EventTypes.RoomMessage, Bob, edit),
            Event(EventTypes.Reaction, Me, new ReactionContent { RelatesTo = new Relation { RelType = "m.annotation", EventId = original.EventId, Key = "👍" } }),
            Event(EventTypes.Reaction, Bob, new ReactionContent { RelatesTo = new Relation { RelType = "m.annotation", EventId = original.EventId, Key = "👍" } }),
            Event(EventTypes.RoomRedaction, Bob, new { redacts = other.EventId }),
        ]);

        // Assert
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("hello", list.Items[0].Text);
        Assert.True(list.Items[0].IsEdited);
        Assert.Equal("👍 2", list.Items[0].Reactions);
        Assert.Equal("Message deleted", list.Items[1].Text);
    }

    /// <summary>Consecutive messages of one sender share a header; a new sender or a pause starts a new one.</summary>
    [Fact]
    public void GroupHeaders()
    {
        // Arrange
        Room room = CreateRoom();
        MessageList list = new(Me);

        // Act
        list.Reset(room, [Text(Bob, "a", 0), Text(Bob, "b", 1), Text(Me, "c", 2), Text(Me, "d", 20)]);

        // Assert
        Assert.Equal([true, false, true, true], list.Items.Select(i => i.ShowHeader));
    }

    /// <summary>The first message of each day shows the day, and its sender even when the sender wrote just before midnight.</summary>
    [Fact]
    public void SeparateDays()
    {
        // Arrange
        Room room = CreateRoom();
        MessageList list = new(Me);

        // Act: two messages, then one a day later
        list.Reset(room, [Text(Bob, "a", 0), Text(Bob, "b", 1), Text(Bob, "c", 24 * 60)]);

        // Assert
        Assert.Equal([true, false, true], list.Items.Select(i => i.HasDay));
        Assert.True(list.Items[2].ShowHeader);
        DateOnly today = new(2026, 10, 26);
        Assert.Equal("Today", MessageList.FormatDay(today, today));
        Assert.Equal("Yesterday", MessageList.FormatDay(today.AddDays(-1), today));
        Assert.Equal(today.AddDays(-3).ToString("dddd"), MessageList.FormatDay(today.AddDays(-3), today));
        Assert.EndsWith("2025", MessageList.FormatDay(new DateOnly(2025, 3, 1), today));
    }

    /// <summary>Unsent messages stay after the timeline and are replaced as the outbox changes; older history goes before.</summary>
    [Fact]
    public void KeepPendingLast()
    {
        // Arrange
        Room room = CreateRoom();
        MessageList list = new(Me);
        list.Reset(room, [Text(Bob, "first", 10)]);
        OutboxMessage pending = new() { Id = 1, RoomId = RoomId, EventType = EventTypes.RoomMessage, ContentJson = """{"msgtype":"m.text","body":"on my way"}""", TransactionId = "t1", CreatedAt = DateTimeOffset.UtcNow };

        // Act
        list.SetPending(room, [pending]);
        list.Append(room, [Text(Bob, "second", 11)]);
        list.Prepend(room, [Text(Bob, "older", 1)]);

        // Assert
        Assert.Equal(["older", "first", "second", "on my way"], list.Items.Select(i => i.Text));
        Assert.True(list.Items[^1].IsPending);
        Assert.Equal("Sending…", list.Items[^1].SendState);
        list.SetPending(room, []);
        Assert.DoesNotContain(list.Items, i => i.IsPending);
    }
}
