namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Rooms;

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#joining-rooms">Joining rooms</see> of other servers: <c>make_join</c> for a template, sign it, <c>send_join</c>, check the returned state and store the room.</summary>
/// <remarks>Received events are checked for their content hash and their sender server's signature. Auth chains are not yet run through the auth rules, and later events need <c>/send</c>, which is not there yet.</remarks>
public sealed class RemoteJoins
{
    private readonly FederationClient federation;
    private readonly RemoteServerKeys remoteKeys;
    private readonly ServerKeys keys;
    private readonly EventSender sender;
    private readonly IOptionsMonitor<ServerOptions> server;

    /// <summary>Create the service.</summary>
    public RemoteJoins(FederationClient federation, RemoteServerKeys remoteKeys, ServerKeys keys, EventSender sender, IOptionsMonitor<ServerOptions> server)
    {
        // Store dependencies
        this.federation = federation;
        this.remoteKeys = remoteKeys;
        this.keys = keys;
        this.sender = sender;
        this.server = server;
    }

    /// <summary>Join local user <paramref name="userId"/> to <paramref name="roomId"/> through <paramref name="resident"/>, a server in the room.</summary>
    /// <param name="userId">Local user.</param>
    /// <param name="roomId">Room on another server.</param>
    /// <param name="resident">Server to join through, e.g. the one in the room ID.</param>
    /// <param name="content">Member content, e.g. with the display name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task JoinAsync(string userId, string roomId, string resident, JsonObject content, CancellationToken cancellationToken)
    {
        // Template in a room version we support
        string versions = string.Join("&", RoomVersion.Supported.Keys.Select(v => $"ver={v}"));
        JsonNode? made = await federation.SendAsync(HttpMethod.Get, resident, $"/_matrix/federation/v1/make_join/{Uri.EscapeDataString(roomId)}/{Uri.EscapeDataString(userId)}?{versions}", null, cancellationToken);
        RoomVersion version = RoomVersion.Find(made?["room_version"]?.GetValue<string>() ?? "1") ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UnsupportedRoomVersion, "The room's version is not supported here.");
        if (made?["event"] is not JsonObject template || template["room_id"]?.GetValue<string>() != roomId || template["sender"]?.GetValue<string>() != userId || template["state_key"]?.GetValue<string>() != userId) throw Bad(resident, "a wrong join template");

        // Our join: the template with our content, hashed and signed
        template["content"] = content.DeepClone();
        template["content"]!["membership"] = Memberships.Join;
        template.Remove("hashes");
        template.Remove("signatures");
        template.Remove("unsigned");
        EventFormat.HashAndSign(template, version, server.CurrentValue.ServerName, keys.KeyId, keys.Secret);
        string eventId = EventFormat.ComputeEventId(template, version);

        // The resident server accepts it and sends the room's state
        JsonNode? joined = await federation.SendAsync(HttpMethod.Put, resident, $"/_matrix/federation/v2/send_join/{Uri.EscapeDataString(roomId)}/{Uri.EscapeDataString(eventId)}", template, cancellationToken);
        Dictionary<string, JsonObject> events = [];
        List<string> stateIds = [];
        foreach ((JsonNode? node, bool isState) in (joined?["state"] as JsonArray ?? []).Select(n => (n, true)).Concat((joined?["auth_chain"] as JsonArray ?? []).Select(n => (n, false))))
        {
            if (node is not JsonObject pdu) throw Bad(resident, "an event that is not an object");
            string id = EventFormat.ComputeEventId(pdu, version);
            bool isCreate = version.RoomIdIsCreateHash && pdu["type"]?.GetValue<string>() == EventTypes.RoomCreate && !pdu.ContainsKey("room_id") && RoomVersion.RoomIdOf(id) == roomId;
            if (!isCreate && pdu["room_id"]?.GetValue<string>() != roomId) throw Bad(resident, "an event of another room");
            if (!await IsSignedAsync(pdu, version, cancellationToken)) throw Bad(resident, $"event {id} without a valid signature or hash");
            events[id] = pdu;
            if (isState && pdu["state_key"] is not null) stateIds.Add(id);
        }
        // Done
        await sender.StoreJoinedRoomAsync(version, template, eventId, events, stateIds, cancellationToken);
    }

    /// <summary>Return whether a PDU's content hash is valid and its sender's server signed it.</summary>
    private async Task<bool> IsSignedAsync(JsonObject pdu, RoomVersion version, CancellationToken cancellationToken)
    {
        // Assert hash, then any valid key of the sender's server
        if (!EventFormat.HasValidContentHash(pdu) || !UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId senderId)) return false;
        foreach (string keyId in (pdu["signatures"]?[senderId.ServerName] as JsonObject ?? []).Select(k => k.Key))
        {
            if (await remoteKeys.GetVerifyKeyAsync(senderId.ServerName, keyId, cancellationToken) is string key && EventFormat.HasValidSignature(pdu, version, senderId.ServerName, keyId, key)) return true;
        }
        // On error not signed
        return false;
    }

    /// <summary>Return an error about a resident server's answer.</summary>
    private static MatrixException Bad(string resident, string what) => new(HttpStatusCode.BadGateway, ErrorCodes.Unknown, $"{resident} sent {what}.");
}
