namespace Uniform.Matrix.Protocol.Client;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

/// <summary>Client <see href="https://spec.matrix.org/latest/client-server-api/#server-discovery">server discovery</see> via well-known URI.</summary>
public static class HomeserverDiscovery
{
    /// <summary>Resolve the homeserver base URL for <paramref name="serverName"/>; falls back to <c>https://{serverName}/</c> when no well-known file exists.</summary>
    /// <param name="http">Client used for the well-known request.</param>
    /// <param name="serverName">Server part of a user ID, e.g. <c>matrix.org</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <example><code><![CDATA[Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, "matrix.org");]]></code></example>
    public static async Task<Uri> DiscoverAsync(HttpClient http, string serverName, CancellationToken cancellationToken = default)
    {
        // Fetch well-known file
        Uri wellKnown = new($"https://{serverName}/.well-known/matrix/client");
        using HttpResponseMessage response = await http.GetAsync(wellKnown, cancellationToken);

        // No well-known file, use server name as is
        if (response.StatusCode == HttpStatusCode.NotFound) return new Uri($"https://{serverName}/");
        response.EnsureSuccessStatusCode();

        // Parse body
        WellKnownClient? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<WellKnownClient>(MatrixJson.Options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid .well-known/matrix/client for {serverName}", ex);
        }

        // Validate base URL
        string? baseUrl = body?.Homeserver?.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)) throw new InvalidOperationException($"No valid m.homeserver.base_url for {serverName}");
        // Return base URL
        return uri;
    }
}
