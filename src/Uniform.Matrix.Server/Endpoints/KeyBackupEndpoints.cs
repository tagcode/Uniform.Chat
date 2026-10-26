namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Http;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#server-side-key-backups">Server-side key backups</see> of the requester.</summary>
public static class KeyBackupEndpoints
{
    /// <summary>Map the backup endpoints under <c>/_matrix/client/v3/room_keys</c>.</summary>
    public static IEndpointRouteBuilder MapKeyBackup(this IEndpointRouteBuilder routes)
    {
        // Versions
        RouteGroupBuilder backup = routes.MapGroup("/_matrix/client/v3/room_keys").RequireAccessToken();
        backup.MapPost("/version", async (JsonObject body, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["version"] = await backups.CreateVersionAsync(http.GetRequester().UserId, body, cancellationToken) }));
        backup.MapGet("/version", async (HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.GetVersionAsync(http.GetRequester().UserId, null, cancellationToken)));
        backup.MapGet("/version/{version}", async (string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.GetVersionAsync(http.GetRequester().UserId, version, cancellationToken)));
        backup.MapPut("/version/{version}", async (string version, JsonObject body, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) =>
        {
            await backups.UpdateVersionAsync(http.GetRequester().UserId, version, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        backup.MapDelete("/version/{version}", async (string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) =>
        {
            await backups.DeleteVersionAsync(http.GetRequester().UserId, version, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Keys: all, one room, one session
        backup.MapPut("/keys", (string version, JsonObject body, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) =>
            PutAsync(http, backups, version, (body["rooms"] as JsonObject ?? []).SelectMany(r => Sessions(r.Key, r.Value?["sessions"])), cancellationToken));
        backup.MapPut("/keys/{roomId}", (string roomId, string version, JsonObject body, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) =>
            PutAsync(http, backups, version, Sessions(roomId, body["sessions"]), cancellationToken));
        backup.MapPut("/keys/{roomId}/{sessionId}", (string roomId, string sessionId, string version, JsonObject body, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) =>
            PutAsync(http, backups, version, [(roomId, sessionId, body)], cancellationToken));
        backup.MapGet("/keys", async (string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.GetKeysAsync(http.GetRequester().UserId, version, null, null, cancellationToken)));
        backup.MapGet("/keys/{roomId}", async (string roomId, string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.GetKeysAsync(http.GetRequester().UserId, version, roomId, null, cancellationToken)));
        backup.MapGet("/keys/{roomId}/{sessionId}", async (string roomId, string sessionId, string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.GetKeysAsync(http.GetRequester().UserId, version, roomId, sessionId, cancellationToken)));
        backup.MapDelete("/keys", async (string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.DeleteKeysAsync(http.GetRequester().UserId, version, null, null, cancellationToken)));
        backup.MapDelete("/keys/{roomId}", async (string roomId, string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.DeleteKeysAsync(http.GetRequester().UserId, version, roomId, null, cancellationToken)));
        backup.MapDelete("/keys/{roomId}/{sessionId}", async (string roomId, string sessionId, string version, HttpContext http, KeyBackupService backups, CancellationToken cancellationToken) => MatrixHttp.Json(await backups.DeleteKeysAsync(http.GetRequester().UserId, version, roomId, sessionId, cancellationToken)));
        // Return routes
        return routes;
    }

    /// <summary>Store keys and answer with the etag and count.</summary>
    private static async Task<IResult> PutAsync(HttpContext http, KeyBackupService backups, string version, IEnumerable<(string, string, JsonObject)> keys, CancellationToken cancellationToken) =>
        MatrixHttp.Json(await backups.PutKeysAsync(http.GetRequester().UserId, version, [.. keys], cancellationToken));

    /// <summary>Return the sessions of one room's <c>sessions</c> object.</summary>
    private static IEnumerable<(string RoomId, string SessionId, JsonObject Data)> Sessions(string roomId, JsonNode? sessions) =>
        (sessions as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing sessions.")).Select(s => (roomId, s.Key, s.Value as JsonObject ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Key data must be an object.")));
}
