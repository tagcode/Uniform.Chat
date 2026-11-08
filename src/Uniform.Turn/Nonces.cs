namespace Uniform.Turn;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

/// <summary>Stateless nonces of long-term credentials: the time they were made and a MAC of it with the client's address, so the server keeps nothing per client and a nonce works only for the client it was given to, until <c>lifetime</c> has passed.</summary>
/// <param name="time">Clock of the nonces' age.</param>
/// <param name="lifetime">How long a nonce is accepted.</param>
internal sealed class Nonces(TimeProvider time, TimeSpan lifetime)
{
    /// <summary>Key of the MACs, new each start, so nonces of an earlier run are stale.</summary>
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>Return a new nonce for <paramref name="client"/>.</summary>
    public string Create(IPEndPoint client)
    {
        // Time in hex, then the MAC
        string stamp = time.GetUtcNow().ToUnixTimeSeconds().ToString("x", CultureInfo.InvariantCulture);
        // Return nonce
        return $"{stamp}-{Mac(stamp, client)}";
    }

    /// <summary>Return whether <paramref name="nonce"/> was made for <paramref name="client"/> and is still fresh.</summary>
    public bool IsValid(string nonce, IPEndPoint client)
    {
        // Assert the form, then the MAC and the age
        int dash = nonce.IndexOf('-');
        if (dash <= 0 || !long.TryParse(nonce.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long seconds)) return false;
        string stamp = nonce[..dash];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(nonce[(dash + 1)..]), Encoding.ASCII.GetBytes(Mac(stamp, client)))) return false;
        TimeSpan age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        // Return freshness
        return age >= TimeSpan.Zero && age < lifetime;
    }

    /// <summary>Return the MAC of a time and a client, in hex.</summary>
    private string Mac(string stamp, IPEndPoint client) => Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{stamp}|{client}")), 0, 12);
}
