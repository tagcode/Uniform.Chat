namespace Uniform.Matrix.Client.Tests;

using System.Text.Json;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="ClientStore"/> on SQLite.</summary>
public class ClientStoreTests
{
    private const string Me = "@me:hs.example";
    private const string RoomId = "!r:hs.example";
    private static readonly MatrixSession Session = new() { Homeserver = new Uri("https://hs.example/"), UserId = Me, DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Create a text message event.</summary>
    private static ClientEvent Text(string eventId, long ts, string body, string sender = "@bob:hs.example", string? transactionId = null) => new()
    {
        Type = EventTypes.RoomMessage,
        EventId = eventId,
        Sender = sender,
        OriginServerTs = ts,
        Content = JsonSerializer.SerializeToElement(new MessageContent { MessageType = MessageTypes.Text, Body = body }, MatrixJson.Options),
        Unsigned = transactionId is null ? null : new UnsignedData { TransactionId = transactionId },
    };

    /// <summary>Create a sync with <paramref name="events"/> in the timeline of one joined room.</summary>
    private static SyncResponse Sync(string batch, params ClientEvent[] events) => new()
    {
        NextBatch = batch,
        Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { [RoomId] = new() { Timeline = new Timeline { Events = events } } } },
    };

    /// <summary>Return the body of a message event.</summary>
    private static string? Body(ClientEvent clientEvent) => clientEvent.TryGetContent(out MessageContent? m) ? m.Body : null;

    /// <summary>Session is stored and reads back.</summary>
    [Fact]
    public async Task CanSaveAndLoadSession()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();

        // Act
        await store.SaveSessionAsync(Session, Ct);
        StoredAccount? account = await store.LoadAccountAsync(Ct);

        // Assert
        Assert.Equal(Me, account?.UserId);
        Assert.Equal("tok", account?.AccessToken);
        Assert.Equal("https://hs.example/", account?.Homeserver);

        // Act: rejected token is cleared
        await store.ClearAccessTokenAsync(Ct);
        // Assert
        Assert.Null((await store.LoadAccountAsync(Ct))?.AccessToken);
    }

    /// <summary>Encrypted events are found by session ID, also when the ID has characters that stored JSON escapes, like '+'.</summary>
    [Theory]
    [InlineData("plainSessionId")]
    [InlineData("61orICahYm+1EYoWX4BdUASweYJ+jyAh7hyHxlOp/DY")]
    public async Task CanFindEncryptedEventsBySession(string sessionId)
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        EncryptedContent content = new() { Algorithm = EncryptionAlgorithms.Megolm, SessionId = sessionId, SenderKey = "k", Ciphertext = JsonSerializer.SerializeToElement("c") };
        ClientEvent encrypted = new() { Type = EventTypes.RoomEncrypted, EventId = "$e", Sender = "@bob:hs.example", OriginServerTs = 1, Content = JsonSerializer.SerializeToElement(content, MatrixJson.Options) };
        await store.SaveSyncAsync(Sync("b1", encrypted), Me, Ct);

        // Act
        IReadOnlyList<ClientEvent> found = await store.GetEncryptedEventsAsync(RoomId, sessionId, Ct);

        // Assert
        Assert.Equal("$e", Assert.Single(found).EventId);
        Assert.Empty(await store.GetEncryptedEventsAsync(RoomId, "other", Ct));
    }

    /// <summary>Sync events are stored once, read in time order, and the position is saved.</summary>
    [Fact]
    public async Task CanSaveSyncAndReadHistory()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        await store.SaveSessionAsync(Session, Ct);

        // Act: second sync repeats $2, as a full-state resync may
        await store.SaveSyncAsync(Sync("b1", Text("$1", 100, "one"), Text("$2", 200, "two")), Me, Ct);
        await store.SaveSyncAsync(Sync("b2", Text("$2", 200, "two"), Text("$3", 300, "three")), Me, Ct);

        // Assert history and position
        Assert.Equal(["one", "two", "three"], (await store.GetRoomEventsAsync(RoomId, 10, cancellationToken: Ct)).Select(Body));
        Assert.Equal(["two", "three"], (await store.GetRoomEventsAsync(RoomId, 2, cancellationToken: Ct)).Select(Body));
        Assert.Equal(["one"], (await store.GetRoomEventsAsync(RoomId, 10, beforeTs: 200, cancellationToken: Ct)).Select(Body));
        Assert.Equal(RoomId, (await store.GetRoomEventsAsync(RoomId, 1, cancellationToken: Ct))[0].RoomId);
        Assert.Equal("b2", (await store.LoadAccountAsync(Ct))?.NextBatch);
    }

    /// <summary>Redactions strip stored content, also when target and redaction arrive in one sync.</summary>
    [Fact]
    public async Task CanRedactStoredEvents()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        ClientEvent Redaction(string id, string target) => new() { Type = EventTypes.RoomRedaction, EventId = id, Sender = Me, OriginServerTs = 500, Content = JsonSerializer.SerializeToElement(new RedactionContent { Redacts = target }, MatrixJson.Options) };

        // Act: earlier target, then same-batch target
        await store.SaveSyncAsync(Sync("b1", Text("$old", 100, "old secret")), Me, Ct);
        await store.SaveSyncAsync(Sync("b2", Redaction("$r1", "$old"), Text("$new", 200, "new secret"), Redaction("$r2", "$new")), Me, Ct);

        // Assert
        List<ClientEvent> events = [.. await store.GetRoomEventsAsync(RoomId, 10, cancellationToken: Ct)];
        ClientEvent old = events.Single(e => e.EventId == "$old");
        ClientEvent fresh = events.Single(e => e.EventId == "$new");
        Assert.Null(Body(old));
        Assert.Null(Body(fresh));
        Assert.Equal("$r1", old.Unsigned?.RedactedBecause?.EventId);
    }

    /// <summary>Outbox keeps order and transaction IDs; echoes in sync confirm unconfirmed messages.</summary>
    [Fact]
    public async Task CanQueueAndConfirmByEcho()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();

        // Act
        OutboxMessage first = await store.EnqueueAsync(RoomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "a" }, Ct);
        OutboxMessage second = await store.EnqueueAsync(RoomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "b" }, Ct);

        // Assert queue
        Assert.Equal([first.Id, second.Id], (await store.GetPendingAsync(Ct)).Select(m => m.Id));
        Assert.NotEqual(first.TransactionId, second.TransactionId);

        // Act: echo of the first arrives, e.g. sent before a crash
        await store.SaveSyncAsync(Sync("b1", Text("$echo", 100, "a", Me, first.TransactionId)), Me, Ct);

        // Assert
        Assert.Equal([second.Id], (await store.GetPendingAsync(Ct)).Select(m => m.Id));
        Assert.Equal([second.Id], (await store.GetUnsentAsync(RoomId, Ct)).Select(m => m.Id));
    }

    /// <summary>Failed attempts are counted; permanent failures leave the queue.</summary>
    [Fact]
    public async Task CanRecordFailedAttempts()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxMessage message = await store.EnqueueAsync(RoomId, EventTypes.RoomMessage, new { body = "x" }, Ct);

        // Act: transient
        await store.MarkAttemptFailedAsync(message.Id, "timeout", permanent: false, Ct);
        // Assert
        OutboxMessage pending = Assert.Single(await store.GetPendingAsync(Ct));
        Assert.Equal((1, "timeout"), (pending.Attempts, pending.LastError));

        // Act: permanent
        await store.MarkAttemptFailedAsync(message.Id, "forbidden", permanent: true, Ct);
        // Assert
        Assert.Empty(await store.GetPendingAsync(Ct));
        Assert.Equal(OutboxState.Failed, Assert.Single(await store.GetUnsentAsync(RoomId, Ct)).State);
    }
}
