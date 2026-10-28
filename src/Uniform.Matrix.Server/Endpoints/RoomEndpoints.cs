namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;

/// <summary>Room creation, <see href="https://spec.matrix.org/latest/client-server-api/#room-membership">membership</see>, messages, state and redaction.</summary>
public static class RoomEndpoints
{
    /// <summary>Map the room endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapRooms(this IEndpointRouteBuilder routes)
    {
        // Creation and the user's rooms
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3").RequireAccessToken();
        client.MapPost("/createRoom", async (JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["room_id"] = await rooms.CreateRoomAsync(http.GetRequester(), body, cancellationToken) })).RequireRateLimiting(RateLimits.Writes);
        client.MapGet("/joined_rooms", async (HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["joined_rooms"] = new JsonArray([.. (await rooms.GetJoinedRoomsAsync(http.GetRequester().UserId, cancellationToken)).Select(r => (JsonNode)r)]) }));

        // Own membership
        client.MapPost("/join/{roomIdOrAlias}", async (string roomIdOrAlias, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["room_id"] = await rooms.JoinAsync(http.GetRequester().UserId, roomIdOrAlias, await ReasonAsync(http, cancellationToken), cancellationToken, [.. http.Request.Query["via"].Concat(http.Request.Query["server_name"]).OfType<string>()]) }));
        client.MapPost("/rooms/{roomId}/join", async (string roomId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["room_id"] = await rooms.JoinAsync(http.GetRequester().UserId, roomId, await ReasonAsync(http, cancellationToken), cancellationToken) }));
        client.MapPost("/rooms/{roomId}/leave", async (string roomId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => { await rooms.LeaveAsync(http.GetRequester().UserId, roomId, await ReasonAsync(http, cancellationToken), cancellationToken); return Empty(); });
        client.MapPost("/rooms/{roomId}/forget", async (string roomId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => { await rooms.ForgetAsync(http.GetRequester().UserId, roomId, cancellationToken); return Empty(); });

        // Others' membership
        client.MapPost("/rooms/{roomId}/invite", async (string roomId, JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => { await rooms.InviteAsync(http.GetRequester().UserId, roomId, Target(body), body["reason"]?.GetValue<string>(), isDirect: false, cancellationToken); return Empty(); }).RequireRateLimiting(RateLimits.Writes);
        client.MapPost("/rooms/{roomId}/kick", (string roomId, JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => ChangeAsync(http, rooms, roomId, body, Memberships.Leave, cancellationToken));
        client.MapPost("/rooms/{roomId}/ban", (string roomId, JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => ChangeAsync(http, rooms, roomId, body, Memberships.Ban, cancellationToken));
        client.MapPost("/rooms/{roomId}/unban", (string roomId, JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => ChangeAsync(http, rooms, roomId, body, Memberships.Leave, cancellationToken));

        // Reports of content, rooms and users, kept for the admin to review
        foreach (string pattern in new[] { "/rooms/{roomId}/report/{eventId}", "/rooms/{roomId}/report", "/users/{userId}/report" })
        {
            client.MapPost(pattern, async (HttpContext http, ReportService reports, CancellationToken cancellationToken) =>
            {
                JsonObject? body = http.Request.ContentLength is not 0 && http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<JsonObject>(cancellationToken) : null;
                string reason = body?["reason"] is JsonValue value && value.TryGetValue(out string? text) ? text : "";
                string? Route(string key) => http.Request.RouteValues.TryGetValue(key, out object? routeValue) ? routeValue as string : null;
                await reports.AddAsync(http.GetRequester().UserId, Route("roomId"), Route("eventId"), Route("userId"), reason, cancellationToken);
                return MatrixHttp.Json(new JsonObject());
            }).RequireRateLimiting(RateLimits.Writes);
        }

        // Messages, state, redaction
        client.MapPut("/rooms/{roomId}/send/{eventType}/{txnId}", async (string roomId, string eventType, string txnId, JsonObject body, HttpContext http, RoomService rooms, DelayedEvents delayed, CancellationToken cancellationToken) =>
            CallEndpoints.GetDelay(http) is long delay
                ? MatrixHttp.Json(new JsonObject { ["delay_id"] = delayed.Schedule(http.GetRequester(), roomId, eventType, null, body, delay) })
                : MatrixHttp.Json(new JsonObject { ["event_id"] = await rooms.SendMessageAsync(http.GetRequester(), roomId, eventType, body, txnId, cancellationToken) })).RequireRateLimiting(RateLimits.Writes);
        client.MapPut("/rooms/{roomId}/state/{eventType}/{stateKey?}", async (string roomId, string eventType, string? stateKey, JsonObject body, HttpContext http, RoomService rooms, DelayedEvents delayed, CancellationToken cancellationToken) =>
            CallEndpoints.GetDelay(http) is long delay
                ? MatrixHttp.Json(new JsonObject { ["delay_id"] = delayed.Schedule(http.GetRequester(), roomId, eventType, stateKey ?? "", body, delay) })
                : MatrixHttp.Json(new JsonObject { ["event_id"] = await rooms.SetStateAsync(http.GetRequester(), roomId, eventType, stateKey ?? "", body, cancellationToken) })).RequireRateLimiting(RateLimits.Writes);
        client.MapPut("/rooms/{roomId}/redact/{eventId}/{txnId}", async (string roomId, string eventId, string txnId, JsonObject body, HttpContext http, RoomService rooms, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["event_id"] = await rooms.RedactAsync(http.GetRequester(), roomId, eventId, body["reason"]?.GetValue<string>(), txnId, cancellationToken) })).RequireRateLimiting(RateLimits.Writes);

        // Reading
        client.MapGet("/rooms/{roomId}/state", async (string roomId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonArray([.. await rooms.GetStateAsync(http.GetRequester(), roomId, cancellationToken)])));
        client.MapGet("/rooms/{roomId}/state/{eventType}/{stateKey?}", async (string roomId, string eventType, string? stateKey, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(await rooms.GetStateContentAsync(http.GetRequester(), roomId, eventType, stateKey ?? "", cancellationToken)));
        client.MapGet("/rooms/{roomId}/event/{eventId}", async (string roomId, string eventId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(await rooms.GetEventAsync(http.GetRequester(), roomId, eventId, cancellationToken)));
        client.MapGet("/rooms/{roomId}/members", async (string roomId, string? membership, string? not_membership, HttpContext http, RoomService rooms, CancellationToken cancellationToken) =>
        {
            IEnumerable<JsonObject> members = (await rooms.GetStateAsync(http.GetRequester(), roomId, cancellationToken)).Where(e => e["type"]?.GetValue<string>() == EventTypes.RoomMember);
            if (membership is not null) members = members.Where(e => e["content"]?["membership"]?.GetValue<string>() == membership);
            if (not_membership is not null) members = members.Where(e => e["content"]?["membership"]?.GetValue<string>() != not_membership);
            return MatrixHttp.Json(new JsonObject { ["chunk"] = new JsonArray([.. members]) });
        });
        client.MapGet("/rooms/{roomId}/joined_members", async (string roomId, HttpContext http, RoomService rooms, CancellationToken cancellationToken) => MatrixHttp.Json(await rooms.GetJoinedMembersAsync(http.GetRequester(), roomId, cancellationToken)));
        // Return routes
        return routes;
    }

    /// <summary>Kick, bans or unbans the body's <c>user_id</c>.</summary>
    private static async Task<IResult> ChangeAsync(HttpContext http, RoomService rooms, string roomId, JsonObject body, string membership, CancellationToken cancellationToken)
    {
        // Change, then empty answer
        await rooms.SetMembershipAsync(http.GetRequester().UserId, roomId, Target(body), membership, body["reason"]?.GetValue<string>(), cancellationToken);
        // Return empty
        return Empty();
    }

    /// <summary>Return the body's <c>user_id</c>; throw <c>M_MISSING_PARAM</c> without one.</summary>
    private static string Target(JsonObject body) => body["user_id"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing user_id.");

    /// <summary>Return the optional <c>reason</c> of a membership request, whose body may be empty.</summary>
    private static async Task<string?> ReasonAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // Empty bodies are allowed
        if (http.Request.ContentLength is 0 || !http.Request.HasJsonContentType() && http.Request.ContentLength is null) return null;
        JsonObject? body = await http.Request.ReadFromJsonAsync<JsonObject>(MatrixJson.Options, cancellationToken);
        // Return reason
        return body?["reason"]?.GetValue<string>();
    }

    /// <summary>Return an empty JSON object.</summary>
    private static IResult Empty() => MatrixHttp.Json(new JsonObject());
}
