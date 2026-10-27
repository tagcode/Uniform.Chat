namespace Uniform.Matrix.Server.Accounts;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#openid">OpenID tokens</see>: short-lived tokens a user hands to a service, which asks this server who they belong to through <c>/_matrix/federation/v1/openid/userinfo</c>.</summary>
/// <remarks>Tokens are kept in memory; they hold for <see cref="CallOptions.OpenIdTokenLifetime"/> and are lost on restart, after which clients ask for new ones.</remarks>
public sealed class OpenIdTokens
{
    private readonly IOptionsMonitor<CallOptions> options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, (string UserId, DateTimeOffset Expires)> tokens = new();

    /// <summary>Create the store.</summary>
    public OpenIdTokens(IOptionsMonitor<CallOptions> options, TimeProvider time)
    {
        // Store dependencies
        this.options = options;
        this.time = time;
    }

    /// <summary>Return a new token of <paramref name="userId"/> and how long it holds.</summary>
    public (string Token, TimeSpan Lifetime) Issue(string userId)
    {
        // Forget expired tokens, then a random one
        DateTimeOffset now = time.GetUtcNow();
        foreach ((string old, (string, DateTimeOffset Expires) entry) in tokens) if (entry.Expires <= now) tokens.TryRemove(old, out _);
        string token = Base64.EncodeUrlSafe(RandomNumberGenerator.GetBytes(32));
        TimeSpan lifetime = options.CurrentValue.OpenIdTokenLifetime;
        tokens[token] = (userId, now + lifetime);
        // Return token
        return (token, lifetime);
    }

    /// <summary>Return the user of a valid token, or null.</summary>
    public string? Validate(string? token) => token is not null && tokens.TryGetValue(token, out (string UserId, DateTimeOffset Expires) entry) && entry.Expires > time.GetUtcNow() ? entry.UserId : null;
}
