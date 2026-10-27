namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#presence">Presence</see>, kept in memory: what users set, and online while they sync.</summary>
/// <remarks>Each change takes a <see cref="ServerStream"/> position, so waiting syncs wake and incremental syncs send <c>m.presence</c> to everyone who shares a room. Syncing marks a user online unless <c>set_presence</c> says otherwise; a user who has not synced for <see cref="OfflineAfter"/> goes offline. Users of other servers get their presence from their server, see <see cref="SetRemoteAsync"/>. Presence is lost on restart, and users start offline.</remarks>
public sealed class PresenceService : IDisposable
{
    /// <summary>How long after their last sync or presence update a user counts as offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);
    /// <summary>How long after their last activity a user still counts as currently active.</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromMinutes(1);
    /// <summary>How long a user of another server keeps their presence here without an update from their server, which normally tells when they go offline.</summary>
    public static readonly TimeSpan RemoteOfflineAfter = TimeSpan.FromHours(1);
    private const string Online = "online";
    private const string Offline = "offline";
    private const string Unavailable = "unavailable";
    private static readonly string[] States = [Online, Offline, Unavailable];
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly TimeProvider time;
    private readonly ILogger<PresenceService> logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> users = [];
    private readonly ITimer timer;

    /// <summary>One user's presence.</summary>
    private sealed class Entry
    {
        /// <summary><c>online</c>, <c>unavailable</c> or <c>offline</c>.</summary>
        public string State { get; set; } = Offline;
        /// <summary>Status message, or null.</summary>
        public string? Status { get; set; }
        /// <summary>Last sync or presence update.</summary>
        public DateTimeOffset LastActive { get; set; }
        /// <summary>Whether the user chose the state with the presence API, so syncing does not change it.</summary>
        public bool Chosen { get; set; }
        /// <summary>Stream position of the last change that others see.</summary>
        public long Changed { get; set; }
        /// <summary>Whether the user is of another server, whose presence comes from there.</summary>
        public bool Remote { get; set; }
        /// <summary>When the other server last told about a remote user.</summary>
        public DateTimeOffset Updated { get; set; }
    }

    /// <summary>Create the service and its timer, which takes idle users offline.</summary>
    public PresenceService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, TimeProvider time, ILogger<PresenceService> logger)
    {
        // Store dependencies; look for idle users every few seconds
        this.contexts = contexts;
        this.stream = stream;
        this.time = time;
        this.logger = logger;
        timer = time.CreateTimer(_ => _ = ExpireAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Set a user's presence and status message with the presence API.</summary>
    public async Task SetAsync(string userId, string presence, string? status, CancellationToken cancellationToken)
    {
        // Assert known state
        if (!States.Contains(presence)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown presence {presence}.");

        // Change; a new position only when others would see a difference
        bool changed;
        lock (gate)
        {
            Entry entry = GetEntry(userId);
            changed = entry.State != presence || entry.Status != status;
            entry.State = presence;
            entry.Status = status;
            entry.LastActive = time.GetUtcNow();
            entry.Chosen = presence != Online;
        }
        // Done
        if (changed) await PublishAsync([userId], cancellationToken);
    }

    /// <summary>Record that a user syncs: online, or idle when <paramref name="setPresence"/> is <c>unavailable</c>; <c>offline</c> leaves the presence as it is.</summary>
    /// <param name="userId">The syncing user.</param>
    /// <param name="setPresence">The sync's <c>set_presence</c>, or null for the default, <c>online</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TouchAsync(string userId, string? setPresence, CancellationToken cancellationToken)
    {
        // Assert a known value; offline syncs change nothing
        if (setPresence is not null && !States.Contains(setPresence)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown set_presence {setPresence}.");
        if (setPresence == Offline) return;

        // Activity always; the state only when the user did not choose one, e.g. offline to appear away
        bool changed;
        lock (gate)
        {
            Entry entry = GetEntry(userId);
            entry.LastActive = time.GetUtcNow();
            string wanted = setPresence ?? Online;
            changed = !entry.Chosen && entry.State != wanted;
            if (!changed) return;
            entry.State = wanted;
            entry.Chosen = false;
        }
        // Done
        await PublishAsync([userId], cancellationToken);
    }

    /// <summary>Return a user's presence for the presence API; <c>offline</c> when unknown.</summary>
    public JsonObject Get(string userId)
    {
        // Content as in m.presence, without the user ID
        lock (gate)
        {
            // Return presence
            return users.TryGetValue(userId, out Entry? entry) ? Content(entry) : new JsonObject { ["presence"] = Offline };
        }
    }

    /// <summary>Set the presence of another server's user from an <c>m.presence</c> EDU of their server.</summary>
    /// <param name="userId">User of the sending server.</param>
    /// <param name="presence"><c>online</c>, <c>unavailable</c> or <c>offline</c>; other values are ignored.</param>
    /// <param name="status">Status message, or null.</param>
    /// <param name="lastActiveAgo">Milliseconds since the user was last active.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetRemoteAsync(string userId, string presence, string? status, long lastActiveAgo, CancellationToken cancellationToken)
    {
        // Assert a known state
        if (!States.Contains(presence)) return;

        // Change; a new position only when others would see a difference
        DateTimeOffset now = time.GetUtcNow();
        bool changed;
        lock (gate)
        {
            Entry entry = GetEntry(userId);
            changed = entry.State != presence || entry.Status != status;
            entry.State = presence;
            entry.Status = status;
            entry.LastActive = now - TimeSpan.FromMilliseconds(Math.Max(0, lastActiveAgo));
            entry.Remote = true;
            entry.Updated = now;
        }
        // Done
        if (changed) await PublishAsync([userId], cancellationToken);
    }

    /// <summary>Return the presence of users of this server that changed after <paramref name="since"/> up to <paramref name="until"/>, for other servers.</summary>
    public IReadOnlyList<(string UserId, JsonObject Content)> GetLocalChanges(long since, long until)
    {
        // Local users only, as other servers tell about their own
        lock (gate)
        {
            // Return changes
            return [.. users.Where(u => !u.Value.Remote && u.Value.Changed > since && u.Value.Changed <= until).Select(u => (u.Key, Content(u.Value)))];
        }
    }

    /// <summary>Return <c>m.presence</c> events of the user and of everyone sharing a joined room with them that changed after <paramref name="since"/>; for initial syncs (null), of everyone known.</summary>
    public async Task<IReadOnlyList<JsonObject>> GetEventsAsync(ServerDbContext dbContext, string userId, long? since, long until, CancellationToken cancellationToken)
    {
        // Changed users first, as most syncs have none
        List<(string UserId, JsonObject Content)> changed;
        lock (gate) changed = [.. users.Where(u => u.Value.Changed <= until && (since is null || u.Value.Changed > since)).Select(u => (u.Key, Content(u.Value)))];
        if (changed.Count == 0) return [];

        // Of those, the ones the user may see: themselves and people sharing a joined room
        List<string> rooms = await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
        List<string> candidates = [.. changed.Select(c => c.UserId)];
        HashSet<string> visible = [userId, .. await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && rooms.Contains(s.RoomId) && candidates.Contains(s.StateKey)).Select(s => s.StateKey).Distinct().ToListAsync(cancellationToken)];
        // Return events
        return [.. changed.Where(c => visible.Contains(c.UserId)).OrderBy(c => c.UserId, StringComparer.Ordinal).Select(c => new JsonObject { ["type"] = "m.presence", ["sender"] = c.UserId, ["content"] = c.Content })];
    }

    /// <summary>Stop the timer.</summary>
    public void Dispose() => timer.Dispose();

    /// <summary>Take users who stopped syncing offline, under one new position.</summary>
    private async Task ExpireAsync()
    {
        // Online or idle users quiet for too long
        DateTimeOffset now = time.GetUtcNow();
        List<string> expired = [];
        lock (gate)
        {
            foreach ((string userId, Entry entry) in users.Where(u => u.Value.State != Offline && (u.Value.Remote ? now - u.Value.Updated >= RemoteOfflineAfter : now - u.Value.LastActive >= OfflineAfter)))
            {
                entry.State = Offline;
                entry.Chosen = false;
                expired.Add(userId);
            }
        }
        if (expired.Count == 0) return;

        // One position for all of them
        try
        {
            await PublishAsync(expired, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish presence timeouts");
        }
    }

    /// <summary>Give the changes of <paramref name="userIds"/> one new stream position, which wakes waiting syncs.</summary>
    private Task PublishAsync(IReadOnlyList<string> userIds, CancellationToken cancellationToken) =>
        stream.WriteAsync(position =>
        {
            // Under the lock
            lock (gate)
            {
                foreach (string userId in userIds) GetEntry(userId).Changed = position;
            }
            // Done
            return Task.FromResult(true);
        }, cancellationToken);

    /// <summary>Return the user's entry, adding an offline one; call under the lock.</summary>
    private Entry GetEntry(string userId)
    {
        // Add on first use
        if (!users.TryGetValue(userId, out Entry? entry)) users[userId] = entry = new Entry { LastActive = time.GetUtcNow() };
        // Return entry
        return entry;
    }

    /// <summary>Return the <c>m.presence</c> content of an entry; call under the lock.</summary>
    private JsonObject Content(Entry entry)
    {
        // State, activity, and the status when set
        TimeSpan ago = time.GetUtcNow() - entry.LastActive;
        JsonObject content = new() { ["presence"] = entry.State, ["last_active_ago"] = (long)ago.TotalMilliseconds, ["currently_active"] = entry.State == Online && ago < ActiveFor };
        if (entry.Status is not null) content["status_msg"] = entry.Status;
        // Return content
        return content;
    }
}
