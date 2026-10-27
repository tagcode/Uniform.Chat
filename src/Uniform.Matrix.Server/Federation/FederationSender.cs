namespace Uniform.Matrix.Server.Federation;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>Sends new room events to the other servers in each room as <see href="https://spec.matrix.org/latest/server-server-api/#transactions">transactions</see>: events of local users, and joins of remote users that this server accepted as the room's server.</summary>
/// <remarks>Follows the <see cref="ServerStream"/> like <see cref="Accounts.PushNotifier"/>; one transaction per event and server, without retries yet, so a server that is down misses the event until backfill exists. Events from before the server started are not sent.</remarks>
public sealed class FederationSender : BackgroundService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly FederationClient federation;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<FederationSender> logger;

    /// <summary>Create the sender.</summary>
    public FederationSender(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, FederationClient federation, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, TimeProvider time, ILogger<FederationSender> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.federation = federation;
        this.server = server;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Follow the stream until the server stops, sending the events of each new position.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // From now on
        long position = await stream.GetCurrentAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long next = await stream.WaitPastAsync(position, TimeSpan.FromSeconds(30), stoppingToken);
                if (next <= position) continue;
                if (options.CurrentValue.Enabled) await SendRangeAsync(position, next, stoppingToken);
                position = next;
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
            foreach (string destination in destinations) await SendAsync(destination, $"{row.Stream}", pdu, cancellationToken);
        }
    }

    /// <summary>Send one PDU to a server; log failures.</summary>
    private async Task SendAsync(string destination, string transactionId, JsonObject pdu, CancellationToken cancellationToken)
    {
        // The transaction
        JsonObject transaction = new() { ["origin"] = server.CurrentValue.ServerName, ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["pdus"] = new JsonArray(pdu.DeepClone()) };
        try
        {
            await federation.SendAsync(HttpMethod.Put, destination, $"/_matrix/federation/v1/send/{Uri.EscapeDataString(transactionId)}", transaction, cancellationToken);
        }
        catch (MatrixException ex)
        {
            // On error log and go on
            logger.LogInformation("Sending to {Destination} failed: {Error}", destination, ex.Message);
        }
    }
}
