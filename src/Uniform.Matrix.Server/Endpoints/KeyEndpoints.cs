namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#key-management-api">Key management</see>, <see href="https://spec.matrix.org/latest/client-server-api/#cross-signing">cross-signing</see> and <see href="https://spec.matrix.org/latest/client-server-api/#send-to-device-messaging">to-device messages</see>.</summary>
public static class KeyEndpoints
{
    /// <summary>Map the key endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapKeys(this IEndpointRouteBuilder routes)
    {
        // Device, one-time and fallback keys
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapPost("/keys/upload", async (JsonObject body, HttpContext http, DeviceKeyService keys, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await keys.UploadAsync(http.GetRequester(), body["device_keys"] as JsonObject, body["one_time_keys"] as JsonObject, (body["fallback_keys"] ?? body["org.matrix.msc2732.fallback_keys"]) as JsonObject, cancellationToken)));
        client.MapPost("/keys/query", async (JsonObject body, HttpContext http, DeviceKeyService keys, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await keys.QueryAsync(http.GetRequester().UserId, body["device_keys"] as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing device_keys."), cancellationToken)));
        client.MapPost("/keys/claim", async (JsonObject body, DeviceKeyService keys, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await keys.ClaimAsync(body["one_time_keys"] as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing one_time_keys."), cancellationToken)));
        client.MapGet("/keys/changes", async (string from, string to, HttpContext http, IDbContextFactory<ServerDbContext> contexts, CancellationToken cancellationToken) =>
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            (IReadOnlyList<string> changed, IReadOnlyList<string> left) = await ToDeviceService.GetDeviceListsAsync(db, http.GetRequester().UserId, SyncService.ParseToken(from), SyncService.ParseToken(to), cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["changed"] = new JsonArray([.. changed.Select(u => (JsonNode)u)]), ["left"] = new JsonArray([.. left.Select(u => (JsonNode)u)]) });
        });

        // Cross-signing keys, after the password when replacing them
        client.MapPost("/keys/device_signing/upload", async (JsonObject body, HttpContext http, DeviceKeyService keys, AccountService accounts, UserInteractiveAuth auth, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            await keys.UploadCrossSigningKeysAsync(requester.UserId, body["master_key"] as JsonObject, body["self_signing_key"] as JsonObject, body["user_signing_key"] as JsonObject,
                () => AccountEndpoints.RequirePasswordAsync(requester, body["auth"] as JsonObject, accounts, auth, cancellationToken), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapPost("/keys/signatures/upload", async (JsonObject body, HttpContext http, DeviceKeyService keys, CancellationToken cancellationToken) => MatrixHttp.Json(await keys.UploadSignaturesAsync(http.GetRequester().UserId, body, cancellationToken)));

        // To-device messages
        client.MapPut("/sendToDevice/{eventType}/{txnId}", async (string eventType, string txnId, JsonObject body, HttpContext http, ToDeviceService toDevice, CancellationToken cancellationToken) =>
        {
            await toDevice.SendAsync(http.GetRequester(), eventType, txnId, body["messages"] as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing messages."), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        // Return routes
        return routes;
    }
}
