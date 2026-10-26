namespace Uniform.Matrix.Server.Endpoints;

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

    /// <summary>Map <c>/_matrix/client/versions</c> and <c>/.well-known/matrix/client</c>.</summary>
    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder routes)
    {
        // Versions, no auth needed
        routes.MapGet("/_matrix/client/versions", () => MatrixHttp.Json(new VersionsResponse { Versions = SupportedVersions, UnstableFeatures = new Dictionary<string, bool>() }));

        // Well-known only with a configured public address
        routes.MapGet("/.well-known/matrix/client", (IOptionsMonitor<ServerOptions> options) => options.CurrentValue.PublicBaseUrl is string url
            ? MatrixHttp.Json(new WellKnownClient { Homeserver = new WellKnownServer { BaseUrl = url } })
            : MatrixHttp.Error(System.Net.HttpStatusCode.NotFound, ErrorCodes.NotFound, "No public base URL configured."));
        // Return routes
        return routes;
    }
}
