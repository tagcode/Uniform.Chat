namespace Uniform.Stun.Protocol;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>Time-limited TURN credentials from a secret shared by a TURN server and the service that hands them out, such as a Matrix homeserver's <c>/voip/turnServer</c>: the user name is <c>expiry:name</c>, with the expiry in Unix seconds, and the password is the Base64 HMAC-SHA1 of the user name with the secret.</summary>
/// <remarks>This is the scheme of the <see href="https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00">TURN REST API draft</see> that coturn's <c>use-auth-secret</c> and most Matrix homeservers use.</remarks>
public static class TurnCredentials
{
    /// <summary>Return a user name valid until <paramref name="expires"/> and its password.</summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="name">Who the credentials are for, e.g. a Matrix user ID; may not contain a colon only at its start.</param>
    /// <param name="expires">When the credentials stop working.</param>
    public static (string Username, string Password) Create(string secret, string name, DateTimeOffset expires)
    {
        // Expiry first, then the name
        string username = $"{expires.ToUnixTimeSeconds()}:{name}";
        // Return both
        return (username, Password(secret, username));
    }

    /// <summary>Return the password of a time-limited <paramref name="username"/>.</summary>
    public static string Password(string secret, string username) => Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));

    /// <summary>Return when a time-limited user name expires, or null when it does not start with a Unix time.</summary>
    public static DateTimeOffset? Expiry(string username)
    {
        // Digits before the first colon, or the whole name
        int colon = username.IndexOf(':');
        string seconds = colon < 0 ? username : username[..colon];
        if (!long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out long unix)) return null;
        // Return time, clamped to what DateTimeOffset holds
        return unix > 253402300799 ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeSeconds(unix);
    }
}
