namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;

/// <summary>What calls need from the homeserver: OpenID tokens for the MatrixRTC authorization service, the call service's address, delayed events, and TURN credentials for older 1:1 calls.</summary>
public static class CallEndpoints
{
    /// <summary>Query parameter that delays a send or state event, see <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4140">MSC4140</see>.</summary>
    public const string DelayParameter = "org.matrix.msc4140.delay";

    /// <summary>Map the OpenID, RTC transport, delayed event and TURN endpoints.</summary>
    public static IEndpointRouteBuilder MapCalls(this IEndpointRouteBuilder routes)
    {
        // OpenID tokens for the own user, and checking them for services, which do not sign requests
        RouteGroupBuilder client = routes.MapGroup("/_matrix/client").RequireAccessToken();
        client.MapPost("/v3/user/{userId}/openid/request_token", (string userId, HttpContext http, OpenIdTokens tokens, IOptionsMonitor<ServerOptions> server) =>
        {
            (string token, TimeSpan lifetime) = tokens.Issue(http.GetOwnRequester(userId).UserId);
            return MatrixHttp.Json(new JsonObject { ["access_token"] = token, ["token_type"] = "Bearer", ["matrix_server_name"] = server.CurrentValue.ServerName, ["expires_in"] = (long)lifetime.TotalSeconds });
        });
        routes.MapGet("/_matrix/federation/v1/openid/userinfo", (string? access_token, OpenIdTokens tokens) =>
            tokens.Validate(access_token) is string userId ? MatrixHttp.Json(new JsonObject { ["sub"] = userId }) : MatrixHttp.Error(HttpStatusCode.Unauthorized, ErrorCodes.UnknownToken, "Unknown or expired OpenID token.")).RequireFederation(signed: false);

        // The MatrixRTC service, when configured
        client.MapGet("/unstable/org.matrix.msc4143/rtc/transports", (IOptionsMonitor<CallOptions> options) => MatrixHttp.Json(new JsonObject { ["rtc_transports"] = Transports(options.CurrentValue) }));

        // TURN credentials by the shared-secret scheme of coturn; nothing without a TURN server
        client.MapGet("/v3/voip/turnServer", (HttpContext http, IOptionsMonitor<CallOptions> options, TimeProvider time) =>
        {
            CallOptions calls = options.CurrentValue;
            if (calls.TurnSharedSecret is not string secret || calls.GetTurnUris() is not { Count: > 0 } uris) return MatrixHttp.Json(new JsonObject());
            string username = $"{(time.GetUtcNow() + calls.TurnUserLifetime).ToUnixTimeSeconds()}:{http.GetRequester().UserId}";
            string password = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
            return MatrixHttp.Json(new JsonObject { ["username"] = username, ["password"] = password, ["uris"] = new JsonArray([.. uris.Select(u => (JsonNode)u)]), ["ttl"] = (long)calls.TurnUserLifetime.TotalSeconds });
        });

        // Delayed events: the own list needs a token, acting on one only its ID
        client.MapGet("/unstable/org.matrix.msc4140/delayed_events", (HttpContext http, DelayedEvents delayed) => MatrixHttp.Json(new JsonObject { ["delayed_events"] = delayed.List(http.GetRequester().UserId) }));
        RouteGroupBuilder actions = routes.MapGroup("/_matrix/client/unstable/org.matrix.msc4140/delayed_events");
        actions.MapPost("/{delayId}", async (string delayId, JsonObject body, DelayedEvents delayed, CancellationToken cancellationToken) =>
        {
            await delayed.ActAsync(delayId, body["action"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing action."), cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        }).RequireRateLimiting(RateLimits.Writes);
        actions.MapPost("/{delayId}/{action}", async (string delayId, string action, DelayedEvents delayed, CancellationToken cancellationToken) =>
        {
            await delayed.ActAsync(delayId, action, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        }).RequireRateLimiting(RateLimits.Writes);
        // Return routes
        return routes;
    }

    /// <summary>Return the MatrixRTC transports of <paramref name="options"/>: the LiveKit service, or none.</summary>
    public static JsonArray Transports(CallOptions options) => options.LiveKitServiceUrl is string url ? new JsonArray(new JsonObject { ["type"] = "livekit", ["livekit_service_url"] = url }) : [];

    /// <summary>Return the delay of a send or state request in milliseconds, or null when it is not delayed; throw <c>M_INVALID_PARAM</c> for a bad value.</summary>
    public static long? GetDelay(HttpContext http) => http.Request.Query[DelayParameter] is [string text]
        ? long.TryParse(text, out long delay) ? delay : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{DelayParameter} must be milliseconds.")
        : null;
}
