namespace Uniform.Matrix.Client.Tests;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Lines of stored events: each shown event on a line of its own, its edits, reactions and redactions on its line; pages of lines; changes that wait for their target; and the migration that gives stored events their lines.</summary>
public class EventLineTests
{
    private const string Me = "@me:hs.example";
    private const string RoomId = "!r:hs.example";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A message's reaction and edit load with it, after it; lines of one millisecond stay apart; pages go back by lines, not events.</summary>
    [Fact]
    public async Task PageLinesWithTheirChanges()
    {
        // Arrange: four messages, the last two in one millisecond, and changes of the first after the second
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        await store.SaveSyncAsync(Sync("b1", Text("$1", 100, "one"), Text("$2", 200, "two"), Reaction("$like", 300, "$1"), Edit("$fix", 400, "$1", "one!"), Text("$3", 500, "three"), Text("$4", 500, "four")), Me, Ct);

        // Act
        RoomLines newest = await store.GetRoomLinesAsync(RoomId, 2, cancellationToken: Ct);
        RoomLines before = await store.GetRoomLinesAsync(RoomId, 2, newest.OldestLine, Ct);
        RoomLines past = await store.GetRoomLinesAsync(RoomId, 2, before.OldestLine, Ct);

        // Assert: two lines a page; the first message followed by its changes
        Assert.Equal(["$3", "$4"], newest.Events.Select(e => e.EventId));
        Assert.Equal(["$1", "$like", "$fix", "$2"], before.Events.Select(e => e.EventId));
        Assert.Empty(past.Events);
        // Done
        Assert.Null(past.OldestLine);
    }

    /// <summary>A reaction, and the redaction of it, stored before the message they change wait without a line, and join the message's line when older history brings it.</summary>
    [Fact]
    public async Task PlaceChangesWhenTargetArrives()
    {
        // Arrange: a reaction to an older message and its redaction, then a new message
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        await store.SaveSyncAsync(Sync("b1", Reaction("$like", 300, "$old"), Redaction("$unlike", 400, "$like"), Text("$new", 500, "new")), Me, Ct);
        RoomLines waiting = await store.GetRoomLinesAsync(RoomId, 10, cancellationToken: Ct);

        // Act: the message from history
        await store.SaveHistoryAsync(RoomId, [Text("$old", 100, "old")], Ct);
        RoomLines placed = await store.GetRoomLinesAsync(RoomId, 10, cancellationToken: Ct);

        // Assert: only the new message at first; then the old one with both changes
        Assert.Equal(["$new"], waiting.Events.Select(e => e.EventId));
        // Done
        Assert.Equal(["$old", "$like", "$unlike", "$new"], placed.Events.Select(e => e.EventId));
    }

    /// <summary>Events stored before lines existed get them in the migration: lines from time, apart within a millisecond, and changes on their target's line.</summary>
    [Fact]
    public async Task MigrateStoredEvents()
    {
        // Arrange: the first schema, with events of the old shape
        await using TestDatabase database = new();
        await using (ClientDbContext db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261025234718_InitialCreate", Ct);
            foreach (ClientEvent clientEvent in new[] { Text("$1", 100, "one"), Text("$2", 100, "two"), Reaction("$like", 200, "$1"), Redaction("$unlike", 300, "$like"), Text("$3", 400, "three") })
            {
                string json = JsonSerializer.Serialize(clientEvent with { RoomId = RoomId }, MatrixJson.Options);
                await db.Database.ExecuteSqlAsync($"""INSERT INTO "Events" ("EventId", "RoomId", "Type", "Sender", "StateKey", "OriginServerTs", "Redacted", "Json") VALUES ({clientEvent.EventId}, {RoomId}, {clientEvent.Type}, {clientEvent.Sender}, NULL, {clientEvent.OriginServerTs}, 0, {json})""", Ct);
            }
        }

        // Act: the rest of the migrations
        ClientStore store = await database.CreateStoreAsync();
        RoomLines lines = await store.GetRoomLinesAsync(RoomId, 10, cancellationToken: Ct);
        await using ClientDbContext check = database.CreateDbContext();
        Dictionary<string, long?> line = await check.Events.ToDictionaryAsync(e => e.EventId, e => e.Line, Ct);

        // Assert: one millisecond's two lines apart, the changes on the first's line
        Assert.Equal(["$1", "$like", "$unlike", "$2", "$3"], lines.Events.Select(e => e.EventId));
        Assert.Equal(100 * ClientStore.LinesPerMillisecond, line["$1"]);
        Assert.Equal(100 * ClientStore.LinesPerMillisecond + 1, line["$2"]);
        // Done
        Assert.Equal(line["$1"], line["$unlike"]);
    }

    /// <summary>Create a text message.</summary>
    private static ClientEvent Text(string eventId, long ts, string body) => new()
    {
        Type = EventTypes.RoomMessage,
        EventId = eventId,
        Sender = "@bob:hs.example",
        OriginServerTs = ts,
        Content = JsonSerializer.SerializeToElement(new MessageContent { MessageType = MessageTypes.Text, Body = body }, MatrixJson.Options),
    };

    /// <summary>Create a 👍 reaction to <paramref name="target"/>.</summary>
    private static ClientEvent Reaction(string eventId, long ts, string target) => new()
    {
        Type = EventTypes.Reaction,
        EventId = eventId,
        Sender = "@bob:hs.example",
        OriginServerTs = ts,
        Content = JsonSerializer.SerializeToElement(new ReactionContent { RelatesTo = new Relation { RelType = RelationTypes.Annotation, EventId = target, Key = "👍" } }, MatrixJson.Options),
    };

    /// <summary>Create an edit of <paramref name="target"/> with new text.</summary>
    private static ClientEvent Edit(string eventId, long ts, string target, string body) => new()
    {
        Type = EventTypes.RoomMessage,
        EventId = eventId,
        Sender = "@bob:hs.example",
        OriginServerTs = ts,
        Content = JsonSerializer.SerializeToElement(new MessageContent { MessageType = MessageTypes.Text, Body = "* " + body, NewContent = new MessageContent { MessageType = MessageTypes.Text, Body = body }, RelatesTo = new Relation { RelType = RelationTypes.Replace, EventId = target } }, MatrixJson.Options),
    };

    /// <summary>Create a redaction of <paramref name="target"/>.</summary>
    private static ClientEvent Redaction(string eventId, long ts, string target) => new()
    {
        Type = EventTypes.RoomRedaction,
        EventId = eventId,
        Sender = "@bob:hs.example",
        OriginServerTs = ts,
        Redacts = target,
        Content = JsonSerializer.SerializeToElement(new RedactionContent { Redacts = target }, MatrixJson.Options),
    };

    /// <summary>Create a sync with <paramref name="events"/> in one joined room's timeline.</summary>
    private static SyncResponse Sync(string batch, params ClientEvent[] events) => new()
    {
        NextBatch = batch,
        Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { [RoomId] = new() { Timeline = new Timeline { Events = events } } } },
    };
}
