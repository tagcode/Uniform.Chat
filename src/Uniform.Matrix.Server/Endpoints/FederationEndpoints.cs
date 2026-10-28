namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Protocol.Server;

/// <summary>The <see href="https://spec.matrix.org/latest/server-server-api/">server-server API</see> that other servers call: this server's keys and version, and queries.</summary>
public static class FederationEndpoints
{
    /// <summary>Software name in <c>/_matrix/federation/v1/version</c>.</summary>
    public const string SoftwareName = "Uniform.Matrix";

    /// <summary>Map <c>/_matrix/key/v2</c> and <c>/_matrix/federation/v1</c>.</summary>
    public static IEndpointRouteBuilder MapFederation(this IEndpointRouteBuilder routes)
    {
        // Keys and version, unsigned
        routes.MapGet("/_matrix/key/v2/server", (ServerKeys keys, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> federation, TimeProvider time) =>
            MatrixHttp.Json(KeyResponse(keys, server.CurrentValue.ServerName, time.GetUtcNow() + federation.CurrentValue.KeyValidity))).RequireFederation(signed: false);
        routes.MapGet("/_matrix/federation/v1/version", () =>
            MatrixHttp.Json(new JsonObject { ["server"] = new JsonObject { ["name"] = SoftwareName, ["version"] = typeof(FederationEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" } })).RequireFederation(signed: false);

        // Key server: keys of this and other servers, each answer signed by this server too
        routes.MapPost("/_matrix/key/v2/query", async (JsonObject body, ServerKeys keys, RemoteServerKeys remote, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> federation, TimeProvider time, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["server_keys"] = await NotarizeAsync([.. (body["server_keys"] as JsonObject ?? []).Select(s => s.Key).Take(100)], keys, remote, server.CurrentValue.ServerName, time.GetUtcNow() + federation.CurrentValue.KeyValidity, cancellationToken) })).RequireFederation(signed: false);
        routes.MapGet("/_matrix/key/v2/query/{serverName}", async (string serverName, ServerKeys keys, RemoteServerKeys remote, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> federation, TimeProvider time, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["server_keys"] = await NotarizeAsync([serverName], keys, remote, server.CurrentValue.ServerName, time.GetUtcNow() + federation.CurrentValue.KeyValidity, cancellationToken) })).RequireFederation(signed: false);

        // Queries of signed servers
        RouteGroupBuilder federation = routes.MapGroup("/_matrix/federation/v1").RequireFederation();
        federation.MapGet("/query/profile", async (string user_id, string? field, IOptionsMonitor<ServerOptions> server, ProfileService profiles, CancellationToken cancellationToken) =>
        {
            if (!UserId.TryParse(user_id, out UserId userId) || userId.ServerName != server.CurrentValue.ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Not a user of this server.");
            return MatrixHttp.Json(field is null ? await profiles.GetProfileAsync(user_id, cancellationToken) : await profiles.GetFieldAsync(user_id, field, cancellationToken));
        });

        // Spaces here, for other servers' previews
        federation.MapGet("/hierarchy/{roomId}", async (string roomId, bool? suggested_only, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await directory.GetFederationHierarchyAsync(http.GetOrigin(), roomId, suggested_only == true, cancellationToken)));

        // Local aliases for other servers
        federation.MapGet("/query/directory", async (string room_alias, DirectoryService directory, CancellationToken cancellationToken) =>
        {
            (string roomId, IReadOnlyList<string> servers) = await directory.GetLocalAliasAsync(room_alias, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["room_id"] = roomId, ["servers"] = new JsonArray([.. servers.Select(s => (JsonNode)s)]) });
        });

        // Public room directory for other servers' users
        federation.MapGet("/publicRooms", async (int? limit, string? since, DirectoryService directory, IOptionsMonitor<ServerOptions> server, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await directory.GetPublicRoomsAsync(limit, since, null, server.CurrentValue.ServerName, cancellationToken)));
        federation.MapPost("/publicRooms", async (JsonObject body, DirectoryService directory, IOptionsMonitor<ServerOptions> server, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await directory.GetPublicRoomsAsync((int?)body["limit"]?.GetValue<long>(), body["since"]?.GetValue<string>(), body["filter"]?["generic_search_term"]?.GetValue<string>(), server.CurrentValue.ServerName, cancellationToken)));

        // Joins and leaves of other servers' users, such as rejected invites
        federation.MapGet("/make_join/{roomId}/{userId}", async (string roomId, string userId, HttpContext http, EventSender events, RoomAcls acls, CancellationToken cancellationToken) =>
        {
            await acls.AssertAllowedAsync(http.GetOrigin(), roomId, cancellationToken);
            (JsonObject template, RoomVersion version) = await events.MakeMembershipAsync(roomId, AssertOriginUser(http, userId), Memberships.Join, cancellationToken);
            if (!http.Request.Query["ver"].Contains(version.Id)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.IncompatibleRoomVersion, $"The room has version {version.Id}, which the asking server does not support.");
            return MatrixHttp.Json(new JsonObject { ["event"] = template, ["room_version"] = version.Id });
        });
        federation.MapGet("/make_leave/{roomId}/{userId}", async (string roomId, string userId, HttpContext http, EventSender events, RoomAcls acls, CancellationToken cancellationToken) =>
        {
            await acls.AssertAllowedAsync(http.GetOrigin(), roomId, cancellationToken);
            (JsonObject template, RoomVersion version) = await events.MakeMembershipAsync(roomId, AssertOriginUser(http, userId), Memberships.Leave, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["event"] = template, ["room_version"] = version.Id });
        });
        routes.MapPut("/_matrix/federation/v2/send_join/{roomId}/{eventId}", async (string roomId, string eventId, JsonObject pdu, HttpContext http, EventSender events, RemoteServerKeys keys, RoomAcls acls, IOptionsMonitor<ServerOptions> server, CancellationToken cancellationToken) =>
        {
            await acls.AssertAllowedAsync(http.GetOrigin(), roomId, cancellationToken);
            (List<JsonObject> state, List<JsonObject> authChain) = await AcceptAsync(http, roomId, eventId, pdu, Memberships.Join, events, keys, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["origin"] = server.CurrentValue.ServerName, ["members_omitted"] = false, ["state"] = new JsonArray([.. state]), ["auth_chain"] = new JsonArray([.. authChain]) });
        }).RequireFederation();
        routes.MapPut("/_matrix/federation/v2/send_leave/{roomId}/{eventId}", async (string roomId, string eventId, JsonObject pdu, HttpContext http, EventSender events, RemoteServerKeys keys, RoomAcls acls, CancellationToken cancellationToken) =>
        {
            await acls.AssertAllowedAsync(http.GetOrigin(), roomId, cancellationToken);
            await AcceptAsync(http, roomId, eventId, pdu, Memberships.Leave, events, keys, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        }).RequireFederation();

        // Invites of this server's users to rooms elsewhere
        routes.MapPut("/_matrix/federation/v2/invite/{roomId}/{eventId}", async (string roomId, string eventId, JsonObject body, HttpContext http, RemoteInvites invites, RoomAcls acls, CancellationToken cancellationToken) =>
        {
            await acls.AssertAllowedAsync(http.GetOrigin(), roomId, cancellationToken);
            return MatrixHttp.Json(await invites.ReceiveAsync(http.GetOrigin(), roomId, eventId, body, cancellationToken));
        }).RequireFederation();

        // History for servers filling gaps: missing events, backfill, single events and state at an event
        federation.MapPost("/get_missing_events/{roomId}", async (string roomId, JsonObject body, HttpContext http, RoomHistory history, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject
            {
                ["events"] = await history.GetMissingEventsAsync(http.GetOrigin(), roomId, Ids(body["earliest_events"]), Ids(body["latest_events"]), (int)(body["limit"]?.GetValue<long>() ?? 10), body["min_depth"]?.GetValue<long>() ?? 0, cancellationToken),
            }));
        federation.MapGet("/backfill/{roomId}", async (string roomId, int? limit, HttpContext http, RoomHistory history, IOptionsMonitor<ServerOptions> server, TimeProvider time, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject
            {
                ["origin"] = server.CurrentValue.ServerName,
                ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
                ["pdus"] = await history.BackfillAsync(http.GetOrigin(), roomId, [.. http.Request.Query["v"].OfType<string>()], limit ?? 10, cancellationToken),
            }));
        federation.MapGet("/event/{eventId}", async (string eventId, HttpContext http, RoomHistory history, IOptionsMonitor<ServerOptions> server, TimeProvider time, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["origin"] = server.CurrentValue.ServerName, ["origin_server_ts"] = time.GetUtcNow().ToUnixTimeMilliseconds(), ["pdus"] = new JsonArray(await history.GetEventAsync(http.GetOrigin(), eventId, cancellationToken)) }));
        federation.MapGet("/state_ids/{roomId}", async (string roomId, string event_id, HttpContext http, RoomHistory history, CancellationToken cancellationToken) =>
        {
            (_, _, List<string> stateIds, List<string> chainIds) = await history.GetStateAsync(http.GetOrigin(), roomId, event_id, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["pdu_ids"] = new JsonArray([.. stateIds.Select(i => (JsonNode)i)]), ["auth_chain_ids"] = new JsonArray([.. chainIds.Select(i => (JsonNode)i)]) });
        });
        federation.MapGet("/state/{roomId}", async (string roomId, string event_id, HttpContext http, RoomHistory history, CancellationToken cancellationToken) =>
        {
            (List<JsonObject> state, List<JsonObject> chain, _, _) = await history.GetStateAsync(http.GetOrigin(), roomId, event_id, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["pdus"] = new JsonArray([.. state]), ["auth_chain"] = new JsonArray([.. chain]) });
        });

        // Keys and devices of this server's users, for encrypting to them
        federation.MapPost("/user/keys/query", async (JsonObject body, IOptionsMonitor<ServerOptions> server, DeviceKeyService keys, CancellationToken cancellationToken) =>
        {
            JsonObject result = await keys.QueryAsync("", LocalUsers(body["device_keys"] as JsonObject, server.CurrentValue.ServerName), cancellationToken);
            result.Remove("user_signing_keys");
            return MatrixHttp.Json(result);
        });
        federation.MapPost("/user/keys/claim", async (JsonObject body, IOptionsMonitor<ServerOptions> server, DeviceKeyService keys, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await keys.ClaimAsync(LocalUsers(body["one_time_keys"] as JsonObject, server.CurrentValue.ServerName), cancellationToken)));
        federation.MapGet("/user/devices/{userId}", async (string userId, IOptionsMonitor<ServerOptions> server, RemoteDevices devices, CancellationToken cancellationToken) =>
            UserId.TryParse(userId, out UserId user) && user.ServerName == server.CurrentValue.ServerName ? MatrixHttp.Json(await devices.GetUserDevicesAsync(userId, cancellationToken)) : MatrixHttp.Error(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Not a user of this server."));

        // Transactions of events, and EDUs: to-device messages, device list changes, typing, receipts and presence
        federation.MapPut("/send/{txnId}", async (string txnId, JsonObject body, HttpContext http, RemoteEvents events, RemoteDevices devices, RemoteActivity activity, RoomAcls acls, CancellationToken cancellationToken) =>
        {
            // Typing and receipts of rooms whose ACL denies the origin are dropped, as are its events there
            string origin = http.GetOrigin();
            foreach (JsonObject edu in (body["edus"] as JsonArray ?? []).OfType<JsonObject>().Take(100))
            {
                if (edu["content"] is not JsonObject content) continue;
                switch (edu["edu_type"]?.GetValue<string>())
                {
                    case "m.direct_to_device": await devices.ReceiveToDeviceAsync(origin, content, cancellationToken); break;
                    case "m.device_list_update": await devices.ReceiveDeviceListUpdateAsync(origin, content, cancellationToken); break;
                    case "m.typing" when await acls.IsAllowedAsync(origin, content["room_id"]?.GetValue<string>(), cancellationToken): await activity.ReceiveTypingAsync(origin, content, cancellationToken); break;
                    case "m.receipt": await activity.ReceiveReceiptsAsync(origin, await AllowedRoomsAsync(acls, origin, content, cancellationToken), cancellationToken); break;
                    case "m.presence": await activity.ReceivePresenceAsync(origin, content, cancellationToken); break;
                }
            }
            JsonObject results = [];
            foreach (JsonObject pdu in (body["pdus"] as JsonArray ?? []).OfType<JsonObject>().Take(50))
            {
                if (!await acls.IsAllowedAsync(origin, pdu["room_id"]?.GetValue<string>(), cancellationToken)) continue;
                (string? eventId, string? error) = await events.AcceptAsync(origin, pdu, cancellationToken);
                if (eventId is not null) results[eventId] = error is null ? new JsonObject() : new JsonObject { ["error"] = error };
            }
            return MatrixHttp.Json(new JsonObject { ["pdus"] = results });
        });
        // Return routes
        return routes;
    }

    /// <summary>Return the rooms of an <c>m.receipt</c> EDU whose ACL allows <paramref name="origin"/>.</summary>
    private static async Task<JsonObject> AllowedRoomsAsync(RoomAcls acls, string origin, JsonObject content, CancellationToken cancellationToken)
    {
        // Each room separately
        JsonObject allowed = [];
        foreach ((string roomId, JsonNode? receipts) in content)
        {
            if (await acls.IsAllowedAsync(origin, roomId, cancellationToken)) allowed[roomId] = receipts?.DeepClone();
        }
        // Return allowed rooms
        return allowed;
    }

    /// <summary>Return <paramref name="userId"/> when it is a user of the asking server; throw <c>M_FORBIDDEN</c> otherwise.</summary>
    private static string AssertOriginUser(HttpContext http, string userId) =>
        UserId.TryParse(userId, out UserId user) && user.ServerName == http.GetOrigin() ? userId : throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "The user is not of the asking server.");

    /// <summary>Accept a signed join or leave of the asking server's user, with the origin's keys it was signed with.</summary>
    private static async Task<(List<JsonObject> State, List<JsonObject> AuthChain)> AcceptAsync(HttpContext http, string roomId, string eventId, JsonObject pdu, string membership, EventSender events, RemoteServerKeys keys, CancellationToken cancellationToken)
    {
        // Assert the room, then the keys it names
        if (pdu["room_id"]?.GetValue<string>() != roomId) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The event is of another room.");
        string origin = http.GetOrigin();
        Dictionary<string, string> originKeys = [];
        foreach (string keyId in (pdu["signatures"]?[origin] as JsonObject ?? []).Select(k => k.Key))
        {
            if (await keys.GetVerifyKeyAsync(origin, keyId, cancellationToken) is string key) originKeys[keyId] = key;
        }
        // Return what the endpoint answers
        return await events.AcceptMembershipAsync(pdu, membership, eventId, origin, originKeys, cancellationToken);
    }

    /// <summary>Return the strings of a JSON array of event IDs.</summary>
    private static IReadOnlyCollection<string> Ids(JsonNode? array) => [.. (array as JsonArray ?? []).Select(i => i?.GetValue<string>()).OfType<string>()];

    /// <summary>Return the entries of <paramref name="byUser"/> for users of <paramref name="serverName"/>; other servers ask only about their own.</summary>
    private static JsonObject LocalUsers(JsonObject? byUser, string serverName) =>
        new([.. (byUser ?? []).Where(u => UserId.TryParse(u.Key, out UserId user) && user.ServerName == serverName).Select(u => KeyValuePair.Create(u.Key, u.Value?.DeepClone()))]);

    /// <summary>Return the key answers of <paramref name="serverNames"/>, this server's own and kept or fetched ones of others, each signed by this server too; servers whose keys cannot be had are left out.</summary>
    private static async Task<JsonArray> NotarizeAsync(IReadOnlyList<string> serverNames, ServerKeys keys, RemoteServerKeys remote, string serverName, DateTimeOffset validUntil, CancellationToken cancellationToken)
    {
        // Each server once
        JsonArray answers = [];
        foreach (string name in serverNames.Distinct())
        {
            if (!ServerNames.TryParse(name, out _, out _)) continue;
            JsonObject? response = name == serverName ? KeyResponse(keys, serverName, validUntil) : await remote.GetResponseAsync(name, cancellationToken);
            if (response is null) continue;
            if (name != serverName) CanonicalJson.AddSignature(response, serverName, keys.KeyId, Ed25519.Sign(keys.Secret, CanonicalJson.SigningForm(response)));
            answers.Add(response);
        }
        // Return answers
        return answers;
    }

    /// <summary>Return this server's signed key response.</summary>
    public static JsonObject KeyResponse(ServerKeys keys, string serverName, DateTimeOffset validUntil)
    {
        // Current key, no old ones yet
        JsonObject response = new()
        {
            ["server_name"] = serverName,
            ["verify_keys"] = new JsonObject { [keys.KeyId] = new JsonObject { ["key"] = keys.PublicKey } },
            ["old_verify_keys"] = new JsonObject(),
            ["valid_until_ts"] = validUntil.ToUnixTimeMilliseconds(),
        };
        CanonicalJson.AddSignature(response, serverName, keys.KeyId, Ed25519.Sign(keys.Secret, CanonicalJson.SigningForm(response)));
        // Return signed
        return response;
    }
}
