namespace Uniform.Matrix.Server.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Http;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#server-discovery">Server discovery</see> and supported versions.</summary>
public static class DiscoveryEndpoints
{
    /// <summary>Spec versions this server implements enough of for clients that check them.</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["v1.1", "v1.2", "v1.3", "v1.4", "v1.5", "v1.6", "v1.7", "v1.8", "v1.9", "v1.10", "v1.11", "v1.12"];

    /// <summary>Map <c>/_matrix/client/versions</c>, <c>/.well-known/matrix/client</c> and <c>/.well-known/matrix/server</c>.</summary>
    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder routes)
    {
        // Versions, no auth needed; delayed events for calls
        routes.MapGet("/_matrix/client/versions", () => MatrixHttp.Json(new VersionsResponse { Versions = SupportedVersions, UnstableFeatures = new Dictionary<string, bool> { ["org.matrix.msc4140"] = true } }));

        // Well-known only with a configured public address, with the call service when there is one
        routes.MapGet("/.well-known/matrix/client", (IOptionsMonitor<ServerOptions> options, IOptionsMonitor<CallOptions> calls) =>
        {
            if (options.CurrentValue.PublicBaseUrl is not string url) return MatrixHttp.Error(System.Net.HttpStatusCode.NotFound, ErrorCodes.NotFound, "No public base URL configured.");
            JsonObject wellKnown = new() { ["m.homeserver"] = new JsonObject { ["base_url"] = url } };
            if (CallEndpoints.Transports(calls.CurrentValue) is { Count: > 0 } transports) wellKnown["org.matrix.msc4143.rtc_foci"] = transports;
            return MatrixHttp.Json(wellKnown);
        });
        // No bridges to other networks; clients ask before showing a directory of them
        routes.MapGet("/_matrix/client/v3/thirdparty/protocols", () => MatrixHttp.Json(new JsonObject())).RequireAccessToken();

        // Delegation of federation, when set
        routes.MapGet("/.well-known/matrix/server", (IOptionsMonitor<FederationOptions> federation) => federation.CurrentValue.WellKnownServer is string server
            ? MatrixHttp.Json(new JsonObject { ["m.server"] = server })
            : MatrixHttp.Error(System.Net.HttpStatusCode.NotFound, ErrorCodes.NotFound, "No federation delegation configured."));
        // Return routes
        return routes;
    }
}
