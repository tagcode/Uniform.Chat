namespace Uniform.Turn;

using Uniform.Stun.Protocol;

/// <summary>Finds the keys of a user's <see href="https://www.rfc-editor.org/rfc/rfc8489#section-9.2">long-term credentials</see>, which the TURN server checks requests with.</summary>
public interface ITurnAuthenticator
{
    /// <summary>Return the keys <paramref name="username"/> may have in <paramref name="realm"/>, each MD5 of <c>username:realm:password</c>; none when the user is unknown or its credentials have expired. A request is accepted when its integrity matches any.</summary>
    IEnumerable<byte[]> GetKeys(string username, string realm);
}

/// <summary>Time-limited credentials from shared secrets, as Matrix homeservers hand them out: see <see cref="TurnCredentials"/>. Several secrets let one be replaced without cutting off credentials made with the other.</summary>
/// <param name="secrets">The shared secrets; any of them may have made the password.</param>
/// <param name="time">Clock for the expiry.</param>
public sealed class SharedSecretAuthenticator(IReadOnlyList<string> secrets, TimeProvider time) : ITurnAuthenticator
{
    /// <summary>Return the key each secret gives <paramref name="username"/>, or none when it has expired or has no expiry.</summary>
    public IEnumerable<byte[]> GetKeys(string username, string realm)
    {
        // Assert an expiry in the future
        if (TurnCredentials.Expiry(username) is not DateTimeOffset expiry || expiry < time.GetUtcNow()) yield break;
        // Return a key per secret
        foreach (string secret in secrets) yield return StunMessage.LongTermKey(username, realm, TurnCredentials.Password(secret, username));
    }
}

/// <summary>Fixed users with passwords, e.g. from settings, for TURN servers without a service that hands out time-limited credentials.</summary>
/// <param name="passwords">Passwords by user name.</param>
public sealed class StaticUserAuthenticator(IReadOnlyDictionary<string, string> passwords) : ITurnAuthenticator
{
    /// <summary>Return the key of a known user, or none.</summary>
    public IEnumerable<byte[]> GetKeys(string username, string realm) => passwords.TryGetValue(username, out string? password) ? [StunMessage.LongTermKey(username, realm, password)] : [];
}

/// <summary>Tries several authenticators in order, e.g. shared secrets and fixed users together.</summary>
/// <param name="authenticators">The authenticators.</param>
public sealed class CombinedAuthenticator(IReadOnlyList<ITurnAuthenticator> authenticators) : ITurnAuthenticator
{
    /// <summary>Return the keys of every authenticator, in order.</summary>
    public IEnumerable<byte[]> GetKeys(string username, string realm) => authenticators.SelectMany(a => a.GetKeys(username, realm));
}
