namespace Uniform.LiveKit.Protocol;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The claims of a LiveKit access token: a JWT signed with HS256 by an API secret, whose issuer is the API key.</summary>
/// <see href="https://docs.livekit.io/home/get-started/authentication/"/>
public sealed record AccessToken
{
    /// <summary>The API key, which tells LiveKit which secret signed the token.</summary>
    [JsonPropertyName("iss")]
    public string Issuer { get; init; } = "";
    /// <summary>The participant's identity, unique in a room.</summary>
    [JsonPropertyName("sub"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identity { get; init; }
    /// <summary>Shown name of the participant.</summary>
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
    /// <summary>Start of validity, in Unix seconds.</summary>
    [JsonPropertyName("nbf")]
    public long NotBefore { get; init; }
    /// <summary>End of validity, in Unix seconds.</summary>
    [JsonPropertyName("exp")]
    public long Expires { get; init; }
    /// <summary>The room permissions.</summary>
    [JsonPropertyName("video"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoGrant? Video { get; init; }

    /// <summary>Create claims for <paramref name="identity"/> valid from <paramref name="now"/> for <paramref name="lifetime"/>.</summary>
    public static AccessToken Create(string apiKey, string? identity, VideoGrant video, DateTimeOffset now, TimeSpan lifetime) => new() { Issuer = apiKey, Identity = identity, Video = video, NotBefore = now.ToUnixTimeSeconds(), Expires = (now + lifetime).ToUnixTimeSeconds() };

    /// <summary>Return the claims as a JWT signed with <paramref name="apiSecret"/>.</summary>
    public string Sign(string apiSecret)
    {
        // Header and claims, then their signature
        string signed = $"{Base64Url(Header)}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(this))}";
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.ASCII.GetBytes(signed));
        // Return token
        return $"{signed}.{Base64Url(signature)}";
    }

    /// <summary>Read a JWT signed with HS256 by <paramref name="apiSecret"/> and valid at <paramref name="now"/>; return false for a bad signature, format or time.</summary>
    public static bool TryRead(string token, string apiSecret, DateTimeOffset now, out AccessToken? claims)
    {
        // Three parts, an HS256 header and a matching signature
        claims = null;
        string[] parts = token.Split('.');
        if (parts.Length != 3) return false;
        if (!TryDecode(parts[0], out byte[] header) || !TryDecode(parts[1], out byte[] body) || !TryDecode(parts[2], out byte[] signature)) return false;
        try
        {
            if (JsonDocument.Parse(header).RootElement.GetProperty("alg").GetString() != "HS256") return false;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return false;

        // Claims valid now
        try
        {
            claims = JsonSerializer.Deserialize<AccessToken>(body);
        }
        catch (JsonException)
        {
            return false;
        }
        long seconds = now.ToUnixTimeSeconds();
        // Return validity
        return claims is not null && claims.NotBefore <= seconds && seconds < claims.Expires;
    }

    /// <summary>The JWT header of HS256.</summary>
    private static readonly byte[] Header = """{"alg":"HS256","typ":"JWT"}"""u8.ToArray();

    /// <summary>Return <paramref name="data"/> in unpadded URL-safe Base64.</summary>
    private static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decode unpadded URL-safe Base64; return false when it is not.</summary>
    private static bool TryDecode(string text, out byte[] data)
    {
        // Padding back, standard alphabet
        string standard = text.Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - standard.Length % 4) % 4);
        data = new byte[standard.Length];
        if (!Convert.TryFromBase64String(standard, data, out int written)) return false;
        data = data[..written];
        // Done
        return true;
    }
}
