namespace Uniform.Matrix.Server.Encryption;

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#server-side-key-backups">Server-side key backups</see>: versions and the encrypted room keys in them.</summary>
/// <remarks>The latest version that is not deleted is current; keys are uploaded only to it. A stored key is replaced only by a better one: verified over unverified, then a lower first message index, then a lower forward count.</remarks>
public sealed class KeyBackupService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;

    /// <summary>Create the service.</summary>
    public KeyBackupService(IDbContextFactory<ServerDbContext> contexts)
    {
        // Store dependency
        this.contexts = contexts;
    }

    /// <summary>Create a version, which becomes current; return its ID.</summary>
    public async Task<string> CreateVersionAsync(string userId, JsonObject body, CancellationToken cancellationToken)
    {
        // Algorithm and auth data are required
        string algorithm = body["algorithm"]?.GetValue<string>() ?? throw Missing("algorithm");
        JsonObject authData = body["auth_data"] as JsonObject ?? throw Missing("auth_data");

        // Next number, also after deleted versions
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        long version = (await db.KeyBackupVersions.Where(v => v.UserId == userId).MaxAsync(v => (long?)v.Version, cancellationToken) ?? 0) + 1;
        db.KeyBackupVersions.Add(new KeyBackupVersionRow { UserId = userId, Version = version, Algorithm = algorithm, AuthDataJson = authData.ToJsonString() });
        await db.SaveChangesAsync(cancellationToken);
        // Return ID
        return version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Return a version, or the current one when <paramref name="version"/> is null, with its key count and etag.</summary>
    public async Task<JsonObject> GetVersionAsync(string userId, string? version, CancellationToken cancellationToken)
    {
        // Existing version
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow row = await FindAsync(db, userId, version, cancellationToken);
        int count = await db.RoomKeys.CountAsync(k => k.UserId == userId && k.Version == row.Version, cancellationToken);
        // Return description
        return new JsonObject
        {
            ["version"] = row.Version.ToString(CultureInfo.InvariantCulture),
            ["algorithm"] = row.Algorithm,
            ["auth_data"] = JsonNode.Parse(row.AuthDataJson),
            ["count"] = count,
            ["etag"] = row.Etag.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Replace a version's auth data; the algorithm and version in the body must match.</summary>
    public async Task UpdateVersionAsync(string userId, string version, JsonObject body, CancellationToken cancellationToken)
    {
        // Same algorithm and version
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow row = await FindAsync(db, userId, version, cancellationToken);
        if (body["algorithm"]?.GetValue<string>() != row.Algorithm) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The algorithm cannot change.");
        if (body["version"]?.GetValue<string>() is string given && given != version) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The version in the body does not match.");
        row.AuthDataJson = (body["auth_data"] as JsonObject ?? throw Missing("auth_data")).ToJsonString();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Delete a version and its keys.</summary>
    public async Task DeleteVersionAsync(string userId, string version, CancellationToken cancellationToken)
    {
        // Mark deleted, so the number is not reused
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow row = await FindAsync(db, userId, version, cancellationToken);
        row.Deleted = true;
        await db.RoomKeys.Where(k => k.UserId == userId && k.Version == row.Version).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Store keys in the current version by room and session, keeping the better of old and new; return the new etag and count.</summary>
    /// <param name="userId">Owner.</param>
    /// <param name="version">Version, which must be current.</param>
    /// <param name="keys">Room ID, then session ID, then key data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> PutKeysAsync(string userId, string version, IEnumerable<(string RoomId, string SessionId, JsonObject Data)> keys, CancellationToken cancellationToken)
    {
        // Only into the current version
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow current = await FindAsync(db, userId, null, cancellationToken);
        if (current.Version.ToString(CultureInfo.InvariantCulture) != version) throw new MatrixErrorException(HttpStatusCode.Forbidden, "M_WRONG_ROOM_KEYS_VERSION", "Not the current backup version.", new JsonObject { ["current_version"] = current.Version.ToString(CultureInfo.InvariantCulture) });

        // Better keys replace stored ones
        bool changed = false;
        foreach ((string roomId, string sessionId, JsonObject data) in keys)
        {
            RoomKeyRow candidate = ToRow(userId, current.Version, roomId, sessionId, data);
            RoomKeyRow? stored = await db.RoomKeys.SingleOrDefaultAsync(k => k.UserId == userId && k.Version == current.Version && k.RoomId == roomId && k.SessionId == sessionId, cancellationToken);
            if (stored is not null && !IsBetter(candidate, stored)) continue;
            if (stored is not null) db.RoomKeys.Remove(stored);
            db.RoomKeys.Add(candidate);
            changed = true;
        }
        if (changed) current.Etag++;
        await db.SaveChangesAsync(cancellationToken);
        // Return etag and count
        return await CountAsync(db, current, cancellationToken);
    }

    /// <summary>Return the keys of a version: all of them, one room's, or one session's.</summary>
    /// <returns><c>{"rooms":{…}}</c>, <c>{"sessions":{…}}</c> for a room, or the key data for a session; throws <c>M_NOT_FOUND</c> for a missing session.</returns>
    public async Task<JsonObject> GetKeysAsync(string userId, string version, string? roomId, string? sessionId, CancellationToken cancellationToken)
    {
        // Keys of the version, narrowed
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow row = await FindAsync(db, userId, version, cancellationToken);
        List<RoomKeyRow> keys = await db.RoomKeys.AsNoTracking().Where(k => k.UserId == userId && k.Version == row.Version && (roomId == null || k.RoomId == roomId) && (sessionId == null || k.SessionId == sessionId)).ToListAsync(cancellationToken);
        if (sessionId is not null) return keys.Count == 1 ? JsonNode.Parse(keys[0].Json)!.AsObject() : throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "No backed-up key for this session.");

        // Grouped by room and session
        JsonObject rooms = [];
        foreach (IGrouping<string, RoomKeyRow> room in keys.GroupBy(k => k.RoomId))
        {
            JsonObject sessions = [];
            foreach (RoomKeyRow key in room) sessions[key.SessionId] = JsonNode.Parse(key.Json);
            rooms[room.Key] = new JsonObject { ["sessions"] = sessions };
        }
        // Return all, or one room
        return roomId is null ? new JsonObject { ["rooms"] = rooms } : rooms[roomId]?.DeepClone() as JsonObject ?? new JsonObject { ["sessions"] = new JsonObject() };
    }

    /// <summary>Delete keys of a version: all of them, one room's, or one session's; return the new etag and count.</summary>
    public async Task<JsonObject> DeleteKeysAsync(string userId, string version, string? roomId, string? sessionId, CancellationToken cancellationToken)
    {
        // Matching keys
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        KeyBackupVersionRow row = await FindAsync(db, userId, version, cancellationToken);
        int deleted = await db.RoomKeys.Where(k => k.UserId == userId && k.Version == row.Version && (roomId == null || k.RoomId == roomId) && (sessionId == null || k.SessionId == sessionId)).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0) row.Etag++;
        await db.SaveChangesAsync(cancellationToken);
        // Return etag and count
        return await CountAsync(db, row, cancellationToken);
    }

    /// <summary>Return a version, or the current one; throw <c>M_NOT_FOUND</c> when missing or deleted.</summary>
    private static async Task<KeyBackupVersionRow> FindAsync(ServerDbContext dbContext, string userId, string? version, CancellationToken cancellationToken)
    {
        // Numbered or latest
        IQueryable<KeyBackupVersionRow> versions = dbContext.KeyBackupVersions.Where(v => v.UserId == userId && !v.Deleted);
        KeyBackupVersionRow? row = version is null
            ? await versions.OrderByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken)
            : long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? await versions.SingleOrDefaultAsync(v => v.Version == number, cancellationToken) : null;
        // Return version
        return row ?? throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, version is null ? "No current backup version." : "Unknown backup version.");
    }

    /// <summary>Return the etag and key count of a version.</summary>
    private static async Task<JsonObject> CountAsync(ServerDbContext dbContext, KeyBackupVersionRow version, CancellationToken cancellationToken) =>
        new() { ["etag"] = version.Etag.ToString(CultureInfo.InvariantCulture), ["count"] = await dbContext.RoomKeys.CountAsync(k => k.UserId == version.UserId && k.Version == version.Version, cancellationToken) };

    /// <summary>Read key data into a row; throw <c>M_INVALID_PARAM</c> when its ranking fields are missing.</summary>
    private static RoomKeyRow ToRow(string userId, long version, string roomId, string sessionId, JsonObject data)
    {
        // Fields that rank keys
        long firstIndex = data["first_message_index"]?.GetValue<long>() ?? throw Missing("first_message_index");
        long forwarded = data["forwarded_count"]?.GetValue<long>() ?? throw Missing("forwarded_count");
        bool verified = data["is_verified"]?.GetValue<bool>() ?? throw Missing("is_verified");
        if (data["session_data"] is not JsonObject) throw Missing("session_data");
        // Return row
        return new RoomKeyRow { UserId = userId, Version = version, RoomId = roomId, SessionId = sessionId, Json = data.ToJsonString(), FirstMessageIndex = firstIndex, ForwardedCount = forwarded, IsVerified = verified };
    }

    /// <summary>Return whether <paramref name="candidate"/> should replace <paramref name="stored"/>.</summary>
    private static bool IsBetter(RoomKeyRow candidate, RoomKeyRow stored)
    {
        // Verified, then earlier index, then fewer forwards
        if (candidate.IsVerified != stored.IsVerified) return candidate.IsVerified;
        if (candidate.FirstMessageIndex != stored.FirstMessageIndex) return candidate.FirstMessageIndex < stored.FirstMessageIndex;
        // Return verdict
        return candidate.ForwardedCount < stored.ForwardedCount;
    }

    /// <summary>Return an <c>M_MISSING_PARAM</c> error.</summary>
    private static MatrixException Missing(string field) => new(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, $"Missing {field}.");
}
