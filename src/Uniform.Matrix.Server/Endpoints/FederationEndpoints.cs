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
