namespace Uniform.Matrix.Server.Rooms;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#receipts">Read receipts</see> and <see href="https://spec.matrix.org/latest/client-server-api/#fully-read-markers">read markers</see> of local users.</summary>
/// <remarks>Only the latest receipt per user, type and thread is kept; a receipt for an earlier event than the stored one is ignored, so receipts only move forward.</remarks>
public sealed class ReceiptService
{
    /// <summary>Public read receipt.</summary>
    public const string Read = "m.read";
    /// <summary>Read receipt only the user's own devices see.</summary>
    public const string ReadPrivate = "m.read.private";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly AccountDataService accountData;
    private readonly TimeProvider time;

    /// <summary>Create the service.</summary>
    public ReceiptService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, AccountDataService accountData, TimeProvider time)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.accountData = accountData;
        this.time = time;
    }

    /// <summary>Store a receipt of <paramref name="type"/>; <c>m.fully_read</c> moves the read marker instead.</summary>
    /// <param name="userId">Reader, joined to the room.</param>
    /// <param name="roomId">Room.</param>
    /// <param name="type"><see cref="Read"/>, <see cref="ReadPrivate"/> or <c>m.fully_read</c>.</param>
    /// <param name="eventId">Event of the room read up to.</param>
    /// <param name="threadId">Thread root, <c>main</c>, or null for an unthreaded receipt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetReceiptAsync(string userId, string roomId, string type, string eventId, string? threadId, CancellationToken cancellationToken)
    {
        // Assert type, membership and event
        if (type is not (Read or ReadPrivate or AccountDataService.FullyReadType)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown receipt type {type}.");
        if (type == AccountDataService.FullyReadType && threadId is not null) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The fully read marker has no thread.");
        long eventStream = await GetEventStreamAsync(userId, roomId, eventId, cancellationToken);

        // Read marker is room account data
        if (type == AccountDataService.FullyReadType)
        {
            await accountData.SetAsync(userId, roomId, AccountDataService.FullyReadType, new JsonObject { ["event_id"] = eventId }, cancellationToken);
            return;
        }

        // Latest receipt per type and thread, only forward
        string thread = threadId ?? "";
        await stream.WriteAsync(async position =>
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            ReceiptRow? row = await db.Receipts.SingleOrDefaultAsync(r => r.RoomId == roomId && r.UserId == userId && r.Type == type && r.ThreadId == thread, cancellationToken);
            if (row is not null && await db.Events.Where(e => e.EventId == row.EventId).Select(e => e.Stream).SingleOrDefaultAsync(cancellationToken) >= eventStream) return false;
            if (row is null) db.Receipts.Add(row = new ReceiptRow { RoomId = roomId, UserId = userId, Type = type, ThreadId = thread, EventId = eventId });
            row.EventId = eventId;
            row.Ts = time.GetUtcNow().ToUnixTimeMilliseconds();
            row.Stream = position;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Set the read markers of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidread_markers">/read_markers</see>: any of <c>m.fully_read</c>, <c>m.read</c> and <c>m.read.private</c>.</summary>
    public async Task SetReadMarkersAsync(string userId, string roomId, JsonObject body, CancellationToken cancellationToken)
    {
        // Each given marker
        foreach (string type in new[] { AccountDataService.FullyReadType, Read, ReadPrivate })
        {
            if (body[type]?.GetValue<string>() is not string eventId) continue;
            await SetReceiptAsync(userId, roomId, type, eventId, threadId: null, cancellationToken);
        }
    }

    /// <summary>Return the receipts of a room changed after <paramref name="since"/> (all when null) as an <c>m.receipt</c> event for <paramref name="userId"/>, or null when there are none.</summary>
    /// <remarks>Private receipts of other users are left out.</remarks>
    public static async Task<JsonObject?> GetReceiptEventAsync(ServerDbContext dbContext, string roomId, string userId, long? since, long until, CancellationToken cancellationToken)
    {
        // Changed receipts the user may see
        List<ReceiptRow> rows = await dbContext.Receipts.AsNoTracking().Where(r => r.RoomId == roomId && r.Stream <= until && (since == null || r.Stream > since) && (r.Type != ReadPrivate || r.UserId == userId)).ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;

        // {event: {type: {user: {ts, thread_id}}}}
        JsonObject content = [];
        foreach (ReceiptRow row in rows)
        {
            JsonObject byType = content[row.EventId] as JsonObject ?? (JsonObject)(content[row.EventId] = new JsonObject());
            JsonObject byUser = byType[row.Type] as JsonObject ?? (JsonObject)(byType[row.Type] = new JsonObject());
            JsonObject receipt = new() { ["ts"] = row.Ts };
            if (row.ThreadId.Length > 0) receipt["thread_id"] = row.ThreadId;
            byUser[row.UserId] = receipt;
        }
        // Return event
        return new JsonObject { ["type"] = "m.receipt", ["content"] = content };
    }

    /// <summary>Return the stream position of an event of the room; throw when the user is not joined or the event is unknown.</summary>
    private async Task<long> GetEventStreamAsync(string userId, string roomId, string eventId, CancellationToken cancellationToken)
    {
        // Joined user, event of this room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        bool joined = await db.CurrentState.AnyAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join, cancellationToken);
        if (!joined) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not joined to the room.");
        long? position = await db.Events.AsNoTracking().Where(e => e.EventId == eventId && e.RoomId == roomId).Select(e => (long?)e.Stream).SingleOrDefaultAsync(cancellationToken);
        // Return position
        return position ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown event.");
    }
}
