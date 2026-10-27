namespace Uniform.Matrix.Server.Federation;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#typing-notifications">Typing</see>, <see href="https://spec.matrix.org/latest/server-server-api/#receipts">read receipts</see> and received <see href="https://spec.matrix.org/latest/server-server-api/#presence">presence</see> with other servers: local users' ones go to the room's other servers as EDUs, and theirs are shown here.</summary>
/// <remarks>Only public read receipts leave this server; private ones and the fully read marker stay. Remote typing ends after <see cref="RemoteTypingTimeout"/> unless renewed, as servers do not send an end when a client goes away.</remarks>
public sealed class RemoteActivity
{
    /// <summary>How long a remote user shows as typing without a renewal.</summary>
    public static readonly TimeSpan RemoteTypingTimeout = TimeSpan.FromSeconds(30);
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly FederationQueue queue;
    private readonly TypingService typing;
    private readonly ReceiptService receipts;
    private readonly PresenceService presence;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<RemoteActivity> logger;

    /// <summary>Create the service.</summary>
    public RemoteActivity(IDbContextFactory<ServerDbContext> contexts, FederationQueue queue, TypingService typing, ReceiptService receipts, PresenceService presence, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, TimeProvider time, ILogger<RemoteActivity> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.queue = queue;
        this.typing = typing;
        this.receipts = receipts;
        this.presence = presence;
        this.server = server;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Tell the room's other servers that a local user started or stopped typing.</summary>
    public async Task SendTypingAsync(string userId, string roomId, bool isTyping, CancellationToken cancellationToken)
    {
        // An m.typing EDU to each
        JsonObject edu = new() { ["edu_type"] = "m.typing", ["content"] = new JsonObject { ["room_id"] = roomId, ["user_id"] = userId, ["typing"] = isTyping } };
        foreach (string destination in await OtherServersAsync(roomId, cancellationToken)) queue.EnqueueEdu(destination, edu);
    }

    /// <summary>Tell the room's other servers about a local user's public read receipt.</summary>
    public async Task SendReceiptAsync(string userId, string roomId, string type, string eventId, string? threadId, CancellationToken cancellationToken)
    {
        // Public read receipts only, as an m.receipt EDU
        if (type != ReceiptService.Read) return;
        JsonObject data = new() { ["ts"] = time.GetUtcNow().ToUnixTimeMilliseconds() };
        if (threadId is not null) data["thread_id"] = threadId;
        JsonObject edu = new() { ["edu_type"] = "m.receipt", ["content"] = new JsonObject { [roomId] = new JsonObject { [ReceiptService.Read] = new JsonObject { [userId] = new JsonObject { ["event_ids"] = new JsonArray(eventId), ["data"] = data } } } } };
        foreach (string destination in await OtherServersAsync(roomId, cancellationToken)) queue.EnqueueEdu(destination, edu);
    }

    /// <summary>Show an <c>m.typing</c> EDU of <paramref name="origin"/>'s user in a room they are in.</summary>
    public async Task ReceiveTypingAsync(string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert a user of the origin
        if (!UserId.TryParse(content["user_id"]?.GetValue<string>(), out UserId user) || user.ServerName != origin || content["room_id"]?.GetValue<string>() is not string roomId) return;
        try
        {
            await typing.SetTypingAsync(user.ToString(), roomId, content["typing"]?.GetValue<bool>() == true, RemoteTypingTimeout, cancellationToken);
        }
        catch (MatrixException ex)
        {
            // On error ignore, e.g. not in the room
            logger.LogDebug("Typing of {UserId} in {RoomId} ignored: {Error}", user, roomId, ex.Message);
        }
    }

    /// <summary>Store the read receipts of an <c>m.receipt</c> EDU of <paramref name="origin"/>'s users.</summary>
    public async Task ReceiveReceiptsAsync(string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Rooms, then users of the origin, then their latest event
        foreach ((string roomId, JsonNode? byType) in content)
        {
            foreach ((string userId, JsonNode? receipt) in byType?[ReceiptService.Read] as JsonObject ?? [])
            {
                if (!UserId.TryParse(userId, out UserId user) || user.ServerName != origin) continue;
                if ((receipt?["event_ids"] as JsonArray)?.LastOrDefault()?.GetValue<string>() is not string eventId) continue;
                try
                {
                    await receipts.SetReceiptAsync(userId, roomId, ReceiptService.Read, eventId, receipt?["data"]?["thread_id"]?.GetValue<string>(), cancellationToken);
                }
                catch (MatrixException ex)
                {
                    // Ignore, e.g. an event not here yet
                    logger.LogDebug("Receipt of {UserId} in {RoomId} ignored: {Error}", userId, roomId, ex.Message);
                }
            }
        }
    }

    /// <summary>Show the presence of an <c>m.presence</c> EDU of <paramref name="origin"/>'s users.</summary>
    public async Task ReceivePresenceAsync(string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Each push of a user of the origin
        foreach (JsonObject push in (content["push"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!UserId.TryParse(push["user_id"]?.GetValue<string>(), out UserId user) || user.ServerName != origin || push["presence"]?.GetValue<string>() is not string state) continue;
            string? status = push["status_msg"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
            await presence.SetRemoteAsync(user.ToString(), state, status, push["last_active_ago"]?.GetValue<long>() ?? 0, cancellationToken);
        }
    }

    /// <summary>Return the servers, other than this one, with members joined to the room; none with federation off.</summary>
    private async Task<IReadOnlyCollection<string>> OtherServersAsync(string roomId, CancellationToken cancellationToken)
    {
        // Servers of the joined members
        if (!options.CurrentValue.Enabled) return [];
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> members = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).ToListAsync(cancellationToken);
        HashSet<string> servers = [.. members.Select(m => UserId.TryParse(m, out UserId member) ? member.ServerName : null).OfType<string>()];
        servers.Remove(server.CurrentValue.ServerName);
        // Return servers
        return servers;
    }
}
