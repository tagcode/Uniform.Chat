// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Outbound Megolm session: encrypts room messages for everyone who has its session key.</summary>
/// <remarks>Ported from vodozemac 0.11.1 (<c>megolm/group_session</c>), session config version 1. Pickles use vodozemac's format.</remarks>
public sealed class MegolmOutboundSession : IDisposable
{
    private readonly MegolmRatchet ratchet;
    private readonly byte[] signingSeed;
    private readonly byte[] signingKey;

    /// <summary>Wrap loaded or created state.</summary>
    private MegolmOutboundSession(MegolmRatchet ratchet, byte[] signingSeed)
    {
        // Store state
        this.ratchet = ratchet;
        this.signingSeed = signingSeed;
        signingKey = Curve.Ed25519PublicKey(signingSeed);
    }

    /// <summary>Create a session with a random ratchet and signing key.</summary>
    public static MegolmOutboundSession Create() => new(MegolmRatchet.CreateRandom(), Curve.NewSecret());

    /// <summary>Restore a session from <see cref="Pickle"/>; throw <see cref="OlmException"/> for a wrong key.</summary>
    public static MegolmOutboundSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt and read
        JsonObject json = Pickles.Decrypt(pickle, key);
        Pickles.AssertConfig(json["config"]);
        // Return session
        return new MegolmOutboundSession(MegolmRatchet.FromJson(Pickles.Required(json, "ratchet")), Pickles.SigningKey(json["signing_key"]));
    }

    /// <summary>Session ID: the public signing key, unpadded base64.</summary>
    public string SessionId => OlmBase64.Encode(signingKey);

    /// <summary>Index of the next message.</summary>
    public uint MessageIndex => ratchet.Counter;

    /// <summary>The session key at the current index, signed, to share in <c>m.room_key</c>.</summary>
    public string SessionKey
    {
        get
        {
            // Version 2 key, signed by the session
            byte[] unsigned = MegolmKeys.Encode(2, ratchet, signingKey);
            // Return key with signature
            return OlmBase64.Encode([.. unsigned, .. Curve.Ed25519Sign(signingSeed, unsigned)]);
        }
    }

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/>.</summary>
    public string Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Encrypt <paramref name="plaintext"/> at the current index, then advance the ratchet.</summary>
    public string Encrypt(byte[] plaintext)
    {
        // Encrypt with keys of this index
        using OlmCipher cipher = OlmCipher.ForMegolm(ratchet.Bytes);
        byte[] encoded = MegolmMessage.Encode(ratchet.Counter, cipher.Encrypt(plaintext));
        byte[] mac = cipher.Mac(encoded)[..OlmCipher.TruncatedMacLength];

        // Sign message and MAC, then move on
        byte[] signed = [.. encoded, .. mac];
        byte[] signature = Curve.Ed25519Sign(signingSeed, signed);
        ratchet.Advance();
        // Return message
        return OlmBase64.Encode([.. signed, .. signature]);
    }

    /// <summary>Pickle the session in vodozemac's format, encrypted with 32-byte <paramref name="key"/>.</summary>
    public string Pickle(ReadOnlySpan<byte> key) =>
        Pickles.Encrypt(new JsonObject { ["ratchet"] = ratchet.ToJson(), ["signing_key"] = Pickles.SigningKey(signingSeed), ["config"] = Pickles.Config() }, key);

    /// <summary>Wipe secrets.</summary>
    public void Dispose()
    {
        // Clear keys
        ratchet.Clear();
        CryptographicOperations.ZeroMemory(signingSeed);
    }
}

/// <summary>Inbound Megolm session: decrypts one sender's room messages from the index its key was shared at.</summary>
/// <remarks>Ported from vodozemac 0.11.1 (<c>megolm/inbound_group_session</c>), session config version 1. Pickles use vodozemac's format.</remarks>
public sealed class MegolmInboundSession : IDisposable
{
    private readonly MegolmRatchet initial;
    private MegolmRatchet latest;
    private readonly byte[] signingKey;
    private readonly bool signingKeyVerified;

    /// <summary>Wrap loaded or created state.</summary>
    private MegolmInboundSession(MegolmRatchet initial, byte[] signingKey, bool signingKeyVerified)
    {
        // Store state; the latest ratchet starts at the initial one
        this.initial = initial;
        latest = initial.Clone();
        this.signingKey = signingKey;
        this.signingKeyVerified = signingKeyVerified;
    }

    /// <summary>Create a session from a signed session key of <c>m.room_key</c>; throw <see cref="OlmException"/> when the signature is invalid.</summary>
    public static MegolmInboundSession FromSessionKey(string sessionKey)
    {
        // Version 2 key with a signature by the session
        byte[] bytes = OlmBase64.Decode(sessionKey);
        (MegolmRatchet ratchet, byte[] signingKey) = MegolmKeys.Decode(2, bytes, signed: true);
        // Return verified session
        return new MegolmInboundSession(ratchet, signingKey, signingKeyVerified: true);
    }

    /// <summary>Create a session from an exported key, e.g. from a key backup or another device; not signed, so the sender is not proven.</summary>
    public static MegolmInboundSession Import(string exportedKey)
    {
        // Version 1 key without signature
        (MegolmRatchet ratchet, byte[] signingKey) = MegolmKeys.Decode(1, OlmBase64.Decode(exportedKey), signed: false);
        // Return unverified session
        return new MegolmInboundSession(ratchet, signingKey, signingKeyVerified: false);
    }

    /// <summary>Restore a session from <see cref="Pickle"/>; throw <see cref="OlmException"/> for a wrong key.</summary>
    public static MegolmInboundSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt and read
        JsonObject json = Pickles.Decrypt(pickle, key);
        Pickles.AssertConfig(json["config"]);
        // Return session
        return new MegolmInboundSession(MegolmRatchet.FromJson(Pickles.Required(json, "initial_ratchet")), Pickles.Bytes(json["signing_key"]), json["signing_key_verified"]?.GetValue<bool>() ?? false);
    }

    /// <summary>Session ID: the sender's public signing key, unpadded base64.</summary>
    public string SessionId => OlmBase64.Encode(signingKey);

    /// <summary>First message index this session can decrypt.</summary>
    public uint FirstKnownIndex => initial.Counter;

    /// <summary>Decrypt a Megolm message; throw <see cref="OlmException"/> for bad signatures, MACs or an index before <see cref="FirstKnownIndex"/>.</summary>
    public MegolmPlaintext Decrypt(string ciphertext)
    {
        // Signature over message and MAC, checked first
        MegolmMessage message = MegolmMessage.Parse(OlmBase64.Decode(ciphertext));
        byte[] encoded = MegolmMessage.Encode(message.Index, message.Ciphertext);
        if (!Curve.Ed25519Verify(signingKey, [.. encoded, .. message.Mac], message.Signature)) throw new OlmException("The signature on the message was invalid.");

        // Keys of the message's index
        MegolmRatchet ratchet = FindRatchet(message.Index) ?? throw new OlmException($"The message was encrypted using an unknown message index, first known index {FirstKnownIndex}, index of the message {message.Index}.");
        using OlmCipher cipher = OlmCipher.ForMegolm(ratchet.Bytes);
        if (!cipher.VerifyTruncatedMac(encoded, message.Mac)) throw new OlmException("Failed decrypting Megolm message, invalid MAC.");
        // Return plaintext
        return new MegolmPlaintext(cipher.Decrypt(message.Ciphertext), message.Index);
    }

    /// <summary>Export the session from <paramref name="index"/> on, to share with another device or a backup; throw <see cref="OlmException"/> before <see cref="FirstKnownIndex"/>.</summary>
    public string ExportAt(uint index)
    {
        // Ratchet at the index
        MegolmRatchet ratchet = FindRatchet(index) ?? throw new OlmException($"The session can't be exported at index {index}, first known index {FirstKnownIndex}.");
        // Return version 1 key
        return OlmBase64.Encode(MegolmKeys.Encode(1, ratchet, signingKey));
    }

    /// <summary>Pickle the session in vodozemac's format, encrypted with 32-byte <paramref name="key"/>.</summary>
    public string Pickle(ReadOnlySpan<byte> key) => Pickles.Encrypt(new JsonObject
    {
        ["initial_ratchet"] = initial.ToJson(),
        ["signing_key"] = Pickles.Bytes(signingKey),
        ["signing_key_verified"] = signingKeyVerified,
        ["config"] = Pickles.Config(),
    }, key);

    /// <summary>Wipe secrets.</summary>
    public void Dispose()
    {
        // Clear ratchets
        initial.Clear();
        latest.Clear();
    }

    /// <summary>Return the ratchet at <paramref name="index"/>, reusing or advancing the latest one; null before the first known index.</summary>
    private MegolmRatchet? FindRatchet(uint index)
    {
        // Exact matches
        if (initial.Counter == index) return initial;
        if (latest.Counter == index) return latest;

        // Advance the latest when ahead of it, else restart from the initial
        if (latest.Counter < index)
        {
            latest.AdvanceTo(index);
            return latest;
        }
        if (initial.Counter >= index) return null;
        latest.Clear();
        latest = initial.Clone();
        latest.AdvanceTo(index);
        // Return ratchet
        return latest;
    }
}

/// <summary>The Megolm ratchet: four 32-byte parts R0 to R3 hashed forward at different rates, and a counter.</summary>
/// <remarks>Ported from vodozemac's <c>megolm/ratchet</c>; part <c>i</c> advances every 2^(8·(3-i)) messages.</remarks>
internal sealed class MegolmRatchet
{
    private const int Length = 128;
    private readonly byte[] bytes;

    /// <summary>Wrap ratchet bytes at a counter.</summary>
    private MegolmRatchet(byte[] bytes, uint counter)
    {
        // Assert length
        if (bytes.Length != Length) throw new OlmException($"Invalid Megolm ratchet length: expected {Length}, got {bytes.Length}.");
        this.bytes = bytes;
        Counter = counter;
    }

    /// <summary>Message index of the current state.</summary>
    public uint Counter { get; private set; }
    /// <summary>The 128 ratchet bytes.</summary>
    public byte[] Bytes => bytes;

    /// <summary>Create a random ratchet at index 0.</summary>
    public static MegolmRatchet CreateRandom() => new(RandomNumberGenerator.GetBytes(Length), 0);

    /// <summary>Create a ratchet from bytes and a counter.</summary>
    public static MegolmRatchet FromBytes(byte[] bytes, uint counter) => new(bytes, counter);

    /// <summary>Return an independent copy.</summary>
    public MegolmRatchet Clone() => new((byte[])bytes.Clone(), Counter);

    /// <summary>Advance by one message: rehashes the slowest part that changes and every faster part from it.</summary>
    public void Advance()
    {
        // Slowest part whose counter digits roll over
        Counter = unchecked(Counter + 1);
        int from = 3;
        uint mask = 0x00FFFFFF;
        for (int i = 0; i < 4; i++)
        {
            if ((Counter & mask) == 0)
            {
                from = i;
                break;
            }
            mask >>= 8;
        }
        // Update parts 3 down to the slowest
        for (int to = 3; to >= from; to--) Update(from, to);
    }

    /// <summary>Advance to <paramref name="index"/>, hashing each part only as often as needed.</summary>
    public void AdvanceTo(uint index)
    {
        // Each part from slowest to fastest
        for (int j = 0; j < 4; j++)
        {
            int shift = (3 - j) * 8;
            uint mask = ~0u << shift;
            uint steps = unchecked((index >> shift) - (Counter >> shift)) & 0xFF;
            if (steps == 0)
            {
                // Wrap-around of R0
                if (index < Counter) steps = 0x100;
                else continue;
            }

            // Bump this part alone, then once more with the faster parts
            for (; steps > 1; steps--) Update(j, j);
            for (int k = 3; k >= j; k--) Update(j, k);
            Counter = index & mask;
        }
    }

    /// <summary>Set part <paramref name="to"/> to HMAC(part <paramref name="from"/>, <paramref name="to"/>).</summary>
    private void Update(int from, int to)
    {
        // Seed is the target part's number
        byte[] hash = HMACSHA256.HashData(bytes.AsSpan(from * 32, 32), [(byte)to]);
        hash.CopyTo(bytes, to * 32);
    }

    /// <summary>Write vodozemac's JSON.</summary>
    public JsonObject ToJson() => new() { ["inner"] = Pickles.Bytes(bytes), ["counter"] = Counter };

    /// <summary>Read vodozemac's JSON.</summary>
    public static MegolmRatchet FromJson(JsonNode json) => new(Pickles.Bytes(json["inner"], Length), json["counter"]!.GetValue<uint>());

    /// <summary>Wipe the state.</summary>
    public void Clear() => CryptographicOperations.ZeroMemory(bytes);
}

/// <summary>Megolm session key formats: version, big-endian index, 128 ratchet bytes, the 32-byte signing key, and for version 2 a signature.</summary>
internal static class MegolmKeys
{
    private const int UnsignedLength = 1 + 4 + 128 + Curve.KeyLength;

    /// <summary>Encode a key of <paramref name="version"/> without signature.</summary>
    public static byte[] Encode(byte version, MegolmRatchet ratchet, byte[] signingKey)
    {
        // Version, index, ratchet, public key
        byte[] output = new byte[UnsignedLength];
        output[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1), ratchet.Counter);
        ratchet.Bytes.CopyTo(output, 5);
        signingKey.CopyTo(output, 5 + 128);
        // Return bytes
        return output;
    }

    /// <summary>Decode a key; for <paramref name="signed"/> keys, verifies the trailing signature by the key's own signing key.</summary>
    public static (MegolmRatchet Ratchet, byte[] SigningKey) Decode(byte version, byte[] bytes, bool signed)
    {
        // Version and length
        if (bytes.Length == 0 || bytes[0] != version) throw new OlmException($"The session key had an invalid version, expected {version}.");
        if (bytes.Length < UnsignedLength + (signed ? Curve.SignatureLength : 0)) throw new OlmException("The session key was too short.");

        // Fields
        uint index = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1));
        byte[] ratchet = bytes[5..(5 + 128)];
        byte[] signingKey = bytes[(5 + 128)..UnsignedLength];
        if (signed && !Curve.Ed25519Verify(signingKey, bytes.AsSpan(0, UnsignedLength), bytes.AsSpan(UnsignedLength, Curve.SignatureLength))) throw new OlmException("The signature on the session key was invalid.");
        // Return ratchet and key
        return (MegolmRatchet.FromBytes(ratchet, index), signingKey);
    }
}

/// <summary>A Megolm message: version, protobuf index and ciphertext, truncated MAC, then an Ed25519 signature.</summary>
/// <param name="Index">Message index.</param>
/// <param name="Ciphertext">AES-CBC ciphertext.</param>
/// <param name="Mac">Truncated MAC over the encoded message.</param>
/// <param name="Signature">Signature over the encoded message and MAC.</param>
internal sealed record MegolmMessage(uint Index, byte[] Ciphertext, byte[] Mac, byte[] Signature)
{
    /// <summary>Message version with truncated MACs.</summary>
    private const byte Version = 3;

    /// <summary>Encode version and fields, the input of the MAC.</summary>
    public static byte[] Encode(uint index, byte[] ciphertext)
    {
        // Version, then fields 1 and 2
        List<byte> output = [Version];
        Protobuf.WriteVarint(output, 1, index);
        Protobuf.WriteBytes(output, 2, ciphertext);
        // Return bytes
        return [.. output];
    }

    /// <summary>Parse a message with a truncated MAC; throw <see cref="OlmException"/> when malformed.</summary>
    public static MegolmMessage Parse(byte[] bytes)
    {
        // Version and length
        int suffix = OlmCipher.TruncatedMacLength + Curve.SignatureLength;
        if (bytes.Length == 0) throw new OlmException("The message is missing its version.");
        if (bytes[0] != Version) throw new OlmException($"Unsupported Megolm message version {bytes[0]}.");
        if (bytes.Length < suffix + 2) throw new OlmException("The message is too short.");

        // Fields, MAC, signature
        (Dictionary<int, ulong> varints, Dictionary<int, byte[]> fields) = Protobuf.Read(bytes.AsSpan(1, bytes.Length - 1 - suffix));
        if (!varints.TryGetValue(1, out ulong index) || index > uint.MaxValue) throw new OlmException("The message is missing its index.");
        byte[] mac = bytes[^suffix..^Curve.SignatureLength];
        // Return message
        return new MegolmMessage((uint)index, fields.GetValueOrDefault(2) ?? [], mac, bytes[^Curve.SignatureLength..]);
    }
}
