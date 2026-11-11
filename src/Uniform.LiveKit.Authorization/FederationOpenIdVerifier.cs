namespace Uniform.LiveKit.Authorization;

using System.Net;
using System.Text.Json;
using Uniform.LiveKit.Protocol;

/// <summary>Verifies OpenID tokens over the federation API, <c>GET /_matrix/federation/v1/openid/userinfo</c>, at the address the server name delegates to by <c>/.well-known/matrix/server</c>, else at port 8448.</summary>
/// <see href="https://spec.matrix.org/v1.16/server-server-api/#openid"/>
/// <param name="http">The client of the requests.</param>
public sealed class FederationOpenIdVerifier(HttpClient http) : IOpenIdVerifier
{
    /// <summary>The default federation port.</summary>
    private const int FederationPort = 8448;

    /// <summary>Ask the token's homeserver who the token is of.</summary>
    public async Task<string?> VerifyAsync(OpenIdToken token, CancellationToken cancellationToken)
    {
        // Assert a token and a server
        if (string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.MatrixServerName)) return null;

        // The user of the token, from its homeserver
        string baseUrl = await ResolveAsync(token.MatrixServerName, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await http.GetAsync($"{baseUrl}/_matrix/federation/v1/openid/userinfo?access_token={Uri.EscapeDataString(token.AccessToken)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        // Return user
        return body.RootElement.TryGetProperty("sub", out JsonElement sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null;
    }

    /// <summary>Return the federation base URL of <paramref name="serverName"/>: as is with an explicit port; else its <c>m.server</c> delegation, at 8448 without a port; else the name at 8448.</summary>
    public async Task<string> ResolveAsync(string serverName, CancellationToken cancellationToken)
    {
        // An explicit port is used as is
        if (HasPort(serverName)) return $"https://{serverName}";

        // Delegation, when the server has one
        string? delegated = null;
        try
        {
            using HttpResponseMessage response = await http.GetAsync($"https://{serverName}/.well-known/matrix/server", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("m.server", out JsonElement server) && server.ValueKind == JsonValueKind.String) delegated = server.GetString();
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // On no delegation: the name itself
        }
        string host = string.IsNullOrWhiteSpace(delegated) ? serverName : delegated;
        // Return URL
        return HasPort(host) ? $"https://{host}" : $"https://{host}:{FederationPort}";
    }

    /// <summary>Return whether <paramref name="host"/> ends with a port, also after a bracketed IPv6 address.</summary>
    public static bool HasPort(string host)
    {
        // The colon after the IPv6 brackets, or the only one
        int colon = host.LastIndexOf(':');
        if (colon < 0) return false;
        if (host.StartsWith('[')) return colon > host.LastIndexOf(']');
        // Return whether one colon
        return host.IndexOf(':') == colon;
    }
}
