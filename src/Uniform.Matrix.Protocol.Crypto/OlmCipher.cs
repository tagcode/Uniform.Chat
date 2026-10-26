// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text;

/// <summary>The cipher of Olm, Megolm and pickles: keys expanded with HKDF-SHA-256, AES-256-CBC with PKCS#7 padding, and HMAC-SHA-256.</summary>
/// <remarks>Ported from vodozemac's <c>cipher</c> module; the 80 expanded bytes are the AES key, the HMAC key and the IV, in that order.</remarks>
internal sealed class OlmCipher : IDisposable
{
    /// <summary>Length of a full MAC.</summary>
    public const int MacLength = 32;
    /// <summary>Length of the truncated MAC that Olm and Megolm messages carry.</summary>
    public const int TruncatedMacLength = 8;

    private readonly byte[] aesKey;
    private readonly byte[] macKey;
    private readonly byte[] iv;

    /// <summary>Expand <paramref name="secret"/> with HKDF under <paramref name="info"/> and a one-byte zero salt.</summary>
    private OlmCipher(ReadOnlySpan<byte> secret, string info)
    {
        // 80 bytes: AES key, HMAC key, IV
        byte[] expanded = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), 80, [0], Encoding.ASCII.GetBytes(info));
        aesKey = expanded[..32];
        macKey = expanded[32..64];
        iv = expanded[64..80];
        CryptographicOperations.ZeroMemory(expanded);
    }

    /// <summary>Cipher of an Olm message key.</summary>
    public static OlmCipher ForOlm(ReadOnlySpan<byte> messageKey) => new(messageKey, "OLM_KEYS");

    /// <summary>Cipher of a 128-byte Megolm ratchet state.</summary>
    public static OlmCipher ForMegolm(ReadOnlySpan<byte> ratchet) => new(ratchet, "MEGOLM_KEYS");

    /// <summary>Cipher of a pickle key.</summary>
    public static OlmCipher ForPickle(ReadOnlySpan<byte> pickleKey) => new(pickleKey, "Pickle");

    /// <summary>Cipher of a backup's X25519 shared secret, expanded with empty info.</summary>
    public static OlmCipher ForPk(ReadOnlySpan<byte> sharedSecret) => new(sharedSecret, "");

    /// <summary>Encrypt with AES-256-CBC and PKCS#7 padding.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        // CBC with the derived IV
        using Aes aes = Aes.Create();
        aes.Key = aesKey;
        // Return ciphertext
        return aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
    }

    /// <summary>Decrypt AES-256-CBC; throw <see cref="OlmException"/> for bad padding.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        // CBC with the derived IV
        using Aes aes = Aes.Create();
        aes.Key = aesKey;
        try
        {
            return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            // Done; corrupt or wrong key
            throw new OlmException("Failed decrypting, invalid padding.");
        }
    }

    /// <summary>Return the full HMAC-SHA-256 of <paramref name="message"/>.</summary>
    public byte[] Mac(ReadOnlySpan<byte> message) => HMACSHA256.HashData(macKey, message);

    /// <summary>Return whether <paramref name="tag"/> equals the first bytes of the MAC of <paramref name="message"/>, in constant time.</summary>
    public bool VerifyTruncatedMac(ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag) =>
        tag.Length > 0 && tag.Length <= MacLength && CryptographicOperations.FixedTimeEquals(Mac(message).AsSpan(0, tag.Length), tag);

    /// <summary>Encrypt a pickle: ciphertext followed by the truncated MAC of the ciphertext.</summary>
    public byte[] EncryptPickle(ReadOnlySpan<byte> plaintext)
    {
        // Encrypt, then MAC
        byte[] ciphertext = Encrypt(plaintext);
        // Return ciphertext and MAC
        return [.. ciphertext, .. Mac(ciphertext).AsSpan(0, TruncatedMacLength)];
    }

    /// <summary>Decrypt a pickle; throw <see cref="OlmException"/> for a wrong key or changed data.</summary>
    public byte[] DecryptPickle(ReadOnlySpan<byte> data)
    {
        // Verify, then decrypt
        if (data.Length < TruncatedMacLength + 1) throw new OlmException("The pickle is too short.");
        ReadOnlySpan<byte> ciphertext = data[..^TruncatedMacLength];
        if (!VerifyTruncatedMac(ciphertext, data[^TruncatedMacLength..])) throw new OlmException("The pickle key is wrong, or the pickle was changed.");
        // Return plaintext
        return Decrypt(ciphertext);
    }

    /// <summary>Wipe the keys.</summary>
    public void Dispose()
    {
        // Clear secrets
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(macKey);
        CryptographicOperations.ZeroMemory(iv);
    }
}

/// <summary>An Olm or Megolm operation failed, e.g. a message that does not decrypt or a wrong pickle key.</summary>
public sealed class OlmException(string message) : Exception(message);
