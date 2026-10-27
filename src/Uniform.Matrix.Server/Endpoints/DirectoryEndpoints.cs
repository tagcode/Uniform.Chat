namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#room-aliases">Room aliases</see>, the <see href="https://spec.matrix.org/latest/client-server-api/#listing-rooms">public room directory</see> and <see href="https://spec.matrix.org/latest/client-server-api/#user-directory">user search</see>.</summary>
public static class DirectoryEndpoints
{
    private const int DefaultUserSearchLimit = 10;

    /// <summary>Map the directory endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapDirectory(this IEndpointRouteBuilder routes)
    {
        // Public reads: resolving aliases, visibility, the room list
        RouteGroupBuilder open = routes.MapGroup("/_matrix/client/v3");
        open.MapGet("/directory/room/{roomAlias}", async (string roomAlias, DirectoryService directory, CancellationToken cancellationToken) => MatrixHttp.Json(await directory.GetAliasAsync(roomAlias, cancellationToken)));
        open.MapGet("/directory/list/room/{roomId}", async (string roomId, DirectoryService directory, CancellationToken cancellationToken) => MatrixHttp.Json(await directory.GetVisibilityAsync(roomId, cancellationToken)));
        open.MapGet("/publicRooms", async (int? limit, string? since, string? server, DirectoryService directory, CancellationToken cancellationToken) => MatrixHttp.Json(await directory.GetPublicRoomsAsync(limit, since, null, server, cancellationToken)));

        // Room summaries, also at their unstable paths, and space hierarchies
        RouteGroupBuilder rooms = routes.MapGroup("/_matrix/client").RequireAccessToken();
        foreach (string summary in new[] { "/v1/room_summary/{roomIdOrAlias}", "/unstable/im.nheko.summary/summary/{roomIdOrAlias}", "/unstable/im.nheko.summary/rooms/{roomIdOrAlias}/summary" })
        {
            rooms.MapGet(summary, async (string roomIdOrAlias, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) => MatrixHttp.Json(await directory.GetSummaryAsync(http.GetRequester().UserId, roomIdOrAlias, cancellationToken)));
        }
        rooms.MapGet("/v1/rooms/{roomId}/hierarchy", async (string roomId, int? max_depth, int? limit, string? from, bool? suggested_only, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await directory.GetHierarchyAsync(http.GetRequester().UserId, roomId, max_depth, Math.Clamp(limit ?? 50, 1, 100), from, suggested_only == true, cancellationToken)));

        // Changing aliases and visibility
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapPut("/directory/room/{roomAlias}", async (string roomAlias, JsonObject body, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
        {
            string roomId = body["room_id"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing room_id.");
            await directory.CreateAliasAsync(http.GetRequester().UserId, roomAlias, roomId, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapDelete("/directory/room/{roomAlias}", async (string roomAlias, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
        {
            await directory.DeleteAliasAsync(http.GetRequester().UserId, roomAlias, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapGet("/rooms/{roomId}/aliases", async (string roomId, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) => MatrixHttp.Json(await directory.GetRoomAliasesAsync(http.GetRequester().UserId, roomId, cancellationToken)));
        client.MapPut("/directory/list/room/{roomId}", async (string roomId, JsonObject body, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
        {
            await directory.SetVisibilityAsync(http.GetRequester().UserId, roomId, body["visibility"]?.GetValue<string>() ?? "public", cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Searching rooms and users
        client.MapPost("/publicRooms", async (string? server, JsonObject body, DirectoryService directory, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await directory.GetPublicRoomsAsync(body["limit"]?.GetValue<int>(), body["since"]?.GetValue<string>(), body["filter"]?["generic_search_term"]?.GetValue<string>(), server, cancellationToken)));
        client.MapPost("/user_directory/search", async (JsonObject body, HttpContext http, DirectoryService directory, CancellationToken cancellationToken) =>
        {
            string term = body["search_term"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing search_term.");
            int limit = Math.Clamp(body["limit"]?.GetValue<int>() ?? DefaultUserSearchLimit, 1, 100);
            return MatrixHttp.Json(await directory.SearchUsersAsync(http.GetRequester().UserId, term, limit, cancellationToken));
        });
        // Return routes
        return routes;
    }
}
