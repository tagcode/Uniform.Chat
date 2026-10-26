namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#typing-notifications">Typing</see>, <see href="https://spec.matrix.org/latest/client-server-api/#receipts">receipts</see> and <see href="https://spec.matrix.org/latest/client-server-api/#fully-read-markers">read markers</see>.</summary>
public static class ActivityEndpoints
{
    private static readonly TimeSpan DefaultTypingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Map the activity endpoints under <c>/_matrix/client/v3</c>.</summary>
    public static IEndpointRouteBuilder MapActivity(this IEndpointRouteBuilder routes)
    {
        // Typing of the requester
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client/v3/rooms/{roomId}").RequireAccessToken();
        client.MapPut("/typing/{userId}", async (string roomId, string userId, JsonObject body, HttpContext http, TypingService typing, CancellationToken cancellationToken) =>
        {
            bool isTyping = body["typing"]?.GetValue<bool>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing typing.");
            TimeSpan timeout = body["timeout"]?.GetValue<long>() is long ms ? TimeSpan.FromMilliseconds(ms) : DefaultTypingTimeout;
            await typing.SetTypingAsync(http.GetOwnRequester(userId).UserId, roomId, isTyping, timeout, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });

        // Receipts and markers
        client.MapPost("/receipt/{receiptType}/{eventId}", async (string roomId, string receiptType, string eventId, HttpContext http, ReceiptService receipts, CancellationToken cancellationToken) =>
        {
            JsonObject? body = http.Request.ContentLength is > 0 ? await http.Request.ReadFromJsonAsync<JsonObject>(cancellationToken) : null;
            await receipts.SetReceiptAsync(http.GetRequester().UserId, roomId, receiptType, eventId, body?["thread_id"]?.GetValue<string>(), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        client.MapPost("/read_markers", async (string roomId, JsonObject body, HttpContext http, ReceiptService receipts, CancellationToken cancellationToken) =>
        {
            await receipts.SetReadMarkersAsync(http.GetRequester().UserId, roomId, body, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        // Return routes
        return routes;
    }
}
