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

        // Queries of signed servers
        RouteGroupBuilder federation = routes.MapGroup("/_matrix/federation/v1").RequireFederation();
        federation.MapGet("/query/profile", async (string user_id, string? field, IOptionsMonitor<ServerOptions> server, ProfileService profiles, CancellationToken cancellationToken) =>
        {
            if (!UserId.TryParse(user_id, out UserId userId) || userId.ServerName != server.CurrentValue.ServerName) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Not a user of this server.");
            return MatrixHttp.Json(field is null ? await profiles.GetProfileAsync(user_id, cancellationToken) : await profiles.GetFieldAsync(user_id, field, cancellationToken));
        });

        // Joins of other servers' users
        federation.MapGet("/make_join/{roomId}/{userId}", async (string roomId, string userId, HttpContext http, EventSender events, CancellationToken cancellationToken) =>
        {
            if (!UserId.TryParse(userId, out UserId user) || user.ServerName != http.GetOrigin()) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "The user is not of the asking server.");
            (JsonObject template, RoomVersion version) = await events.MakeJoinAsync(roomId, userId, cancellationToken);
            if (!http.Request.Query["ver"].Contains(version.Id)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.IncompatibleRoomVersion, $"The room has version {version.Id}, which the asking server does not support.");
            return MatrixHttp.Json(new JsonObject { ["event"] = template, ["room_version"] = version.Id });
        });
        routes.MapPut("/_matrix/federation/v2/send_join/{roomId}/{eventId}", async (string roomId, string eventId, JsonObject pdu, HttpContext http, EventSender events, RemoteServerKeys keys, IOptionsMonitor<ServerOptions> server, CancellationToken cancellationToken) =>
        {
            if (pdu["room_id"]?.GetValue<string>() != roomId) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The event is of another room.");
            string origin = http.GetOrigin();
            Dictionary<string, string> originKeys = [];
            foreach (string keyId in (pdu["signatures"]?[origin] as JsonObject ?? []).Select(k => k.Key))
            {
                if (await keys.GetVerifyKeyAsync(origin, keyId, cancellationToken) is string key) originKeys[keyId] = key;
            }
            (List<JsonObject> state, List<JsonObject> authChain) = await events.AcceptJoinAsync(pdu, eventId, origin, originKeys, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["origin"] = server.CurrentValue.ServerName, ["members_omitted"] = false, ["state"] = new JsonArray([.. state]), ["auth_chain"] = new JsonArray([.. authChain]) });
        }).RequireFederation();

        // Transactions of events from other servers; EDUs are not used yet
        federation.MapPut("/send/{txnId}", async (string txnId, JsonObject body, HttpContext http, EventSender events, RemoteServerKeys keys, CancellationToken cancellationToken) =>
        {
            JsonObject results = [];
            foreach (JsonObject pdu in (body["pdus"] as JsonArray ?? []).OfType<JsonObject>().Take(50))
            {
                Dictionary<string, string> senderKeys = [];
                if (UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId sender))
                {
                    foreach (string keyId in (pdu["signatures"]?[sender.ServerName] as JsonObject ?? []).Select(k => k.Key))
                    {
                        if (await keys.GetVerifyKeyAsync(sender.ServerName, keyId, cancellationToken) is string key) senderKeys[keyId] = key;
                    }
                }
                (string? eventId, string? error) = await events.AcceptEventAsync(pdu, senderKeys, cancellationToken);
                if (eventId is not null) results[eventId] = error is null ? new JsonObject() : new JsonObject { ["error"] = error };
            }
            return MatrixHttp.Json(new JsonObject { ["pdus"] = results });
        });
        // Return routes
        return routes;
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
