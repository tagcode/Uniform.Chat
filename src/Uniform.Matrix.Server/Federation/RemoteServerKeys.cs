namespace Uniform.Matrix.Server.Federation;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Storage;

/// <summary>Other servers' <see href="https://spec.matrix.org/latest/server-server-api/#retrieving-server-keys">signing keys</see>: fetched from each server's <c>/_matrix/key/v2/server</c>, else from the <see cref="FederationOptions.TrustedKeyServers"/>, and kept in the database until their <c>valid_until_ts</c>, at most a week.</summary>
/// <remarks>An answer counts only when every key it lists has signed it, and an answer from a key server also needs that server's signature. Old keys (<c>old_verify_keys</c>) are accepted too, as events signed with them stay valid. When a server and the key servers cannot be reached, its last kept answer is used even after it expired, as refusing every event of a server that is down helps no one; such a server is not asked again for a few minutes, and each server is asked once at a time.</remarks>
public sealed class RemoteServerKeys
{
    private static readonly TimeSpan MaxValidity = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryUnreachable = TimeSpan.FromMinutes(5);
    private const int ParallelFetches = 20;
    private const int NotaryBatch = 100;
    private readonly FederationClient federation;
    private readonly ServerKeys ownKeys;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly IOptionsMonitor<ServerOptions> server;
    private readonly IOptionsMonitor<FederationOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<RemoteServerKeys> logger;
    private readonly ConcurrentDictionary<string, KeptKeys> cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<KeptKeys?>>> fetching = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> unreachable = new();

    /// <summary>A server's keys, current and old, until when they hold, and the answer they came in.</summary>
    private sealed record KeptKeys(IReadOnlyDictionary<string, string> Keys, DateTimeOffset ValidUntil, JsonObject Response);

    /// <summary>Create the cache.</summary>
    public RemoteServerKeys(FederationClient federation, ServerKeys ownKeys, IDbContextFactory<ServerDbContext> contexts, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<FederationOptions> options, TimeProvider time, ILogger<RemoteServerKeys> logger)
    {
        // Store dependencies
        this.federation = federation;
        this.ownKeys = ownKeys;
        this.contexts = contexts;
        this.server = server;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Return the public key <paramref name="keyId"/> of <paramref name="serverName"/>, fetching the server's keys when not known or expired; null when the server does not have it or no answer can be had.</summary>
    public async Task<string?> GetVerifyKeyAsync(string serverName, string keyId, CancellationToken cancellationToken)
    {
        // Own key without asking; valid keys from memory or the database next
        if (serverName == server.CurrentValue.ServerName) return keyId == ownKeys.KeyId ? ownKeys.PublicKey : null;
        KeptKeys? kept = await GetKeptAsync(serverName, cancellationToken);
        if (kept is not null && kept.ValidUntil > time.GetUtcNow() && kept.Keys.TryGetValue(keyId, out string? known)) return known;

        // A fresh answer, else the kept one even when expired
        if (await FetchAsync(serverName, cancellationToken) is KeptKeys fresh) return fresh.Keys.GetValueOrDefault(keyId);
        if (kept?.Keys.TryGetValue(keyId, out string? stale) == true)
        {
            logger.LogInformation("Using expired keys of {ServerName}, which cannot be reached", serverName);
            return stale;
        }
        // Return none
        return null;
    }

    /// <summary>Fetch the keys of <paramref name="serverNames"/> that are not kept or have expired, e.g. before checking the many events of a joined room: from the <see cref="FederationOptions.TrustedKeyServers"/> in batches first, then from each remaining server, several at a time.</summary>
    public async Task PrefetchAsync(IEnumerable<string> serverNames, CancellationToken cancellationToken)
    {
        // Other servers only, each once, without valid kept keys
        string own = server.CurrentValue.ServerName;
        List<string> names = [];
        foreach (string name in serverNames.Where(n => n != own).Distinct())
        {
            KeptKeys? kept = await GetKeptAsync(name, cancellationToken);
            if (kept is null || kept.ValidUntil <= time.GetUtcNow()) names.Add(name);
        }

        // Key servers first, many servers per request, as one answer is faster than asking each server, some of which are down
        foreach (string notary in options.CurrentValue.GetTrustedKeyServers().Where(n => n != own))
        {
            foreach (string[] batch in names.Where(n => n != notary).Chunk(NotaryBatch).ToList())
            {
                foreach ((string name, JsonObject response) in await AskNotaryAsync(notary, batch, cancellationToken))
                {
                    if (await KeepAsync(name, response, cancellationToken) is not null) names.Remove(name);
                }
            }
        }
        // Done when each has an answer or failed
        // The rest from each server
        await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = ParallelFetches, CancellationToken = cancellationToken }, async (name, token) => await FetchAsync(name, token));
    }

    /// <summary>Return the kept, validated key answer of <paramref name="serverName"/>, fetching it when there is none or it expired; null when none can be had. Used to answer <c>/_matrix/key/v2/query</c> as a key server.</summary>
    public async Task<JsonObject?> GetResponseAsync(string serverName, CancellationToken cancellationToken)
    {
        // Kept and valid, else fresh, else kept
        KeptKeys? kept = await GetKeptAsync(serverName, cancellationToken);
        if (kept is not null && kept.ValidUntil > time.GetUtcNow()) return (JsonObject)kept.Response.DeepClone();
        KeptKeys? fresh = await FetchAsync(serverName, cancellationToken);
        // Return answer
        return (JsonObject?)(fresh ?? kept)?.Response.DeepClone();
    }

    /// <summary>Return the keys of a server from memory or the database, or null.</summary>
    private async Task<KeptKeys?> GetKeptAsync(string serverName, CancellationToken cancellationToken)
    {
        // Memory first
        if (cache.TryGetValue(serverName, out KeptKeys? cached)) return cached;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        ServerKeyResponseRow? row = await db.ServerKeyResponses.AsNoTracking().SingleOrDefaultAsync(k => k.ServerName == serverName, cancellationToken);
        if (row is null || JsonNode.Parse(row.Json) is not JsonObject response || Validate(serverName, response) is not (IReadOnlyDictionary<string, string>, DateTimeOffset) valid) return null;
        KeptKeys entry = new(valid.Keys, DateTimeOffset.FromUnixTimeMilliseconds(row.ValidUntilTs), response);
        cache[serverName] = entry;
        // Return kept keys
        return entry;
    }

    /// <summary>Fetch a server's keys as <see cref="FetchOnceAsync"/> does, sharing a fetch already under way; null without asking when the server could not be reached a moment ago.</summary>
    private async Task<KeptKeys?> FetchAsync(string serverName, CancellationToken cancellationToken)
    {
        // Assert not recently unreachable
        if (unreachable.TryGetValue(serverName, out DateTimeOffset retryAt) && retryAt > time.GetUtcNow()) return null;
        // Return the shared fetch, which runs to its own time limit even when this caller stops waiting
        Lazy<Task<KeptKeys?>> fetch = fetching.GetOrAdd(serverName, name => new Lazy<Task<KeptKeys?>>(() => FetchOnceAsync(name)));
        return await fetch.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Fetch a server's keys from it, else from the key servers; keep and return them, or null, remembering for a while that the server could not be reached.</summary>
    private async Task<KeptKeys?> FetchOnceAsync(string serverName)
    {
        // Ask, then let the next caller fetch again
        try
        {
            KeptKeys? fetched = await FetchFromServersAsync(serverName, CancellationToken.None);
            if (fetched is null) unreachable[serverName] = time.GetUtcNow() + RetryUnreachable;
            else unreachable.TryRemove(serverName, out _);
            // Return keys
            return fetched;
        }
        finally
        {
            fetching.TryRemove(serverName, out _);
        }
    }

    /// <summary>Fetch a server's keys from it, else from the key servers; keep and return them, or null.</summary>
    private async Task<KeptKeys?> FetchFromServersAsync(string serverName, CancellationToken cancellationToken)
    {
        // The server itself
        JsonObject? response = null;
        try
        {
            response = await federation.SendAsync(HttpMethod.Get, serverName, "/_matrix/key/v2/server", null, cancellationToken, signed: false, timeout: options.CurrentValue.KeyRequestTimeout) as JsonObject;
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Cannot fetch the keys of {ServerName}: {Error}", serverName, ex.Message);
        }

        // Key servers, each until one answers
        if (response is null || Validate(serverName, response) is null)
        {
            response = null;
            foreach (string notary in options.CurrentValue.GetTrustedKeyServers().Where(n => n != serverName && n != server.CurrentValue.ServerName))
            {
                response = (await AskNotaryAsync(notary, [serverName], cancellationToken)).GetValueOrDefault(serverName);
                if (response is not null) break;
            }
        }
        // Return kept keys, or none
        return response is null ? null : await KeepAsync(serverName, response, cancellationToken);
    }

    /// <summary>Keep a key answer of <paramref name="serverName"/> in memory and the database when valid; return the keys, or null.</summary>
    private async Task<KeptKeys?> KeepAsync(string serverName, JsonObject response, CancellationToken cancellationToken)
    {
        // Assert valid
        if (Validate(serverName, response) is not (IReadOnlyDictionary<string, string>, DateTimeOffset) valid) return null;

        // Kept in memory, then the database, where a fetch running beside this one may have stored it first
        KeptKeys entry = new(valid.Keys, valid.ValidUntil, response);
        cache[serverName] = entry;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        ServerKeyResponseRow? row = await db.ServerKeyResponses.SingleOrDefaultAsync(k => k.ServerName == serverName, cancellationToken);
        if (row is null) db.ServerKeyResponses.Add(row = new ServerKeyResponseRow { ServerName = serverName, Json = "" });
        row.Json = response.ToJsonString();
        row.ValidUntilTs = valid.ValidUntil.ToUnixTimeMilliseconds();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogDebug("Keys of {ServerName} were stored meanwhile: {Error}", serverName, ex.Message);
        }
        // Return keys
        return entry;
    }

    /// <summary>Return the answers of key server <paramref name="notary"/> about <paramref name="serverNames"/> by server, those the key server signed.</summary>
    private async Task<Dictionary<string, JsonObject>> AskNotaryAsync(string notary, IReadOnlyCollection<string> serverNames, CancellationToken cancellationToken)
    {
        // Query for the servers' keys
        JsonNode? answer;
        Dictionary<string, JsonObject> answers = [];
        try
        {
            JsonObject query = [];
            foreach (string serverName in serverNames) query[serverName] = new JsonObject();
            answer = await federation.SendAsync(HttpMethod.Post, notary, "/_matrix/key/v2/query", new JsonObject { ["server_keys"] = query }, cancellationToken, signed: false, timeout: options.CurrentValue.KeyRequestTimeout);
        }
        catch (MatrixException ex)
        {
            logger.LogInformation("Key server {Notary} cannot give the keys of {Count} servers: {Error}", notary, serverNames.Count, ex.Message);
            return answers;
        }

        // Entries of the asked servers, signed by the key server too
        foreach (JsonObject entry in (answer?["server_keys"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (entry["server_name"]?.GetValue<string>() is not string serverName || !serverNames.Contains(serverName) || answers.ContainsKey(serverName)) continue;
            foreach (string keyId in (entry["signatures"]?[notary] as JsonObject ?? []).Select(k => k.Key))
            {
                if (await GetVerifyKeyAsync(notary, keyId, cancellationToken) is not string key || CanonicalJson.GetSignature(entry, notary, keyId) is not string signature || !Ed25519.Verify(key, CanonicalJson.SigningForm(entry), signature)) continue;
                answers[serverName] = entry;
                break;
            }
        }
        // Return answers
        return answers;
    }

    /// <summary>Return the keys of a key answer, current and old, and until when they hold, or null when it is not for <paramref name="serverName"/> or not signed by each of its current keys.</summary>
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

        // Old keys, which signed earlier events
        foreach ((string keyId, JsonNode? entry) in response["old_verify_keys"] as JsonObject ?? [])
        {
            if (entry?["key"]?.GetValue<string>() is string key) keys.TryAdd(keyId, key);
        }

        // Validity, at most a week from now
        long validUntilTs = response["valid_until_ts"]?.GetValue<long>() ?? 0;
        DateTimeOffset validUntil = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(validUntilTs, (time.GetUtcNow() + MaxValidity).ToUnixTimeMilliseconds()));
        // Return keys
        return (keys, validUntil);
    }
}
