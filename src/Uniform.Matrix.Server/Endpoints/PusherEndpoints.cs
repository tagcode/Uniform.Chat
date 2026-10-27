namespace Uniform.Matrix.Server.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;

/// <summary>The requester's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3pushers">pushers</see>: where their push notifications go.</summary>
public static class PusherEndpoints
{
    /// <summary>Map the pusher endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapPushers(this IEndpointRouteBuilder routes)
    {
        // List, and add, replace or delete
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapGet("/pushers", async (HttpContext http, PusherService pushers, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["pushers"] = await pushers.GetAsync(http.GetRequester().UserId, cancellationToken) }));
        client.MapPost("/pushers/set", async (JsonObject body, HttpContext http, PusherService pushers, CancellationToken cancellationToken) =>
        {
            Requester requester = http.GetRequester();
            await pushers.SetAsync(requester.UserId, requester.DeviceId, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        // Return routes
        return routes;
    }
}
