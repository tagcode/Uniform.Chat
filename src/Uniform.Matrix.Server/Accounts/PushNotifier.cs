namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>Sends <see href="https://spec.matrix.org/latest/push-gateway-api/">push notifications</see>: follows the <see cref="ServerStream"/>, runs the push rules of the room's members on each new event, and posts to the push gateways of their http pushers.</summary>
/// <remarks>Gateways are called outside every lock, one pusher at a time; a failing gateway is logged and skipped. Push keys a gateway rejects are deleted. Events from before the server started are not pushed.</remarks>
public sealed class PushNotifier : BackgroundService
{
    /// <summary>Name of the HTTP client that reaches gateways.</summary>
    public const string HttpClientName = "Uniform.Matrix.Server.Push";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly PushRuleService pushRules;
    private readonly PusherService pushers;
    private readonly IHttpClientFactory httpClients;
    private readonly IOptionsMonitor<PushOptions> options;
    private readonly ILogger<PushNotifier> logger;

    /// <summary>Create the notifier.</summary>
    public PushNotifier(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, PushRuleService pushRules, PusherService pushers, IHttpClientFactory httpClients, IOptionsMonitor<PushOptions> options, ILogger<PushNotifier> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.pushRules = pushRules;
        this.pushers = pushers;
        this.httpClients = httpClients;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Follow the stream until the server stops, pushing the events of each new position.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // From now on; older events were notified before, or before any pusher existed
        long position = await stream.GetCurrentAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long next = await stream.WaitPastAsync(position, TimeSpan.FromSeconds(30), stoppingToken);
                if (next <= position) continue;
                await PushRangeAsync(position, next, stoppingToken);
                position = next;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep going; a database or gateway hiccup should not end pushing
                logger.LogWarning(ex, "Pushing events failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    /// <summary>Push the events after <paramref name="from"/> up to <paramref name="until"/>.</summary>
    private async Task PushRangeAsync(long from, long until, CancellationToken cancellationToken)
    {
        // Nothing to do without pushers, which is the usual case for small servers
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Pushers.AnyAsync(p => p.Kind == "http", cancellationToken)) return;
        List<EventRow> events = await db.Events.AsNoTracking().Where(e => e.Stream > from && e.Stream <= until && e.RedactedBy == null).OrderBy(e => e.Stream).ToListAsync(cancellationToken);

        // Each event to each member with pushers, but not to its sender
        foreach (EventRow row in events)
        {
            if (JsonNode.Parse(row.Json) is not JsonObject clientEvent) continue;
            foreach (string userId in await RecipientsAsync(db, row, cancellationToken))
            {
                List<PusherRow> userPushers = await db.Pushers.AsNoTracking().Where(p => p.UserId == userId && p.Kind == "http").ToListAsync(cancellationToken);
                if (userPushers.Count == 0) continue;
                JsonObject ruleset = await pushRules.GetRulesetAsync(userId, cancellationToken);
                PushDecision decision = await PushRuleService.DecideAsync(db, ruleset, userId, row.RoomId, clientEvent, cancellationToken);
                if (!decision.Notify) continue;
                int unread = await PushRuleService.CountAllUnreadAsync(db, ruleset, userId, cancellationToken);
                JsonObject facts = await RoomFactsAsync(db, row, userId, cancellationToken);
                foreach (PusherRow pusher in userPushers) await SendAsync(pusher, clientEvent, decision, unread, facts, cancellationToken);
            }
        }
    }

    /// <summary>Return who an event may notify: the room's joined members, and the user an invite is for; never the sender.</summary>
    private static async Task<List<string>> RecipientsAsync(ServerDbContext db, EventRow row, CancellationToken cancellationToken)
    {
        // Joined members, plus the invitee
        List<string> users = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == row.RoomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && s.StateKey != row.Sender).Select(s => s.StateKey).ToListAsync(cancellationToken);
        if (row.Type == EventTypes.RoomMember && row.StateKey is string target && target != row.Sender && !users.Contains(target)) users.Add(target);
        // Return users
        return users;
    }

    /// <summary>Return the names a notification shows: the sender's display name, the room's name and alias, and whether the event is about the user.</summary>
    private static async Task<JsonObject> RoomFactsAsync(ServerDbContext db, EventRow row, string userId, CancellationToken cancellationToken)
    {
        // Current state of the room
        JsonObject facts = [];
        if (await StateAsync(db, row.RoomId, EventTypes.RoomMember, row.Sender, cancellationToken) is JsonObject sender && sender["displayname"]?.GetValue<string>() is string senderName) facts["sender_display_name"] = senderName;
        if (await StateAsync(db, row.RoomId, EventTypes.RoomName, "", cancellationToken) is JsonObject name && name["name"]?.GetValue<string>() is { Length: > 0 } roomName) facts["room_name"] = roomName;
        if (await StateAsync(db, row.RoomId, EventTypes.RoomCanonicalAlias, "", cancellationToken) is JsonObject alias && alias["alias"]?.GetValue<string>() is { Length: > 0 } roomAlias) facts["room_alias"] = roomAlias;
        if (row.Type == EventTypes.RoomMember) facts["user_is_target"] = row.StateKey == userId;
        // Return facts
        return facts;
    }

    /// <summary>Return the content of a current state event, or null.</summary>
    private static async Task<JsonObject?> StateAsync(ServerDbContext db, string roomId, string type, string stateKey, CancellationToken cancellationToken)
    {
        // State row, then its event
        string? eventId = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == type && s.StateKey == stateKey).Select(s => s.EventId).SingleOrDefaultAsync(cancellationToken);
        string? json = eventId is null ? null : await db.Events.AsNoTracking().Where(e => e.EventId == eventId).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        // Return content
        return json is null ? null : JsonNode.Parse(json)?["content"] as JsonObject;
    }

    /// <summary>Post one notification to a pusher's gateway; delete the pusher when the gateway rejects its key.</summary>
    private async Task SendAsync(PusherRow pusher, JsonObject clientEvent, PushDecision decision, int unread, JsonObject facts, CancellationToken cancellationToken)
    {
        // The gateway URL, and its data without it
        JsonObject data = JsonNode.Parse(pusher.DataJson) as JsonObject ?? [];
        string? url = data["url"]?.GetValue<string>();
        if (!PusherService.IsGatewayUrl(url)) return;
        Uri gateway = new(url!);
        bool eventIdOnly = data["format"]?.GetValue<string>() == "event_id_only";
        data.Remove("url");

        // The notification as the push gateway API has it
        JsonObject device = new() { ["app_id"] = pusher.AppId, ["pushkey"] = pusher.PushKey, ["pushkey_ts"] = pusher.CreatedTs / 1000, ["data"] = data, ["tweaks"] = new JsonObject([.. decision.Tweaks.Select(t => KeyValuePair.Create(t.Key, t.Value?.DeepClone()))]) };
        JsonObject notification = new()
        {
            ["event_id"] = clientEvent["event_id"]?.DeepClone(),
            ["room_id"] = clientEvent["room_id"]?.DeepClone(),
            ["counts"] = new JsonObject { ["unread"] = unread },
            ["prio"] = decision.Highlight || clientEvent["type"]?.GetValue<string>() is EventTypes.RoomMessage or EventTypes.RoomEncrypted ? "high" : "low",
            ["devices"] = new JsonArray(device),
        };
        if (!eventIdOnly)
        {
            notification["type"] = clientEvent["type"]?.DeepClone();
            notification["sender"] = clientEvent["sender"]?.DeepClone();
            notification["content"] = clientEvent["content"]?.DeepClone();
            foreach ((string key, JsonNode? value) in facts) notification[key] = value?.DeepClone();
        }

        // Post, then forget rejected keys
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.CurrentValue.GatewayTimeout);
            using HttpResponseMessage response = await httpClients.CreateClient(HttpClientName).PostAsJsonAsync(gateway, new JsonObject { ["notification"] = notification }, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Push gateway {Gateway} answered {Status} for {UserId}", gateway.Host, (int)response.StatusCode, pusher.UserId);
                return;
            }
            JsonObject? result = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            List<string> rejected = [.. (result?["rejected"] as JsonArray ?? []).Select(k => k?.GetValue<string>()).OfType<string>()];
            if (rejected.Count == 0) return;
            await pushers.DeleteAsync(pusher.UserId, pusher.AppId, rejected, cancellationToken);
            logger.LogInformation("Push gateway {Gateway} rejected {Count} push keys of {UserId}; deleted their pushers", gateway.Host, rejected.Count, pusher.UserId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Push gateway {Gateway} failed for {UserId}: {Error}", gateway.Host, pusher.UserId, ex.Message);
        }
    }

    /// <summary>Return the HTTP handler of the gateway client: it connects only to public addresses unless <see cref="PushOptions.AllowPrivateGateways"/> is on.</summary>
    public static SocketsHttpHandler CreateHandler(IOptionsMonitor<PushOptions> options) => PublicAddresses.CreateHandler(() => options.CurrentValue.AllowPrivateGateways, "Push:AllowPrivateGateways");
}
