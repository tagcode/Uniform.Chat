namespace Uniform.Matrix.Server.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#client-config">Account data</see> and <see href="https://spec.matrix.org/latest/client-server-api/#room-tagging">room tags</see> of the requester.</summary>
public static class AccountDataEndpoints
{
    /// <summary>Map the account data endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapAccountData(this IEndpointRouteBuilder routes)
    {
        // Global account data
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3/user/{userId}").RequireAccessToken();
        client.MapPut("/account_data/{type}", async (string userId, string type, JsonObject body, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
        {
            await data.SetFromClientAsync(http.GetOwnRequester(userId).UserId, null, type, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapGet("/account_data/{type}", async (string userId, string type, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await data.GetAsync(http.GetOwnRequester(userId).UserId, null, type, cancellationToken)));

        // Room account data
        client.MapPut("/rooms/{roomId}/account_data/{type}", async (string userId, string roomId, string type, JsonObject body, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
        {
            await data.SetFromClientAsync(http.GetOwnRequester(userId).UserId, roomId, type, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapGet("/rooms/{roomId}/account_data/{type}", async (string userId, string roomId, string type, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
            MatrixHttp.Json(await data.GetAsync(http.GetOwnRequester(userId).UserId, roomId, type, cancellationToken)));

        // Tags
        client.MapGet("/rooms/{roomId}/tags", async (string userId, string roomId, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["tags"] = await data.GetTagsAsync(http.GetOwnRequester(userId).UserId, roomId, cancellationToken) }));
        client.MapPut("/rooms/{roomId}/tags/{tag}", async (string userId, string roomId, string tag, JsonObject body, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
        {
            await data.SetTagAsync(http.GetOwnRequester(userId).UserId, roomId, tag, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapDelete("/rooms/{roomId}/tags/{tag}", async (string userId, string roomId, string tag, HttpContext http, AccountDataService data, CancellationToken cancellationToken) =>
        {
            await data.DeleteTagAsync(http.GetOwnRequester(userId).UserId, roomId, tag, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        // Return routes
        return routes;
    }
}
