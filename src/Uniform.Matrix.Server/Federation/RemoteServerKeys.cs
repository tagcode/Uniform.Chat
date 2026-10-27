namespace Uniform.Matrix.Server.Federation;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Keys;

/// <summary>Other servers' <see href="https://spec.matrix.org/latest/server-server-api/#retrieving-server-keys">signing keys</see>, fetched from <c>/_matrix/key/v2/server</c> of each server and cached until their <c>valid_until_ts</c>, at most a week.</summary>
/// <remarks>Keys are kept in memory and fetched again after a restart. A response counts only when every key it lists has signed it. Notary servers are not asked yet.</remarks>
public sealed class RemoteServerKeys
{
    private static readonly TimeSpan MaxValidity = TimeSpan.FromDays(7);
    private readonly FederationClient federation;
    private readonly ServerKeys ownKeys;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly TimeProvider time;
    private readonly ILogger<RemoteServerKeys> logger;
    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, string> Keys, DateTimeOffset ValidUntil)> cache = new();

    /// <summary>Create the cache.</summary>
    public RemoteServerKeys(FederationClient federation, ServerKeys ownKeys, IOptionsMonitor<ServerOptions> server, TimeProvider time, ILogger<RemoteServerKeys> logger)
    {
        // Store dependencies
        this.federation = federation;
        this.ownKeys = ownKeys;
        this.server = server;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Return the public key <paramref name="keyId"/> of <paramref name="serverName"/>, fetching the server's keys when not cached; null when the server does not have it or cannot be reached.</summary>
    public async Task<string?> GetVerifyKeyAsync(string serverName, string keyId, CancellationToken cancellationToken)
    {
        // Own key without asking; valid cached keys next
        if (serverName == server.CurrentValue.ServerName) return keyId == ownKeys.KeyId ? ownKeys.PublicKey : null;
        if (cache.TryGetValue(serverName, out (IReadOnlyDictionary<string, string> Keys, DateTimeOffset ValidUntil) cached) && cached.ValidUntil > time.GetUtcNow() && cached.Keys.TryGetValue(keyId, out string? known)) return known;

        // Fetch from the server itself
        JsonObject? response;
        try
        {
            response = await federation.SendAsync(HttpMethod.Get, serverName, "/_matrix/key/v2/server", null, cancellationToken, signed: false) as JsonObject;
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Cannot fetch the keys of {ServerName}: {Error}", serverName, ex.Message);
            return null;
        }
        if (response is null || Validate(serverName, response) is not (IReadOnlyDictionary<string, string> keys, DateTimeOffset validUntil)) return null;
        cache[serverName] = (keys, validUntil);
        // Return key, if it was there
        return keys.GetValueOrDefault(keyId);
    }

    /// <summary>Return the verify keys of a key response and until when they hold, or null when it is not for <paramref name="serverName"/> or not signed by each of its keys.</summary>
    private (IReadOnlyDictionary<string, string> Keys, DateTimeOffset ValidUntil)? Validate(string serverName, JsonObject response)
    {
        // Assert the server and its keys
        if (response["server_name"]?.GetValue<string>() != serverName || response["verify_keys"] is not JsonObject verifyKeys || verifyKeys.Count == 0) return null;
        Dictionary<string, string> keys = [];
        string signingForm = CanonicalJson.SigningForm(response);
        foreach ((string keyId, JsonNode? entry) in verifyKeys)
        {
            if (entry?["key"]?.GetValue<string>() is not string key) return null;
            if (CanonicalJson.GetSignature(response, serverName, keyId) is not string signature || !Ed25519.Verify(key, signingForm, signature))
            {
                logger.LogWarning("Keys of {ServerName} are not signed by {KeyId}", serverName, keyId);
                return null;
            }
            keys[keyId] = key;
        }

        // Validity, at most a week from now
        long validUntilTs = response["valid_until_ts"]?.GetValue<long>() ?? 0;
        DateTimeOffset validUntil = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(validUntilTs, (time.GetUtcNow() + MaxValidity).ToUnixTimeMilliseconds()));
        // Return keys
        return (keys, validUntil);
    }
}
