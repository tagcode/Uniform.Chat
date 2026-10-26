namespace Uniform.Matrix.Protocol.Crypto;

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Uniform.Matrix.Protocol.Core;

/// <summary>AES-256 in counter mode with a 128-bit big-endian counter, built from ECB as .NET has no CTR mode.</summary>
public static class AesCtr
{
    /// <summary>Encrypt or decrypt <paramref name="data"/>; the operation is its own inverse.</summary>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        // Key and starting counter
        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        byte[] counter = iv.ToArray();
        byte[] keystream = new byte[16];
        byte[] output = new byte[data.Length];

        // XOR each block with the encrypted counter
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int length = Math.Min(16, data.Length - offset);
            for (int i = 0; i < length; i++) output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            Increment(counter);
        }
        // Return result
        return output;
    }

    /// <summary>Add one to a big-endian counter.</summary>
    private static void Increment(byte[] counter)
    {
        // Carry from the last byte
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) break;
        }
    }
}

/// <summary>Key and hash of an <see href="https://spec.matrix.org/latest/client-server-api/#sending-encrypted-attachments">encrypted attachment</see>, the <c>file</c> object of a message without its URL.</summary>
public sealed record AttachmentKeys
{
    /// <summary>AES-256 key, base64url.</summary>
    public required string Key { get; init; }
    /// <summary>Counter block, base64.</summary>
    public required string Iv { get; init; }
    /// <summary>SHA-256 of the ciphertext, base64.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>Encryption of <see href="https://spec.matrix.org/latest/client-server-api/#sending-encrypted-attachments">attachments</see> in encrypted rooms: AES-256-CTR, then SHA-256 of the ciphertext.</summary>
public static class AttachmentCrypto
{
    /// <summary>Encrypt a file; upload the ciphertext as <c>application/octet-stream</c> and send the keys in the message.</summary>
    public static (byte[] Ciphertext, AttachmentKeys Keys) Encrypt(ReadOnlySpan<byte> plaintext)
    {
        // Random key; random high 64 bits of the counter, low 64 bits zero
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv.AsSpan(0, 8));
        byte[] ciphertext = AesCtr.Transform(key, iv, plaintext);

        // Return ciphertext with keys
        return (ciphertext, new AttachmentKeys { Key = Base64.EncodeUrlSafe(key), Iv = Base64.EncodeUnpadded(iv), Sha256 = Base64.EncodeUnpadded(SHA256.HashData(ciphertext)) });
    }

    /// <summary>Check the hash, then decrypt; throw <see cref="CryptographicException"/> when the file was changed.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, AttachmentKeys keys)
    {
        // Verify before decrypting
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(ciphertext), Base64.Decode(keys.Sha256))) throw new CryptographicException("Attachment hash does not match; the file was changed.");
        // Return plaintext
        return AesCtr.Transform(Base64.Decode(keys.Key), Base64.Decode(keys.Iv), ciphertext);
    }
}

/// <summary>Secret as stored in <see href="https://spec.matrix.org/latest/client-server-api/#secret-storage">secret storage</see> account data: <c>iv</c>, <c>ciphertext</c> and <c>mac</c>.</summary>
/// <param name="Iv">Counter block, base64.</param>
/// <param name="Ciphertext">AES-256-CTR ciphertext, base64.</param>
/// <param name="Mac">HMAC-SHA-256 of the ciphertext, base64.</param>
public sealed record EncryptedSecret(string Iv, string Ciphertext, string Mac);

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#msecret_storagev1aes-hmac-sha2">m.secret_storage.v1.aes-hmac-sha2</see> encryption and the recovery key format.</summary>
public static class SecretStorageCrypto
{
    /// <summary>Algorithm name of the secret storage keys this class handles.</summary>
    public const string Algorithm = "m.secret_storage.v1.aes-hmac-sha2";

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly byte[] RecoveryKeyPrefix = [0x8B, 0x01];

    /// <summary>Encrypt <paramref name="plaintext"/> under <paramref name="name"/>, e.g. <c>m.cross_signing.master</c>.</summary>
    public static EncryptedSecret Encrypt(ReadOnlySpan<byte> key, string name, ReadOnlySpan<byte> plaintext)
    {
        // Keys for this secret name; bit 63 of the counter cleared, as the spec requires
        (byte[] aesKey, byte[] macKey) = DeriveKeys(key, name);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        iv[8] &= 0x7F;

        // Encrypt, then MAC the ciphertext
        byte[] ciphertext = AesCtr.Transform(aesKey, iv, plaintext);
        byte[] mac = HMACSHA256.HashData(macKey, ciphertext);
        // Return padded base64, as written by other clients
        return new EncryptedSecret(Convert.ToBase64String(iv), Convert.ToBase64String(ciphertext), Convert.ToBase64String(mac));
    }

    /// <summary>Encrypt a text secret, such as a base64 private key.</summary>
    public static EncryptedSecret Encrypt(ReadOnlySpan<byte> key, string name, string secret) => Encrypt(key, name, Encoding.UTF8.GetBytes(secret));

    /// <summary>Verify the MAC and decrypt; throw <see cref="CryptographicException"/> for a wrong key or changed data.</summary>
    public static byte[] DecryptBytes(ReadOnlySpan<byte> key, string name, EncryptedSecret secret)
    {
        // Keys for this secret name
        (byte[] aesKey, byte[] macKey) = DeriveKeys(key, name);
        byte[] ciphertext = Base64.Decode(secret.Ciphertext);

        // Verify, then decrypt
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(macKey, ciphertext), Base64.Decode(secret.Mac))) throw new CryptographicException("Wrong secret storage key, or the secret was changed.");
        // Return plaintext
        return AesCtr.Transform(aesKey, Base64.Decode(secret.Iv), ciphertext);
    }

    /// <summary>Decrypt a text secret.</summary>
    public static string Decrypt(ReadOnlySpan<byte> key, string name, EncryptedSecret secret) => Encoding.UTF8.GetString(DecryptBytes(key, name, secret));

    /// <summary>Return the <c>iv</c> and <c>mac</c> that prove knowledge of <paramref name="key"/>: 32 zero bytes encrypted under the empty name.</summary>
    public static (string Iv, string Mac) CreateKeyCheck(ReadOnlySpan<byte> key)
    {
        // Encrypt zeros
        EncryptedSecret check = Encrypt(key, "", new byte[32]);
        // Return check values
        return (check.Iv, check.Mac);
    }

    /// <summary>Return whether <paramref name="key"/> matches a key description's <c>iv</c> and <c>mac</c>.</summary>
    public static bool CheckKey(ReadOnlySpan<byte> key, string iv, string mac)
    {
        // Re-encrypt zeros with the stored counter and compare MACs
        (byte[] aesKey, byte[] macKey) = DeriveKeys(key, "");
        byte[] ciphertext = AesCtr.Transform(aesKey, Base64.Decode(iv), new byte[32]);
        // Return comparison
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(macKey, ciphertext), Base64.Decode(mac));
    }

    /// <summary>Derive a key from a passphrase with PBKDF2-SHA-512, as in <c>m.pbkdf2</c> passphrase settings.</summary>
    public static byte[] KeyFromPassphrase(string passphrase, string salt, int iterations, int bits = 256) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), Encoding.UTF8.GetBytes(salt), iterations, HashAlgorithmName.SHA512, bits / 8);

    /// <summary>Format a 32-byte key as a <see href="https://spec.matrix.org/latest/client-server-api/#key-representation">recovery key</see>: prefix, key and parity in base58, in groups of four.</summary>
    /// <example><code><![CDATA[string shown = SecretStorageCrypto.EncodeRecoveryKey(key); // "EsTc 5rr1 4mpm ..."]]></code></example>
    public static string EncodeRecoveryKey(ReadOnlySpan<byte> key)
    {
        // Prefix, key, parity of all bytes
        byte[] bytes = [.. RecoveryKeyPrefix, .. key, 0];
        bytes[^1] = bytes[..^1].Aggregate((byte)0, (parity, b) => (byte)(parity ^ b));
        // Return base58 in groups of four
        return string.Join(' ', EncodeBase58(bytes).Chunk(4).Select(c => new string(c)));
    }

    /// <summary>Parse a recovery key, ignoring spaces; throw <see cref="FormatException"/> when the prefix, length or parity is wrong.</summary>
    public static byte[] DecodeRecoveryKey(string recoveryKey)
    {
        // Decode base58 without spaces
        byte[] bytes = DecodeBase58(string.Concat(recoveryKey.Where(c => !char.IsWhiteSpace(c))));
        if (bytes.Length != 35 || bytes[0] != RecoveryKeyPrefix[0] || bytes[1] != RecoveryKeyPrefix[1]) throw new FormatException("Not a recovery key.");
        if (bytes.Aggregate((byte)0, (parity, b) => (byte)(parity ^ b)) != 0) throw new FormatException("Recovery key has a typo (parity check failed).");
        // Return key bytes
        return bytes[2..34];
    }

    /// <summary>Derive the AES and HMAC keys of a secret with HKDF-SHA-256, zero salt and the name as info.</summary>
    private static (byte[] AesKey, byte[] MacKey) DeriveKeys(ReadOnlySpan<byte> key, string name)
    {
        // 64 bytes: AES key, then HMAC key
        byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), 64, new byte[32], Encoding.UTF8.GetBytes(name));
        // Return halves
        return (derived[..32], derived[32..]);
    }

    /// <summary>Encode bytes in Bitcoin base58, keeping leading zero bytes as '1'.</summary>
    private static string EncodeBase58(byte[] bytes)
    {
        // Big number to base 58 digits
        BigInteger value = new(bytes, isUnsigned: true, isBigEndian: true);
        StringBuilder result = new();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out BigInteger remainder);
            result.Insert(0, Base58Alphabet[(int)remainder]);
        }
        // Return with leading zeros
        return new string('1', bytes.TakeWhile(b => b == 0).Count()) + result;
    }

    /// <summary>Decode Bitcoin base58.</summary>
    private static byte[] DecodeBase58(string text)
    {
        // Digits to big number
        BigInteger value = BigInteger.Zero;
        foreach (char c in text)
        {
            int digit = Base58Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"'{c}' is not a base58 character.");
            value = value * 58 + digit;
        }
        // Return bytes with leading zeros
        byte[] body = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[text.TakeWhile(c => c == '1').Count()], .. body];
    }
}
