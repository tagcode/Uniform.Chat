namespace Uniform.Matrix.Server.Federation;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>Sends new room events to the other servers in each room as <see href="https://spec.matrix.org/latest/server-server-api/#transactions">transactions</see>: events of local users, and joins of remote users that this server accepted as the room's server; and device changes and presence of local users as <c>m.device_list_update</c> and <c>m.presence</c> EDUs.</summary>
/// <remarks>Follows the <see cref="ServerStream"/> like <see cref="Accounts.PushNotifier"/> and hands what it finds to the <see cref="FederationQueue"/>, which batches and retries. Its position is kept in <see cref="FederationProgressRow"/>, so after a restart it continues where it stopped; on the first start it begins with new events.</remarks>
public sealed class FederationSender : BackgroundService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly FederationQueue queue;
    private readonly PresenceService presence;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<FederationSender> logger;

    /// <summary>Create the sender.</summary>
    public FederationSender(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, FederationQueue queue, PresenceService presence, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, TimeProvider time, ILogger<FederationSender> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.queue = queue;
        this.presence = presence;
        this.server = server;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Follow the stream until the server stops, sending the events of each new position.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // What waited at the last stop, then from where the last run got, or from now on the first time
        if (options.CurrentValue.Enabled) await queue.ResumeAsync(stoppingToken);
        long position = await LoadProgressAsync(stoppingToken) ?? await stream.GetCurrentAsync(stoppingToken);
        logger.LogDebug("Sending to other servers from stream position {Position}", position);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long next = await stream.WaitPastAsync(position, TimeSpan.FromSeconds(30), stoppingToken);
                if (next <= position) continue;
                if (options.CurrentValue.Enabled) await SendRangeAsync(position, next, stoppingToken);
                position = next;
                await SaveProgressAsync(position, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep going; one bad event should not end sending
                logger.LogWarning(ex, "Sending events to other servers failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    /// <summary>Send the events after <paramref name="from"/> up to <paramref name="until"/>.</summary>
    private async Task SendRangeAsync(long from, long until, CancellationToken cancellationToken)
    {
        // Events of the range in order
        string serverName = server.CurrentValue.ServerName;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<EventRow> events = await db.Events.AsNoTracking().Where(e => e.Stream > from && e.Stream <= until).OrderBy(e => e.Stream).ToListAsync(cancellationToken);
        foreach (EventRow row in events)
        {
            // Own events, and remote joins that only this server has seen so far
            string senderServer = UserId.TryParse(row.Sender, out UserId sender) ? sender.ServerName : "";
            if (senderServer != serverName && row.Type != EventTypes.RoomMember) continue;

            // Every other server with joined members, and the target of a membership change
            List<string> members = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == row.RoomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).ToListAsync(cancellationToken);
            if (row.StateKey is string target && row.Type == EventTypes.RoomMember) members.Add(target);
            HashSet<string> destinations = [.. members.Select(m => UserId.TryParse(m, out UserId member) ? member.ServerName : null).OfType<string>()];
            destinations.Remove(serverName);
            destinations.Remove(senderServer);
            if (destinations.Count == 0) continue;

            // One transaction per server, named by stream position
            JsonObject pdu = JsonNode.Parse(row.Json)!.AsObject();
            foreach (string destination in destinations) queue.EnqueuePdu(destination, pdu);
        }

        // Device changes of local users to the servers that share a room with them; without a history the other server fetches the whole list
        List<DeviceListChangeRow> changes = await db.DeviceListChanges.AsNoTracking().Where(c => c.Stream > from && c.Stream <= until).ToListAsync(cancellationToken);
        foreach (DeviceListChangeRow change in changes.Where(c => UserId.TryParse(c.UserId, out UserId user) && user.ServerName == serverName))
        {
            IReadOnlyCollection<string> destinations = await SharingServersAsync(db, change.UserId, serverName, cancellationToken);
            string deviceId = await db.Devices.AsNoTracking().Where(d => d.UserId == change.UserId).Select(d => d.DeviceId).FirstOrDefaultAsync(cancellationToken) ?? "";
            JsonObject edu = new() { ["edu_type"] = "m.device_list_update", ["content"] = new JsonObject { ["user_id"] = change.UserId, ["device_id"] = deviceId, ["stream_id"] = change.Stream, ["prev_id"] = new JsonArray() } };
            foreach (string destination in destinations) queue.EnqueueEdu(destination, edu);
        }

        // Presence of local users, one m.presence EDU per server with everyone it may see
        Dictionary<string, JsonArray> pushes = [];
        foreach ((string userId, JsonObject content) in presence.GetLocalChanges(from, until))
        {
            JsonObject push = new() { ["user_id"] = userId };
            foreach ((string key, JsonNode? value) in content) push[key] = value?.DeepClone();
            foreach (string destination in await SharingServersAsync(db, userId, serverName, cancellationToken)) (pushes.TryGetValue(destination, out JsonArray? list) ? list : pushes[destination] = []).Add(push.DeepClone());
        }
        foreach ((string destination, JsonArray push) in pushes) queue.EnqueueEdu(destination, new JsonObject { ["edu_type"] = "m.presence", ["content"] = new JsonObject { ["push"] = push } });
    }

    /// <summary>Return the last position handled before a restart, or null on the first start.</summary>
    private async Task<long?> LoadProgressAsync(CancellationToken cancellationToken)
    {
        // The one row
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return position
        return (await db.FederationProgress.AsNoTracking().SingleOrDefaultAsync(p => p.Id == 1, cancellationToken))?.Stream;
    }

    /// <summary>Remember the last position handled.</summary>
    private async Task SaveProgressAsync(long position, CancellationToken cancellationToken)
    {
        // Upsert the one row
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        FederationProgressRow? row = await db.FederationProgress.SingleOrDefaultAsync(p => p.Id == 1, cancellationToken);
        if (row is null) db.FederationProgress.Add(new FederationProgressRow { Id = 1, Stream = position });
        else row.Stream = position;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return the other servers with members joined to a room the user is joined to.</summary>
    private static async Task<IReadOnlyCollection<string>> SharingServersAsync(ServerDbContext db, string userId, string serverName, CancellationToken cancellationToken)
    {
        // Rooms of the user, then their members' servers
        List<string> rooms = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
        List<string> members = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && rooms.Contains(s.RoomId)).Select(s => s.StateKey).Distinct().ToListAsync(cancellationToken);
        HashSet<string> servers = [.. members.Select(m => UserId.TryParse(m, out UserId member) ? member.ServerName : null).OfType<string>()];
        servers.Remove(serverName);
        // Return servers
        return servers;
    }
}
