namespace Uniform.Matrix.Client.Storage;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Local storage of session, sync position, room history, outbox and media metadata; thread-safe as each call uses its own context.</summary>
public sealed class ClientStore
{
    private readonly IDbContextFactory<ClientDbContext> contexts;
    private readonly TimeProvider time;

    /// <summary>Create a store over <paramref name="contexts"/>.</summary>
    public ClientStore(IDbContextFactory<ClientDbContext> contexts, TimeProvider time)
    {
        // Store dependencies
        this.contexts = contexts;
        this.time = time;
    }

    /// <summary>Create or upgrades the database schema.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // Apply pending migrations
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Return the stored account, or null on first run.</summary>
    public async Task<StoredAccount?> LoadAccountAsync(CancellationToken cancellationToken = default)
    {
        // Read the single row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return account
        return await db.Accounts.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Store <paramref name="session"/>, keeping the sync position when the user is the same.</summary>
    public async Task SaveSessionAsync(MatrixSession session, CancellationToken cancellationToken = default)
    {
        // Find or add row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        StoredAccount? account = await db.Accounts.SingleOrDefaultAsync(cancellationToken);
        if (account is not null && account.UserId != session.UserId) db.Accounts.Remove(account);
        if (account is null || account.UserId != session.UserId) db.Accounts.Add(account = new StoredAccount { UserId = session.UserId, Homeserver = session.Homeserver.AbsoluteUri });

        // Update session
        account.Homeserver = session.Homeserver.AbsoluteUri;
        account.DeviceId = session.DeviceId;
        account.AccessToken = session.AccessToken;
        account.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Forget the access token, e.g. after the server rejected it; history is kept.</summary>
    public async Task ClearAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Clear token
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Accounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.AccessToken, (string?)null), cancellationToken);
    }

    /// <summary>Store timeline events of <paramref name="sync"/>, applies redactions, confirms echoed outbox messages and save the sync position, all in one transaction.</summary>
    /// <param name="sync">Sync response.</param>
    /// <param name="ownUserId">Logged-in user; their echoed events confirm outbox messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveSyncAsync(SyncResponse sync, string ownUserId, CancellationToken cancellationToken = default)
    {
        // Collect timeline events with their room
        List<ClientEvent> events = [];
        foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join) events.AddRange(room.Timeline.Events.Select(e => e with { RoomId = roomId }));
        foreach ((string roomId, LeftRoom room) in sync.Rooms.Leave) events.AddRange(room.Timeline.Events.Select(e => e with { RoomId = roomId }));

        // Skip events already stored, e.g. after a full-state resync
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> ids = events.Select(e => e.EventId).ToList();
        HashSet<string> known = [.. await db.Events.Where(e => ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(cancellationToken)];
        Dictionary<string, StoredEvent> added = [];

        // Add events and apply effects
        foreach (ClientEvent ev in events)
        {
            if (known.Contains(ev.EventId) || added.ContainsKey(ev.EventId)) continue;
            StoredEvent stored = ToStored(ev);
            db.Events.Add(stored);
            added[ev.EventId] = stored;
            if (ev.Type == EventTypes.RoomRedaction) await RedactAsync(db, ev, added, cancellationToken);
            if (ev.Sender == ownUserId && ev.Unsigned?.TransactionId is string txnId) await ConfirmEchoAsync(db, txnId, ev.EventId, cancellationToken);
        }

        // Save position
        StoredAccount? account = await db.Accounts.SingleOrDefaultAsync(cancellationToken);
        if (account is not null) account.NextBatch = sync.NextBatch;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Store events of <paramref name="roomId"/> loaded from history; already redacted by the server, so no effects are applied.</summary>
    public async Task SaveHistoryAsync(string roomId, IReadOnlyList<ClientEvent> events, CancellationToken cancellationToken = default)
    {
        // Skip known events
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> ids = events.Select(e => e.EventId).ToList();
        HashSet<string> known = [.. await db.Events.Where(e => ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(cancellationToken)];

        // Add new events
        foreach (ClientEvent ev in events)
        {
            if (!known.Add(ev.EventId)) continue;
            db.Events.Add(ToStored(ev with { RoomId = roomId }));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return stored events of a room still encrypted with Megolm session <paramref name="sessionId"/>, to retry after its key arrived.</summary>
    public async Task<IReadOnlyList<ClientEvent>> GetEncryptedEventsAsync(string roomId, string sessionId, CancellationToken cancellationToken = default)
    {
        // Encrypted events mentioning the session, escaped like stored JSON, which writes '+' as \u002B
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string marker = $"\"session_id\":{JsonSerializer.Serialize(sessionId, MatrixJson.Options)}";
        List<StoredEvent> rows = await db.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Type == EventTypes.RoomEncrypted && e.Json.Contains(marker)).ToListAsync(cancellationToken);
        // Return events
        return [.. rows.Select(FromStored)];
    }

    /// <summary>Return every stored event that is still encrypted, with its room.</summary>
    public async Task<IReadOnlyList<(string RoomId, ClientEvent Event)>> GetAllEncryptedEventsAsync(CancellationToken cancellationToken = default)
    {
        // Encrypted rows
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<StoredEvent> rows = await db.Events.AsNoTracking().Where(e => e.Type == EventTypes.RoomEncrypted).ToListAsync(cancellationToken);
        // Return events with rooms
        return [.. rows.Select(r => (r.RoomId, FromStored(r)))];
    }

    /// <summary>Replace a stored event, e.g. with its decrypted form.</summary>
    public async Task ReplaceEventAsync(ClientEvent clientEvent, CancellationToken cancellationToken = default)
    {
        // Update the row in place
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        StoredEvent? row = await db.Events.SingleOrDefaultAsync(e => e.EventId == clientEvent.EventId, cancellationToken);
        if (row is null) return;
        StoredEvent updated = ToStored(clientEvent with { RoomId = row.RoomId });
        row.Type = updated.Type;
        row.StateKey = updated.StateKey;
        row.Json = updated.Json;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return up to <paramref name="limit"/> stored events of a room before <paramref name="beforeTs"/>, oldest first.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="limit">Maximum number of events.</param>
    /// <param name="beforeTs">Exclusive upper bound of <see cref="ClientEvent.OriginServerTs"/>; newest events when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<ClientEvent>> GetRoomEventsAsync(string roomId, int limit, long? beforeTs = null, CancellationToken cancellationToken = default)
    {
        // Query newest first
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        IQueryable<StoredEvent> query = db.Events.AsNoTracking().Where(e => e.RoomId == roomId);
        if (beforeTs is long ts) query = query.Where(e => e.OriginServerTs < ts);
        List<StoredEvent> rows = await query.OrderByDescending(e => e.OriginServerTs).Take(limit).ToListAsync(cancellationToken);
        // Return oldest first
        return rows.AsEnumerable().Reverse().Select(FromStored).ToList();
    }

    /// <summary>Queue an event for sending with a fixed transaction ID.</summary>
    public async Task<OutboxMessage> EnqueueAsync(string roomId, string eventType, object content, CancellationToken cancellationToken = default)
    {
        // Add pending row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        OutboxMessage message = new()
        {
            RoomId = roomId,
            EventType = eventType,
            ContentJson = JsonSerializer.Serialize(content, content.GetType(), MatrixJson.Options),
            TransactionId = $"terminal.{Guid.NewGuid():N}",
            State = OutboxState.Pending,
            CreatedAt = time.GetUtcNow(),
        };
        db.Outbox.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        // Return queued message
        return message;
    }

    /// <summary>Return messages waiting to be sent, oldest first.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        // Query pending in enqueue order
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return messages
        return await db.Outbox.AsNoTracking().Where(m => m.State == OutboxState.Pending).OrderBy(m => m.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Return the messages (<see cref="EventTypes.RoomMessage"/>) of a room in the outbox that are not confirmed, for showing them as pending or failed; other events, such as reactions and call signalling, are left out.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> GetUnsentAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // Query unsent in enqueue order
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return messages
        return await db.Outbox.AsNoTracking().Where(m => m.RoomId == roomId && m.State != OutboxState.Sent && m.EventType == EventTypes.RoomMessage).OrderBy(m => m.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Mark a message accepted by the server as <paramref name="eventId"/>.</summary>
    public async Task MarkSentAsync(long id, string eventId, CancellationToken cancellationToken = default)
    {
        // Update row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = time.GetUtcNow();
        await db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, OutboxState.Sent).SetProperty(m => m.EventId, eventId).SetProperty(m => m.SentAt, now).SetProperty(m => m.Attempts, m => m.Attempts + 1), cancellationToken);
    }

    /// <summary>Record a failed attempt; <paramref name="permanent"/> failures are not retried.</summary>
    public async Task MarkAttemptFailedAsync(long id, string error, bool permanent, CancellationToken cancellationToken = default)
    {
        // Update row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        OutboxState state = permanent ? OutboxState.Failed : OutboxState.Pending;
        await db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, state).SetProperty(m => m.LastError, error).SetProperty(m => m.Attempts, m => m.Attempts + 1), cancellationToken);
    }

    /// <summary>Return stored media of <paramref name="uri"/>, or null when not downloaded.</summary>
    public Task<StoredMedia?> FindMediaAsync(MxcUri uri, CancellationToken cancellationToken = default) => FindMediaAsync(uri.ToString(), cancellationToken);

    /// <summary>Return stored media by key, i.e. a content URI optionally with a thumbnail suffix, or null.</summary>
    public async Task<StoredMedia?> FindMediaAsync(string key, CancellationToken cancellationToken = default)
    {
        // Look up by key
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return row
        return await db.Media.AsNoTracking().SingleOrDefaultAsync(m => m.MxcUri == key, cancellationToken);
    }

    /// <summary>Add or replace media metadata.</summary>
    public async Task SaveMediaAsync(StoredMedia media, CancellationToken cancellationToken = default)
    {
        // Replace existing row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Media.Where(m => m.MxcUri == media.MxcUri).ExecuteDeleteAsync(cancellationToken);
        db.Media.Add(media);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Convert a received event to a row.</summary>
    private static StoredEvent ToStored(ClientEvent clientEvent) => new()
    {
        EventId = clientEvent.EventId,
        RoomId = clientEvent.RoomId!,
        Type = clientEvent.Type,
        Sender = clientEvent.Sender,
        StateKey = clientEvent.StateKey,
        OriginServerTs = clientEvent.OriginServerTs,
        Redacted = clientEvent.Unsigned?.RedactedBecause is not null,
        Json = JsonSerializer.Serialize(clientEvent, MatrixJson.Options),
    };

    /// <summary>Convert a row back to an event.</summary>
    private static ClientEvent FromStored(StoredEvent row) => JsonSerializer.Deserialize<ClientEvent>(row.Json, MatrixJson.Options)!;

    /// <summary>Strip content of the event targeted by <paramref name="redaction"/>, whether stored earlier or added in this batch.</summary>
    private static async Task RedactAsync(ClientDbContext dbContext, ClientEvent redaction, Dictionary<string, StoredEvent> added, CancellationToken cancellationToken)
    {
        // Find target; room version 11 moved redacts into content
        string? targetId = redaction.Redacts ?? (redaction.TryGetContent(out RedactionContent? content) ? content.Redacts : null);
        if (targetId is null) return;
        StoredEvent? target = added.GetValueOrDefault(targetId) ?? await dbContext.Events.SingleOrDefaultAsync(e => e.EventId == targetId, cancellationToken);
        if (target is null) return;

        // Replace content, keep the redaction for display
        ClientEvent original = FromStored(target);
        ClientEvent redacted = original with { Content = JsonDocument.Parse("{}").RootElement.Clone(), Unsigned = (original.Unsigned ?? new UnsignedData()) with { RedactedBecause = redaction } };
        target.Json = JsonSerializer.Serialize(redacted, MatrixJson.Options);
        target.Redacted = true;
    }

    /// <summary>Mark an outbox message sent when its echo arrives, covering a crash between send and <see cref="MarkSentAsync"/>.</summary>
    private async Task ConfirmEchoAsync(ClientDbContext dbContext, string transactionId, string eventId, CancellationToken cancellationToken)
    {
        // Find unconfirmed message
        OutboxMessage? message = await dbContext.Outbox.SingleOrDefaultAsync(m => m.TransactionId == transactionId && m.State != OutboxState.Sent, cancellationToken);
        if (message is null) return;

        // Confirm
        message.State = OutboxState.Sent;
        message.EventId = eventId;
        message.SentAt = time.GetUtcNow();
    }
}
