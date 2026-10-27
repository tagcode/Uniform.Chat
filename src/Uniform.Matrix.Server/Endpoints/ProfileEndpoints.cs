namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#profiles">Profiles</see> and <see href="https://spec.matrix.org/latest/client-server-api/#presence">presence</see>.</summary>
public static class ProfileEndpoints
{
    /// <summary>Map the profile and presence endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapProfiles(this IEndpointRouteBuilder routes)
    {
        // Reading profiles
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapGet("/profile/{userId}", async (string userId, ProfileService profiles, CancellationToken cancellationToken) => MatrixHttp.Json(await profiles.GetProfileAsync(userId, cancellationToken)));
        client.MapGet("/profile/{userId}/{field}", async (string userId, string field, ProfileService profiles, CancellationToken cancellationToken) => MatrixHttp.Json(await profiles.GetFieldAsync(userId, field, cancellationToken)));

        // Own profile fields
        client.MapPut("/profile/{userId}/{field}", async (string userId, string field, JsonObject body, HttpContext http, ProfileService profiles, CancellationToken cancellationToken) =>
        {
            await profiles.SetFieldAsync(http.GetOwnRequester(userId).UserId, field, Text(body, field), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Presence
        client.MapGet("/presence/{userId}/status", (string userId, PresenceService presence) => MatrixHttp.Json(presence.Get(userId)));
        client.MapPut("/presence/{userId}/status", async (string userId, JsonObject body, HttpContext http, PresenceService presence, CancellationToken cancellationToken) =>
        {
            await presence.SetAsync(http.GetOwnRequester(userId).UserId, Text(body, "presence") ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing presence."), Text(body, "status_msg"), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        // Return routes
        return routes;
    }

    /// <summary>Return a string field of the body, null when missing or null; throw <c>M_INVALID_PARAM</c> for other types.</summary>
    private static string? Text(JsonObject body, string field) => body[field] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{field} must be a string."),
    };
}
