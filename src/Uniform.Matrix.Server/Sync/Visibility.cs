namespace Uniform.Matrix.Server.Sync;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>Which events of a room a user may see under its <see href="https://spec.matrix.org/latest/client-server-api/#history-visibility">history visibility</see>, and without the messages of <see href="https://spec.matrix.org/latest/client-server-api/#ignoring-users">ignored users</see>, for one request.</summary>
internal sealed class Visibility
{
    private readonly ServerDbContext db;
    private readonly StateCache cache;
    private readonly string userId;
    private readonly string? currentMembership;
    private readonly Dictionary<string, JsonObject?> contents = [];
    private HashSet<string>? ignored;

    /// <summary>Check events for <paramref name="userId"/>, whose membership in the room is now <paramref name="currentMembership"/>.</summary>
    public Visibility(ServerDbContext dbContext, StateCache cache, string userId, string? currentMembership)
    {
        // Store context
        this.db = dbContext;
        this.cache = cache;
        this.userId = userId;
        this.currentMembership = currentMembership;
    }

    /// <summary>Return whether the user may see <paramref name="row"/>.</summary>
    public async Task<bool> CanSeeAsync(EventRow row, CancellationToken cancellationToken)
    {
        // Own membership changes are always visible; others' messages not when the user ignores them, though their state changes are
        if (row.Type == EventTypes.RoomMember && row.StateKey == userId) return true;
        if (row.StateKey is null && row.Sender != userId && (await IgnoredAsync(cancellationToken)).Contains(row.Sender)) return false;

        // Visibility and membership at the event
        IReadOnlyDictionary<(string Type, string StateKey), string> state = await StateGroups.LoadAsync(db, row.StateGroup, cache, cancellationToken);
        string visibility = (await ContentAsync(state.GetValueOrDefault(("m.room.history_visibility", "")), cancellationToken))?["history_visibility"]?.GetValue<string>() ?? "shared";
        string? membership = (await ContentAsync(state.GetValueOrDefault((EventTypes.RoomMember, userId)), cancellationToken))?["membership"]?.GetValue<string>();
        // Return verdict
        return visibility == "world_readable"
            || membership == Memberships.Join
            || visibility == "shared" && currentMembership == Memberships.Join
            || visibility == "invited" && membership == Memberships.Invite;
    }

    /// <summary>Return the users the user ignores, read once per request.</summary>
    private async Task<HashSet<string>> IgnoredAsync(CancellationToken cancellationToken)
    {
        // Cached per request
        if (ignored is not null) return ignored;
        ignored = await IgnoredUsersAsync(db, userId, cancellationToken);
        // Return users
        return ignored;
    }

    /// <summary>Return the user IDs in the <see cref="EventTypes.IgnoredUserList"/> account data of <paramref name="userId"/>; empty when there is none.</summary>
    public static async Task<HashSet<string>> IgnoredUsersAsync(ServerDbContext dbContext, string userId, CancellationToken cancellationToken)
    {
        // Global account data
        string? json = await dbContext.AccountData.AsNoTracking().Where(a => a.UserId == userId && a.RoomId == "" && a.Type == EventTypes.IgnoredUserList).Select(a => a.Json).SingleOrDefaultAsync(cancellationToken);
        if (json is null || JsonNode.Parse(json)?["ignored_users"] is not JsonObject users) return [];
        // Return users
        return [.. users.Select(u => u.Key)];
    }

    /// <summary>Return an event's content, once per event.</summary>
    private async Task<JsonObject?> ContentAsync(string? eventId, CancellationToken cancellationToken)
    {
        // Cached per request
        if (eventId is null) return null;
        if (contents.TryGetValue(eventId, out JsonObject? cached)) return cached;
        string? json = await db.Events.AsNoTracking().Where(e => e.EventId == eventId).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        JsonObject? content = json is null ? null : JsonNode.Parse(json)!["content"] as JsonObject;
        contents[eventId] = content;
        // Return content
        return content;
    }
}
