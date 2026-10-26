// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text;

/// <summary>Standalone Ed25519 keys, e.g. for cross-signing; keys and signatures are unpadded base64.</summary>
public static class Ed25519
{
    /// <summary>Create a random key; return the 32-byte secret seed and the public key, both base64.</summary>
    public static (string Secret, string PublicKey) GenerateKey()
    {
        // Random seed
        byte[] seed = Curve.NewSecret();
        (string, string) key = (OlmBase64.Encode(seed), OlmBase64.Encode(Curve.Ed25519PublicKey(seed)));
        CryptographicOperations.ZeroMemory(seed);
        // Return key pair
        return key;
    }

    /// <summary>Return the public key of a base64 secret seed; throw <see cref="OlmException"/> when it is not one.</summary>
    public static string GetPublicKey(string secret) => OlmBase64.Encode(Curve.Ed25519PublicKey(OlmBase64.DecodeKey(secret)));

    /// <summary>Sign UTF-8 <paramref name="message"/> with a base64 secret seed; return the signature in base64.</summary>
    public static string Sign(string secret, string message) => OlmBase64.Encode(Curve.Ed25519Sign(OlmBase64.DecodeKey(secret), Encoding.UTF8.GetBytes(message)));

    /// <summary>Return whether <paramref name="signature"/> by <paramref name="publicKey"/> is valid for UTF-8 <paramref name="message"/>; malformed input is invalid.</summary>
    public static bool Verify(string publicKey, string message, string signature)
    {
        // Malformed base64 or keys do not verify
        try
        {
            return Curve.Ed25519Verify(OlmBase64.Decode(publicKey), Encoding.UTF8.GetBytes(message), OlmBase64.Decode(signature));
        }
        catch (OlmException)
        {
            // Done; not a signature
            return false;
        }
    }
}

/// <summary>Ciphertext of <see cref="PkEncryption"/> as stored in a key backup's <c>session_data</c>.</summary>
/// <param name="Ciphertext">AES-CBC ciphertext, base64.</param>
/// <param name="Mac">Truncated MAC, base64; as in libolm it does not authenticate the message.</param>
/// <param name="Ephemeral">Ephemeral Curve25519 key, base64.</param>
public sealed record PkMessage(string Ciphertext, string Mac, string Ephemeral);

/// <summary>Public-key encryption of <see href="https://spec.matrix.org/latest/client-server-api/#backup-algorithm-mmegolm_backupv1curve25519-aes-sha2">m.megolm_backup.v1.curve25519-aes-sha2</see> key backups.</summary>
/// <remarks>Ported from vodozemac's <c>pk_encryption</c>, which keeps libolm's quirk: the MAC is computed over an empty message.</remarks>
public static class PkEncryption
{
    /// <summary>Return the public key of a base64 32-byte backup secret key.</summary>
    public static string GetPublicKey(string secret) => OlmBase64.Encode(Curve.X25519PublicKey(OlmBase64.DecodeKey(secret)));

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/> for <paramref name="publicKey"/>.</summary>
    public static PkMessage Encrypt(string publicKey, string plaintext)
    {
        // Ephemeral key agreement
        byte[] ephemeral = Curve.NewSecret();
        byte[] shared = Curve.X25519AgreeOrThrow(ephemeral, OlmBase64.DecodeKey(publicKey));

        // Encrypt; the MAC covers nothing, as in libolm
        using OlmCipher cipher = OlmCipher.ForPk(shared);
        byte[] ciphertext = cipher.Encrypt(Encoding.UTF8.GetBytes(plaintext));
        byte[] mac = cipher.Mac([])[..OlmCipher.TruncatedMacLength];
        PkMessage message = new(OlmBase64.Encode(ciphertext), OlmBase64.Encode(mac), OlmBase64.Encode(Curve.X25519PublicKey(ephemeral)));
        CryptographicOperations.ZeroMemory(ephemeral);
        CryptographicOperations.ZeroMemory(shared);
        // Return message
        return message;
    }

    /// <summary>Decrypt <paramref name="message"/> with a base64 backup secret key; return UTF-8 text. Throws <see cref="OlmException"/> for a wrong key.</summary>
    public static string Decrypt(string secret, PkMessage message)
    {
        // Key agreement with the sender's ephemeral key
        byte[] shared = Curve.X25519AgreeOrThrow(OlmBase64.DecodeKey(secret), OlmBase64.DecodeKey(message.Ephemeral));
        using OlmCipher cipher = OlmCipher.ForPk(shared);
        CryptographicOperations.ZeroMemory(shared);

        // The MAC of the empty message proves the key, then decrypt
        if (!cipher.VerifyTruncatedMac([], OlmBase64.Decode(message.Mac))) throw new OlmException("The backup MAC is invalid; wrong key.");
        // Return text
        return Encoding.UTF8.GetString(cipher.Decrypt(OlmBase64.Decode(message.Ciphertext)));
    }
}

/// <summary>Key agreement of <see href="https://spec.matrix.org/latest/client-server-api/#short-authentication-string-sas-verification">SAS verification</see>: send <see cref="PublicKey"/>, then <see cref="Establish"/> with theirs.</summary>
public sealed class Sas : IDisposable
{
    private byte[]? secret;

    /// <summary>Wrap an ephemeral secret.</summary>
    private Sas(byte[] secret)
    {
        // Store key
        this.secret = secret;
        PublicKey = OlmBase64.Encode(Curve.X25519PublicKey(secret));
    }

    /// <summary>Start a SAS with a fresh key pair.</summary>
    public static Sas Create() => new(Curve.NewSecret());

    /// <summary>Our ephemeral public key, base64.</summary>
    public string PublicKey { get; }

    /// <summary>Complete the key exchange with <paramref name="theirKey"/>; this object cannot be used afterwards.</summary>
    public EstablishedSas Establish(string theirKey)
    {
        // Assert not used, then consume the secret whatever the outcome
        byte[] ours = secret ?? throw new ObjectDisposedException(nameof(Sas));
        secret = null;
        try
        {
            // Return shared secret
            return new EstablishedSas(Curve.X25519AgreeOrThrow(ours, OlmBase64.DecodeKey(theirKey)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ours);
        }
    }

    /// <summary>Wipe the secret if not established.</summary>
    public void Dispose()
    {
        // Clear key
        if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        secret = null;
    }
}

/// <summary>Shared secret of a SAS verification: emoji or decimals to compare, and MACs over the keys being verified.</summary>
/// <remarks>MACs are <c>hkdf-hmac-sha256.v2</c>: HKDF-SHA-256 of the shared secret under the info gives the HMAC key.</remarks>
public sealed class EstablishedSas : IDisposable
{
    private readonly byte[] shared;

    /// <summary>Wrap the shared secret.</summary>
    internal EstablishedSas(byte[] shared)
    {
        // Store secret
        this.shared = shared;
    }

    /// <summary>Return the 6 bytes derived for <paramref name="info"/>, from which <see cref="EmojiIndices"/> and <see cref="Decimals"/> come.</summary>
    public byte[] GetBytes(string info) => HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, shared), 6, Encoding.UTF8.GetBytes(info));

    /// <summary>Return the 7 emoji indices of <paramref name="bytes"/>: 6 bits each from the first 42 bits.</summary>
    public static int[] EmojiIndices(byte[] bytes)
    {
        // Read 42 bits as big-endian
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits = (bits << 8) | bytes[i];
        bits >>= 6;
        // Return indices from the top
        return [.. Enumerable.Range(0, 7).Select(i => (int)((bits >> (36 - i * 6)) & 0x3F))];
    }

    /// <summary>Return the 3 four-digit numbers of <paramref name="bytes"/>: 13 bits each from the first 39 bits, plus 1000.</summary>
    public static int[] Decimals(byte[] bytes)
    {
        // Spec formula on the first 5 bytes
        int first = ((bytes[0] << 5) | (bytes[1] >> 3)) + 1000;
        int second = (((bytes[1] & 0x7) << 10) | (bytes[2] << 2) | (bytes[3] >> 6)) + 1000;
        int third = (((bytes[3] & 0x3F) << 7) | (bytes[4] >> 1)) + 1000;
        // Return numbers
        return [first, second, third];
    }

    /// <summary>Calculate the MAC of <paramref name="input"/>, e.g. a key, with <paramref name="info"/>; base64.</summary>
    public string CalculateMac(string input, string info) => OlmBase64.Encode(Mac(input, info));

    /// <summary>Return whether <paramref name="mac"/> is the MAC of <paramref name="input"/> with <paramref name="info"/>.</summary>
    public bool VerifyMac(string input, string info, string mac)
    {
        // Malformed base64 does not verify
        byte[] tag;
        try
        {
            tag = OlmBase64.Decode(mac);
        }
        catch (OlmException)
        {
            return false;
        }
        // Return constant-time comparison
        return CryptographicOperations.FixedTimeEquals(Mac(input, info), tag);
    }

    /// <summary>Wipe the shared secret.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(shared);

    /// <summary>HMAC-SHA-256 of <paramref name="input"/> with the key HKDF derives for <paramref name="info"/>.</summary>
    private byte[] Mac(string input, string info)
    {
        // Key per info, then MAC
        byte[] key = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, shared), 32, Encoding.UTF8.GetBytes(info));
        byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input));
        CryptographicOperations.ZeroMemory(key);
        // Return MAC
        return mac;
    }
}
