namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using Uniform.Matrix.Protocol.Core;

/// <summary>Message content of <see href="https://spec.matrix.org/latest/client-server-api/#sending-encrypted-attachments">encrypted attachments</see>; upload and download are in Uniform.Matrix.Client.</summary>
public static class EncryptedAttachments
{
    /// <summary>Build the <see cref="EncryptedFile"/> of ciphertext uploaded to <paramref name="url"/>.</summary>
    public static EncryptedFile ToEncryptedFile(string url, AttachmentKeys keys) => new()
    {
        Url = url,
        Key = new JsonWebKey { Kty = "oct", KeyOps = ["encrypt", "decrypt"], Alg = "A256CTR", K = keys.Key, Ext = true },
        Iv = keys.Iv,
        Hashes = new Dictionary<string, string> { ["sha256"] = keys.Sha256 },
        V = "v2",
    };

    /// <summary>Read the keys of <paramref name="file"/>; throw <see cref="FormatException"/> for unsupported or incomplete files.</summary>
    public static AttachmentKeys GetKeys(EncryptedFile file)
    {
        // Assert supported format
        if (file.V is not ("v2" or null)) throw new FormatException($"Unsupported encrypted file version {file.V}.");
        if (file.Key is not { Alg: "A256CTR", K: string key }) throw new FormatException("Encrypted file has no A256CTR key.");
        if (file.Iv is not string iv || file.Hashes?.TryGetValue("sha256", out string? sha256) != true || sha256 is null) throw new FormatException("Encrypted file has no IV or SHA-256 hash.");
        // Return keys
        return new AttachmentKeys { Key = key, Iv = iv, Sha256 = sha256 };
    }
}
