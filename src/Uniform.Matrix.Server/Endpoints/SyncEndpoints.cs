namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#syncing">Sync</see>, <see href="https://spec.matrix.org/latest/client-server-api/#filtering">filters</see>, room history and event context.</summary>
public static class SyncEndpoints
{
    private const int MaxTimeoutMs = 60_000;

    /// <summary>Map the sync endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapSync(this IEndpointRouteBuilder routes)
    {
        // Sync
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        // Simplified sliding sync of newer clients such as Element X
        routes.MapPost("/_matrix/client/unstable/org.matrix.simplified_msc3575/sync", async (string? pos, long? timeout, JsonObject body, HttpContext http, SlidingSyncService sliding, PresenceService presence, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            await presence.TouchAsync(requester.UserId, null, cancellationToken);
            TimeSpan wait = TimeSpan.FromMilliseconds(Math.Clamp(timeout ?? 0, 0, MaxTimeoutMs));
            return MatrixHttp.Json(await sliding.SyncAsync(requester, pos, wait, body, cancellationToken));
        }).RequireAccessToken();

        client.MapGet("/sync", async (string? since, string? filter, long? timeout, bool? full_state, string? set_presence, HttpContext http, SyncService sync, PresenceService presence, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            await presence.TouchAsync(requester.UserId, set_presence, cancellationToken);
            SyncFilter parsed = await sync.ResolveFilterAsync(requester.UserId, filter, cancellationToken);
            TimeSpan wait = TimeSpan.FromMilliseconds(Math.Clamp(timeout ?? 0, 0, MaxTimeoutMs));
            return MatrixHttp.Json(await sync.SyncAsync(requester, since, wait, parsed, full_state == true, cancellationToken));
        });

        // Filters of the requester only
        client.MapPost("/user/{userId}/filter", async (string userId, JsonObject body, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["filter_id"] = await sync.CreateFilterAsync(http.GetOwnRequester(userId).UserId, body, cancellationToken) }));
        client.MapGet("/user/{userId}/filter/{filterId}", async (string userId, string filterId, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await sync.GetFilterAsync(http.GetOwnRequester(userId).UserId, filterId, cancellationToken)));

        // History and context
        client.MapGet("/rooms/{roomId}/messages", async (string roomId, string? from, string? to, string? dir, int? limit, string? filter, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            if (dir is not null and not "b" and not "f") throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "dir must be b or f.");
            SyncFilter parsed = await sync.ResolveFilterAsync(requester.UserId, filter, cancellationToken);
            return MatrixHttp.Json(await sync.GetMessagesAsync(requester, roomId, from, to, dir != "f", Math.Clamp(limit ?? 10, 1, 1000), parsed, cancellationToken));
        });
        // Relations of an event: edits, reactions, thread replies
        RouteGroupBuilder v1 = routes.MapGroup("/_matrix/client/v1/rooms/{roomId}").RequireAccessToken();
        foreach (string pattern in new[] { "/relations/{eventId}", "/relations/{eventId}/{relType}", "/relations/{eventId}/{relType}/{eventType}" })
        {
            v1.MapGet(pattern, async (string roomId, string eventId, string? relType, string? eventType, string? from, int? limit, string? dir, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
                MatrixHttp.Json(await sync.GetRelationsAsync(http.GetRequester(), roomId, eventId, relType, eventType, from, Math.Clamp(limit ?? 50, 1, 100), dir == "f", cancellationToken)));
        }
        v1.MapGet("/timestamp_to_event", async (string roomId, long ts, string? dir, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await sync.GetEventAtAsync(http.GetRequester(), roomId, ts, dir != "b", cancellationToken)));

        client.MapGet("/rooms/{roomId}/context/{eventId}", async (string roomId, string eventId, int? limit, string? filter, HttpContext http, SyncService sync, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            SyncFilter parsed = await sync.ResolveFilterAsync(requester.UserId, filter, cancellationToken);
            return MatrixHttp.Json(await sync.GetContextAsync(requester, roomId, eventId, Math.Clamp(limit ?? 10, 0, 1000), parsed, cancellationToken));
        });
        // Return routes
        return routes;
    }
}
