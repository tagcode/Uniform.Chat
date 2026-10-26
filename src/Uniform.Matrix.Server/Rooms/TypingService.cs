namespace Uniform.Matrix.Server.Rooms;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#typing-notifications">Typing notifications</see>, kept in memory only.</summary>
/// <remarks>Each change, including a typing timeout running out, takes a <see cref="ServerStream"/> position, so waiting syncs wake and incremental syncs see it.</remarks>
public sealed class TypingService : IDisposable
{
    /// <summary>Longest timeout a client may ask for.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly TimeProvider time;
    private readonly ILogger<TypingService> logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private readonly ITimer timer;

    /// <summary>Typing users of a room and the position of its last change.</summary>
    private sealed class Room
    {
        /// <summary>Typing users and when their typing ends.</summary>
        public Dictionary<string, DateTimeOffset> Users { get; } = [];
        /// <summary>Stream position of the last change.</summary>
        public long Changed { get; set; }
    }

    /// <summary>Create the service and its expiry timer.</summary>
    public TypingService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, TimeProvider time, ILogger<TypingService> logger)
    {
        // Store dependencies; check timeouts every second
        this.contexts = contexts;
        this.stream = stream;
        this.time = time;
        this.logger = logger;
        timer = time.CreateTimer(_ => _ = ExpireAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Start or stop <paramref name="userId"/> typing in a room they are joined to.</summary>
    public async Task SetTypingAsync(string userId, string roomId, bool typing, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Assert joined
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            bool joined = await db.CurrentState.AnyAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join, cancellationToken);
            if (!joined) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Not joined to the room.");
        }

        // Change under a new position; renewing only moves the deadline
        DateTimeOffset until = time.GetUtcNow() + (timeout > MaxTimeout ? MaxTimeout : timeout);
        lock (gate)
        {
            Room room = GetRoom(roomId);
            bool wasTyping = room.Users.ContainsKey(userId);
            if (typing) room.Users[userId] = until;
            else room.Users.Remove(userId);
            if (wasTyping == typing) return;
        }
        await stream.WriteAsync(position => Task.FromResult(Mark(roomId, position)), cancellationToken);
    }

    /// <summary>Return the room's <c>m.typing</c> event when it changed after <paramref name="since"/>, or for initial syncs (null) when someone types.</summary>
    public JsonObject? GetTypingEvent(string roomId, long? since)
    {
        // Snapshot under the lock
        List<string> users;
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out Room? room)) return null;
            if (since is null ? room.Users.Count == 0 : room.Changed <= since) return null;
            users = [.. room.Users.Keys.Order(StringComparer.Ordinal)];
        }
        // Return event
        return new JsonObject { ["type"] = "m.typing", ["content"] = new JsonObject { ["user_ids"] = new JsonArray([.. users.Select(u => (JsonNode)u)]) } };
    }

    /// <summary>Stop the timer.</summary>
    public void Dispose() => timer.Dispose();

    /// <summary>Remove users whose typing timed out, under one new position.</summary>
    private async Task ExpireAsync()
    {
        // Rooms with expired users
        DateTimeOffset now = time.GetUtcNow();
        List<string> expired = [];
        lock (gate)
        {
            foreach ((string roomId, Room room) in rooms)
            {
                int before = room.Users.Count;
                foreach (string userId in room.Users.Where(u => u.Value <= now).Select(u => u.Key).ToList()) room.Users.Remove(userId);
                if (room.Users.Count != before) expired.Add(roomId);
            }
        }
        if (expired.Count == 0) return;

        // One position for all of them
        try
        {
            await stream.WriteAsync(position => Task.FromResult(expired.All(r => Mark(r, position))), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish typing timeouts");
        }
    }

    /// <summary>Record a change of a room at <paramref name="position"/>.</summary>
    private bool Mark(string roomId, long position)
    {
        // Under the lock
        lock (gate) GetRoom(roomId).Changed = position;
        // Done
        return true;
    }

    /// <summary>Return the room's entry, creating it; call under the lock.</summary>
    private Room GetRoom(string roomId)
    {
        // Created on first use
        if (!rooms.TryGetValue(roomId, out Room? room)) rooms[roomId] = room = new Room();
        // Return room
        return room;
    }
}
