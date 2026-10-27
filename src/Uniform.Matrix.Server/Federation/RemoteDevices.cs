namespace Uniform.Matrix.Server.Federation;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>End-to-end encryption with other servers' users: their device and one-time keys from their servers, and to-device messages to them, such as room keys, as <c>m.direct_to_device</c> EDUs.</summary>
/// <remarks>Keys of remote users are asked for on each query and not cached. A server that cannot be reached is listed in <c>failures</c>, as the spec says.</remarks>
public sealed class RemoteDevices
{
    private readonly DeviceKeyService keys;
    private readonly FederationClient federation;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<RemoteDevices> logger;

    /// <summary>Create the service.</summary>
    public RemoteDevices(DeviceKeyService keys, FederationClient federation, IDbContextFactory<ServerDbContext> contexts, ServerStream stream, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, TimeProvider time, ILogger<RemoteDevices> logger)
    {
        // Store dependencies
        this.keys = keys;
        this.federation = federation;
        this.contexts = contexts;
        this.stream = stream;
        this.server = server;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Return the keys of the queried users, local ones from here and others from their servers, as <c>/keys/query</c> answers.</summary>
    public async Task<JsonObject> QueryAsync(string requesterId, JsonObject query, CancellationToken cancellationToken)
    {
        // Local users here
        (JsonObject local, Dictionary<string, JsonObject> remote) = Split(query);
        JsonObject result = await keys.QueryAsync(requesterId, local, cancellationToken);

        // Each other server once, merged in
        JsonObject failures = result["failures"]!.AsObject();
        foreach ((string serverName, JsonObject users) in remote)
        {
            try
            {
                JsonNode? answer = await federation.SendAsync(HttpMethod.Post, serverName, "/_matrix/federation/v1/user/keys/query", new JsonObject { ["device_keys"] = users }, cancellationToken);
                foreach (string field in new[] { "device_keys", "master_keys", "self_signing_keys" })
                {
                    foreach ((string userId, JsonNode? value) in answer?[field] as JsonObject ?? [])
                    {
                        if (users.ContainsKey(userId)) result[field]![userId] = value?.DeepClone();
                    }
                }
            }
            catch (MatrixException ex)
            {
                failures[serverName] = new JsonObject { ["errcode"] = ex.Errcode, ["error"] = ex.Error };
            }
        }
        // Return keys
        return result;
    }

    /// <summary>Claim one-time keys, local ones here and others from their servers, as <c>/keys/claim</c> answers.</summary>
    public async Task<JsonObject> ClaimAsync(JsonObject request, CancellationToken cancellationToken)
    {
        // Local users here
        (JsonObject local, Dictionary<string, JsonObject> remote) = Split(request);
        JsonObject result = await keys.ClaimAsync(local, cancellationToken);

        // Each other server once
        JsonObject failures = result["failures"]!.AsObject();
        foreach ((string serverName, JsonObject users) in remote)
        {
            try
            {
                JsonNode? answer = await federation.SendAsync(HttpMethod.Post, serverName, "/_matrix/federation/v1/user/keys/claim", new JsonObject { ["one_time_keys"] = users }, cancellationToken);
                foreach ((string userId, JsonNode? value) in answer?["one_time_keys"] as JsonObject ?? [])
                {
                    if (users.ContainsKey(userId)) result["one_time_keys"]![userId] = value?.DeepClone();
                }
            }
            catch (MatrixException ex)
            {
                failures[serverName] = new JsonObject { ["errcode"] = ex.Errcode, ["error"] = ex.Error };
            }
        }
        // Return keys
        return result;
    }

    /// <summary>Send the remote users' part of a to-device request to their servers, one EDU per server; failures are logged, as the client cannot retry them.</summary>
    public async Task SendToDeviceAsync(string sender, string type, string messageId, JsonObject messages, CancellationToken cancellationToken)
    {
        // Remote users by server
        (_, Dictionary<string, JsonObject> remote) = Split(messages);
        if (remote.Count == 0 || !options.CurrentValue.Enabled) return;
        foreach ((string serverName, JsonObject users) in remote)
        {
            JsonObject edu = new() { ["edu_type"] = "m.direct_to_device", ["content"] = new JsonObject { ["sender"] = sender, ["type"] = type, ["message_id"] = messageId, ["messages"] = users } };
            await SendEdusAsync(serverName, [edu], cancellationToken);
        }
    }

    /// <summary>Queue the to-device messages of an <c>m.direct_to_device</c> EDU from <paramref name="origin"/> for local devices.</summary>
    public async Task ReceiveToDeviceAsync(string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert a sender of the origin
        if (!UserId.TryParse(content["sender"]?.GetValue<string>(), out UserId sender) || sender.ServerName != origin || content["type"]?.GetValue<string>() is not string type) return;
        string serverName = server.CurrentValue.ServerName;

        // All messages at one position
        await stream.WriteAsync(async position =>
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            foreach ((string userId, JsonNode? devicesNode) in content["messages"] as JsonObject ?? [])
            {
                if (!UserId.TryParse(userId, out UserId target) || target.ServerName != serverName) continue;
                List<string> userDevices = await db.Devices.AsNoTracking().Where(d => d.UserId == userId).Select(d => d.DeviceId).ToListAsync(cancellationToken);
                foreach ((string deviceId, JsonNode? message) in devicesNode as JsonObject ?? [])
                {
                    if (message is not JsonObject) continue;
                    foreach (string device in deviceId == "*" ? userDevices : userDevices.Where(d => d == deviceId)) db.ToDevice.Add(new ToDeviceRow { UserId = userId, DeviceId = device, Sender = sender.ToString(), Type = type, Json = message.ToJsonString(), Stream = position });
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Note an <c>m.device_list_update</c> EDU: the remote user's devices changed, so local users sharing a room with them are told in sync.</summary>
    public async Task ReceiveDeviceListUpdateAsync(string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert a user of the origin
        if (!UserId.TryParse(content["user_id"]?.GetValue<string>(), out UserId user) || user.ServerName != origin) return;
        // Done
        await keys.MarkChangedAsync([user.ToString()], cancellationToken);
    }

    /// <summary>Return the devices of a local user as <c>/_matrix/federation/v1/user/devices</c> answers, for other servers that resync after a change.</summary>
    public async Task<JsonObject> GetUserDevicesAsync(string userId, CancellationToken cancellationToken)
    {
        // The same keys a query gives
        JsonObject found = await keys.QueryAsync("", new JsonObject { [userId] = new JsonArray() }, cancellationToken);
        JsonArray devices = [.. (found["device_keys"]?[userId] as JsonObject ?? []).Select(d => (JsonNode)new JsonObject { ["device_id"] = d.Key, ["keys"] = d.Value?.DeepClone(), ["device_display_name"] = d.Value?["unsigned"]?["device_display_name"]?.DeepClone() })];
        JsonObject answer = new() { ["user_id"] = userId, ["stream_id"] = await stream.GetCurrentAsync(cancellationToken), ["devices"] = devices };
        if (found["master_keys"]?[userId] is JsonNode master) answer["master_key"] = master.DeepClone();
        if (found["self_signing_keys"]?[userId] is JsonNode selfSigning) answer["self_signing_key"] = selfSigning.DeepClone();
        // Return devices
        return answer;
    }

    /// <summary>Send EDUs to a server in a transaction of their own; log failures.</summary>
    public async Task SendEdusAsync(string destination, IReadOnlyList<JsonObject> edus, CancellationToken cancellationToken)
    {
        // A transaction with EDUs only
        string origin = server.CurrentValue.ServerName;
        JsonObject transaction = new() { ["origin"] = origin, ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["pdus"] = new JsonArray(), ["edus"] = new JsonArray([.. edus.Select(e => (JsonNode)e.DeepClone())]) };
        try
        {
            await federation.SendAsync(HttpMethod.Put, destination, $"/_matrix/federation/v1/send/edu{time.GetUtcNow().ToUnixTimeMilliseconds()}{Random.Shared.Next(1000)}", transaction, cancellationToken);
        }
        catch (MatrixException ex)
        {
            // On error log
            logger.LogInformation("Sending EDUs to {Destination} failed: {Error}", destination, ex.Message);
        }
    }

    /// <summary>Split a request keyed by user ID into the local users and the others by server.</summary>
    private (JsonObject Local, Dictionary<string, JsonObject> Remote) Split(JsonObject byUser)
    {
        // By the server part of each user ID
        string serverName = server.CurrentValue.ServerName;
        JsonObject local = [];
        Dictionary<string, JsonObject> remote = [];
        foreach ((string userId, JsonNode? value) in byUser)
        {
            if (!UserId.TryParse(userId, out UserId user)) continue;
            if (user.ServerName == serverName) local[userId] = value?.DeepClone();
            else if (options.CurrentValue.Enabled) (remote.TryGetValue(user.ServerName, out JsonObject? users) ? users : remote[user.ServerName] = [])[userId] = value?.DeepClone();
        }
        // Return parts
        return (local, remote);
    }
}
