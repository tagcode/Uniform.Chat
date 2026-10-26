// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BcEd25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;
using Org.BouncyCastle.Crypto.Parameters;
using Uniform.Matrix.Protocol.Core;

/// <summary>Public identity keys of an account, unpadded base64.</summary>
/// <param name="Curve25519">Key agreement key, the device's <c>curve25519:</c> key.</param>
/// <param name="Ed25519">Signing key, the device fingerprint.</param>
public sealed record IdentityKeys(string Curve25519, string Ed25519);

/// <summary>An Olm message as sent in <c>m.room.encrypted</c> to-device content.</summary>
/// <param name="Type">0 for a pre-key message that starts a session, 1 for a normal message.</param>
/// <param name="Body">Ciphertext, unpadded base64.</param>
public sealed record OlmMessage(int Type, string Body);

/// <summary>A decrypted Megolm message with its index, which must not repeat for a session.</summary>
/// <param name="Plaintext">Decrypted bytes, usually UTF-8 JSON.</param>
/// <param name="MessageIndex">Ratchet index the message was encrypted at.</param>
public sealed record MegolmPlaintext(byte[] Plaintext, uint MessageIndex)
{
    /// <summary>Plaintext as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Plaintext);
}

/// <summary>X25519 and Ed25519 on raw bytes, through BouncyCastle.</summary>
internal static class Curve
{
    /// <summary>Length of keys and shared secrets.</summary>
    public const int KeyLength = 32;
    /// <summary>Length of Ed25519 signatures.</summary>
    public const int SignatureLength = 64;

    /// <summary>Return a random X25519 or Ed25519 secret; scalars are clamped when used, as in vodozemac.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(KeyLength);

    /// <summary>Return the X25519 public key of <paramref name="secret"/>.</summary>
    public static byte[] X25519PublicKey(byte[] secret) => new X25519PrivateKeyParameters(secret).GeneratePublicKey().GetEncoded();

    /// <summary>Return the X25519 shared secret, or null when it is all zeros, i.e. the other key was of small order.</summary>
    public static byte[]? X25519Agree(byte[] secret, byte[] publicKey)
    {
        // BouncyCastle refuses non-contributory results
        if (publicKey.Length != KeyLength) return null;
        byte[] shared = new byte[KeyLength];
        try
        {
            new X25519PrivateKeyParameters(secret).GenerateSecret(new X25519PublicKeyParameters(publicKey), shared, 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        // Return secret
        return shared;
    }

    /// <summary>Return the X25519 shared secret; throw <see cref="OlmException"/> when it is not contributory.</summary>
    public static byte[] X25519AgreeOrThrow(byte[] secret, byte[] publicKey) =>
        X25519Agree(secret, publicKey) ?? throw new OlmException("The key exchange lacked contributory behavior, resulting in an insecure shared secret.");

    /// <summary>Return the Ed25519 public key of a 32-byte seed.</summary>
    public static byte[] Ed25519PublicKey(byte[] seed) => new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();

    /// <summary>Sign <paramref name="message"/> with a 32-byte Ed25519 seed.</summary>
    public static byte[] Ed25519Sign(byte[] seed, ReadOnlySpan<byte> message)
    {
        // Pure Ed25519, no context
        byte[] signature = new byte[SignatureLength];
        new Ed25519PrivateKeyParameters(seed).Sign(BcEd25519.Algorithm.Ed25519, null, message, signature);
        // Return signature
        return signature;
    }

    /// <summary>Return whether <paramref name="signature"/> by <paramref name="publicKey"/> is valid for <paramref name="message"/>; malformed keys are invalid.</summary>
    public static bool Ed25519Verify(byte[] publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        // Assert sizes, then verify
        if (publicKey.Length != KeyLength || signature.Length != SignatureLength) return false;
        try
        {
            return new Ed25519PublicKeyParameters(publicKey).Verify(BcEd25519.Algorithm.Ed25519, null, message, signature);
        }
        catch (ArgumentException)
        {
            // Done; not a point on the curve
            return false;
        }
    }
}

/// <summary>Base64 as vodozemac uses it: unpadded when written, padding optional when read; bad input is an <see cref="OlmException"/>.</summary>
internal static class OlmBase64
{
    /// <summary>Encode without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64.EncodeUnpadded(bytes);

    /// <summary>Decode, throwing <see cref="OlmException"/> for invalid input.</summary>
    public static byte[] Decode(string text)
    {
        // Wrap format errors
        try
        {
            return Base64.Decode(text);
        }
        catch (FormatException)
        {
            // Done; not base64
            throw new OlmException("Invalid base64.");
        }
    }

    /// <summary>Decode a 32-byte key, throwing <see cref="OlmException"/> for other lengths.</summary>
    public static byte[] DecodeKey(string text)
    {
        // Assert length
        byte[] key = Decode(text);
        if (key.Length != Curve.KeyLength) throw new OlmException($"Invalid key length: expected {Curve.KeyLength}, got {key.Length}.");
        // Return key
        return key;
    }
}

/// <summary>The subset of protobuf encoding Olm and Megolm messages use: varint and length-delimited fields.</summary>
internal static class Protobuf
{
    /// <summary>Append a varint field.</summary>
    public static void WriteVarint(List<byte> output, int field, ulong value)
    {
        // Key, then value
        WriteRawVarint(output, (ulong)(field << 3));
        WriteRawVarint(output, value);
    }

    /// <summary>Append a length-delimited field; empty values are left out, like proto3.</summary>
    public static void WriteBytes(List<byte> output, int field, ReadOnlySpan<byte> value)
    {
        // Skip defaults
        if (value.Length == 0) return;
        WriteRawVarint(output, (ulong)((field << 3) | 2));
        WriteRawVarint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    /// <summary>Parse fields into varints and byte strings by field number; later fields win; unknown fixed-width fields are skipped.</summary>
    public static (Dictionary<int, ulong> Varints, Dictionary<int, byte[]> Bytes) Read(ReadOnlySpan<byte> input)
    {
        // Each field
        Dictionary<int, ulong> varints = [];
        Dictionary<int, byte[]> bytes = [];
        int position = 0;
        while (position < input.Length)
        {
            ulong key = ReadRawVarint(input, ref position);
            int field = (int)(key >> 3);
            switch (key & 7)
            {
                case 0:
                    varints[field] = ReadRawVarint(input, ref position);
                    break;
                case 1:
                    position = Skip(input, position, 8);
                    break;
                case 2:
                    ulong length = ReadRawVarint(input, ref position);
                    if (length > (ulong)(input.Length - position)) throw new OlmException("Truncated protobuf field.");
                    bytes[field] = input.Slice(position, (int)length).ToArray();
                    position += (int)length;
                    break;
                case 5:
                    position = Skip(input, position, 4);
                    break;
                default:
                    throw new OlmException("Unsupported protobuf wire type.");
            }
        }
        // Return fields
        return (varints, bytes);
    }

    /// <summary>Append an unsigned varint.</summary>
    private static void WriteRawVarint(List<byte> output, ulong value)
    {
        // Seven bits at a time
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    /// <summary>Read an unsigned varint of up to ten bytes.</summary>
    private static ulong ReadRawVarint(ReadOnlySpan<byte> input, ref int position)
    {
        // Seven bits at a time
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (position >= input.Length) throw new OlmException("Truncated protobuf varint.");
            byte b = input[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
        // Done; too long
        throw new OlmException("Invalid protobuf varint.");
    }

    /// <summary>Return the position after <paramref name="count"/> bytes.</summary>
    private static int Skip(ReadOnlySpan<byte> input, int position, int count)
    {
        // Assert available
        if (input.Length - position < count) throw new OlmException("Truncated protobuf field.");
        // Return new position
        return position + count;
    }
}

/// <summary>Pickles in vodozemac's format: JSON encrypted with <see cref="OlmCipher.ForPickle"/>, unpadded base64, so state moves between the two implementations.</summary>
internal static class Pickles
{
    /// <summary>Length of pickle keys in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>Encrypt a pickle's JSON.</summary>
    public static string Encrypt(JsonObject json, ReadOnlySpan<byte> key)
    {
        // Assert key, then encrypt UTF-8 JSON
        AssertKey(key);
        byte[] plaintext = Encoding.UTF8.GetBytes(json.ToJsonString());
        using OlmCipher cipher = OlmCipher.ForPickle(key);
        string pickle = OlmBase64.Encode(cipher.EncryptPickle(plaintext));
        CryptographicOperations.ZeroMemory(plaintext);
        // Return pickle
        return pickle;
    }

    /// <summary>Decrypt a pickle's JSON; throw <see cref="OlmException"/> for a wrong key or a damaged pickle.</summary>
    public static JsonObject Decrypt(string pickle, ReadOnlySpan<byte> key)
    {
        // Assert key, then decrypt
        AssertKey(key);
        using OlmCipher cipher = OlmCipher.ForPickle(key);
        byte[] plaintext = cipher.DecryptPickle(OlmBase64.Decode(pickle));
        try
        {
            return JsonNode.Parse(plaintext) as JsonObject ?? throw new OlmException("The pickle is not a JSON object.");
        }
        catch (JsonException ex)
        {
            // Done; not ours
            throw new OlmException($"The pickle is not valid JSON: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Throw when <paramref name="key"/> is not 32 bytes.</summary>
    public static void AssertKey(ReadOnlySpan<byte> key)
    {
        // Assert length
        if (key.Length != KeyLength) throw new ArgumentException($"Pickle keys are {KeyLength} bytes.", nameof(key));
    }

    /// <summary>Write bytes as a JSON array of numbers, as serde does for byte arrays.</summary>
    public static JsonArray Bytes(ReadOnlySpan<byte> bytes)
    {
        // One number per byte
        JsonArray array = [];
        foreach (byte b in bytes) array.Add(b);
        // Return array
        return array;
    }

    /// <summary>Read a JSON array of numbers as bytes of <paramref name="length"/>, or any length when null.</summary>
    public static byte[] Bytes(JsonNode? node, int? length = Curve.KeyLength)
    {
        // Assert array
        if (node is not JsonArray array) throw new OlmException("Invalid pickle: expected a byte array.");
        byte[] bytes = [.. array.Select(n => n?.GetValue<byte>() ?? throw new OlmException("Invalid pickle: null in byte array."))];
        if (length is int expected && bytes.Length != expected) throw new OlmException($"Invalid pickle: expected {expected} bytes, got {bytes.Length}.");
        // Return bytes
        return bytes;
    }

    /// <summary>Return a required child node.</summary>
    public static JsonNode Required(JsonNode? node, string name) => node?[name] ?? throw new OlmException($"Invalid pickle: missing {name}.");

    /// <summary>Write the session config, always version 1.</summary>
    public static JsonObject Config() => new() { ["version"] = "V1" };

    /// <summary>Assert a session config of version 1, which is all this implementation supports; missing means version 1.</summary>
    public static void AssertConfig(JsonNode? config)
    {
        // Version 2 needs full MACs, an experimental vodozemac feature
        string? version = config?["version"]?.GetValue<string>();
        if (version is not (null or "V1")) throw new OlmException($"Unsupported session config {version}.");
    }

    /// <summary>Write an Ed25519 key pair as its seed.</summary>
    public static JsonObject SigningKey(byte[] seed) => new() { ["Normal"] = Bytes(seed) };

    /// <summary>Read an Ed25519 key pair; keys expanded from libolm imports are not supported.</summary>
    public static byte[] SigningKey(JsonNode? node)
    {
        // Seed form only
        if (node?["Normal"] is JsonNode seed) return Bytes(seed);
        // Done; expanded keys need the full libolm compatibility layer
        throw new OlmException("Unsupported signing key in pickle.");
    }

    /// <summary>Write a ratchet count: known or unknown.</summary>
    public static JsonObject RatchetCount(ulong? count) => count is ulong known ? new JsonObject { ["Known"] = known } : new JsonObject { ["Unknown"] = null };

    /// <summary>Read a ratchet count; missing means unknown.</summary>
    public static ulong? RatchetCount(JsonNode? node) => node?["Known"]?.GetValue<ulong>();
}
