namespace Uniform.Matrix.Protocol.Core;

/// <summary>Base64 forms used by Matrix: unpadded for keys and ciphertexts, padded by some clients, URL-safe in JWKs; decoding accepts all.</summary>
public static class Base64
{
    /// <summary>Standard alphabet without padding.</summary>
    public static string EncodeUnpadded(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    /// <summary>URL-safe alphabet without padding, as in JWK <c>k</c>.</summary>
    public static string EncodeUrlSafe(ReadOnlySpan<byte> bytes) => EncodeUnpadded(bytes).Replace('+', '-').Replace('/', '_');

    /// <summary>Decode standard or URL-safe base64, with or without padding.</summary>
    public static byte[] Decode(string value)
    {
        // Normalize alphabet and padding
        string normalized = value.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        // Return bytes
        return Convert.FromBase64String(normalized);
    }
}
