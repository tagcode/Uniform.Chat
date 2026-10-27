namespace Uniform.Matrix.Protocol.Server;

using System.Text;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>The <c>X-Matrix</c> <c>Authorization</c> header parameters of a server-to-server request.</summary>
/// <param name="Origin">Server that sent the request.</param>
/// <param name="Destination">Server the request is for, or null from servers that leave it out.</param>
/// <param name="KeyId">Signing key of <paramref name="Origin"/>, e.g. <c>ed25519:a_xyz</c>.</param>
/// <param name="Signature">Unpadded base64 signature.</param>
public sealed record XMatrixAuthorization(string Origin, string? Destination, string KeyId, string Signature)
{
    /// <summary>Return the header value, e.g. <c>X-Matrix origin="a.org",destination="b.org",key="ed25519:k",sig="..."</c>.</summary>
    public override string ToString() => Destination is null
        ? $"X-Matrix origin=\"{Origin}\",key=\"{KeyId}\",sig=\"{Signature}\""
        : $"X-Matrix origin=\"{Origin}\",destination=\"{Destination}\",key=\"{KeyId}\",sig=\"{Signature}\"";
}

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#request-authentication">Request authentication</see> between servers: the sender signs the method, URI, both server names and the JSON body, and puts the signature in an <c>X-Matrix</c> <c>Authorization</c> header.</summary>
/// <example><code><![CDATA[
/// XMatrixAuthorization header = FederationAuth.Sign("GET", "/_matrix/federation/v1/query/profile?user_id=%40bob%3Ab.org", "a.org", "b.org", null, keyId, seed);
/// bool valid = FederationAuth.Verify("GET", uri, header, "b.org", null, publicKeyOfA);
/// ]]></code></example>
public static class FederationAuth
{
    /// <summary>Return the authorization of a request.</summary>
    /// <param name="method">HTTP method, e.g. <c>PUT</c>.</param>
    /// <param name="uri">Path and query as sent, starting with <c>/_matrix/</c>.</param>
    /// <param name="origin">Sending server, this one.</param>
    /// <param name="destination">Receiving server.</param>
    /// <param name="content">JSON body, or null for requests without one.</param>
    /// <param name="keyId">Signing key ID.</param>
    /// <param name="secret">Ed25519 seed, base64.</param>
    public static XMatrixAuthorization Sign(string method, string uri, string origin, string destination, JsonNode? content, string keyId, string secret)
    {
        // Signature over the canonical request
        string signature = Ed25519.Sign(secret, SigningForm(method, uri, origin, destination, content));
        // Return header parameters
        return new XMatrixAuthorization(origin, destination, keyId, signature);
    }

    /// <summary>Return whether <paramref name="authorization"/> validly signs the request for <paramref name="serverName"/>, with the origin's key <paramref name="publicKey"/>.</summary>
    /// <param name="method">HTTP method as received.</param>
    /// <param name="uri">Path and query as received.</param>
    /// <param name="authorization">Parsed header.</param>
    /// <param name="serverName">This server; a header for another destination is not valid here.</param>
    /// <param name="content">JSON body, or null.</param>
    /// <param name="publicKey">Origin's public key of <see cref="XMatrixAuthorization.KeyId"/>.</param>
    public static bool Verify(string method, string uri, XMatrixAuthorization authorization, string serverName, JsonNode? content, string publicKey)
    {
        // Assert the destination is us
        if (authorization.Destination is string destination && destination != serverName) return false;
        // Return whether the signature matches
        return Ed25519.Verify(publicKey, SigningForm(method, uri, authorization.Origin, serverName, content), authorization.Signature);
    }

    /// <summary>Parse an <c>Authorization</c> header value; return null when it is not a complete <c>X-Matrix</c> one.</summary>
    /// <remarks>Parameter names are case-insensitive and values may be quoted or not, as the spec allows; unknown parameters are ignored.</remarks>
    public static XMatrixAuthorization? Parse(string? header)
    {
        // Assert the scheme
        const string scheme = "X-Matrix ";
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        // Comma-separated name=value pairs
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in SplitParameters(header[scheme.Length..]))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;
            string value = part[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            values.TryAdd(part[..equals].Trim(), value);
        }
        if (!values.TryGetValue("origin", out string? origin) || !values.TryGetValue("key", out string? key) || !values.TryGetValue("sig", out string? signature)) return null;
        // Return parameters
        return new XMatrixAuthorization(origin, values.GetValueOrDefault("destination"), key, signature);
    }

    /// <summary>Return the canonical JSON that is signed: method, uri, origin, destination and content when there is one.</summary>
    private static string SigningForm(string method, string uri, string origin, string destination, JsonNode? content)
    {
        // The request as JSON
        JsonObject request = new() { ["method"] = method, ["uri"] = uri, ["origin"] = origin, ["destination"] = destination };
        if (content is not null) request["content"] = content.DeepClone();
        // Return canonical text
        return CanonicalJson.SigningForm(request);
    }

    /// <summary>Split header parameters at commas outside quotes.</summary>
    private static IEnumerable<string> SplitParameters(string parameters)
    {
        // Track quotes and escapes
        StringBuilder current = new();
        bool quoted = false;
        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c == '\\' && quoted && i + 1 < parameters.Length)
            {
                current.Append(c).Append(parameters[++i]);
                continue;
            }
            if (c == '"') quoted = !quoted;
            if (c == ',' && !quoted)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        // Last parameter
        if (current.Length > 0) yield return current.ToString();
    }
}
