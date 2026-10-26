// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>An Olm session with one other device: a double ratchet for encrypting to-device messages.</summary>
/// <remarks>Ported from vodozemac 0.11.1 (<c>olm/session</c>), session config version 1: truncated MACs. Pickles use vodozemac's format.</remarks>
public sealed class OlmSession : IDisposable
{
    private const int MaxReceivingChains = 5;
    private static readonly byte[] MessageKeySeed = [1];
    private static readonly byte[] AdvanceSeed = [2];
    private readonly SessionKeys keys;
    private SendingRatchet sending;
    private readonly List<ReceiverChain> receivingChains;

    /// <summary>Wrap loaded or created state.</summary>
    private OlmSession(SessionKeys keys, SendingRatchet sending, List<ReceiverChain> receivingChains)
    {
        // Store state
        this.keys = keys;
        this.sending = sending;
        this.receivingChains = receivingChains;
    }

    /// <summary>Start an outbound session from the 3DH shared secret; see <see cref="OlmAccount.CreateOutboundSession"/>.</summary>
    internal static OlmSession CreateOutbound(byte[] sharedSecret, SessionKeys keys)
    {
        // Root and first chain key from the shared secret
        (byte[] rootKey, byte[] chainKey) = ExpandRoot(sharedSecret);
        // Return session with an active ratchet
        return new OlmSession(keys, SendingRatchet.Active(null, 0, rootKey, Curve.NewSecret(), chainKey, 0), []);
    }

    /// <summary>Start an inbound session from the 3DH shared secret and the sender's first ratchet key; see <see cref="OlmAccount.CreateInboundSession"/>.</summary>
    internal static OlmSession CreateInbound(byte[] sharedSecret, byte[] remoteRatchetKey, SessionKeys keys)
    {
        // Their chain is ready to receive; ours starts when we first send
        (byte[] rootKey, byte[] chainKey) = ExpandRoot(sharedSecret);
        ReceiverChain chain = new(remoteRatchetKey, chainKey, 0, [], 0);
        // Return session with an inactive ratchet
        return new OlmSession(keys, SendingRatchet.Inactive(rootKey, remoteRatchetKey, 0), [chain]);
    }

    /// <summary>Restore a session from <see cref="Pickle"/>; throw <see cref="OlmException"/> for a wrong key.</summary>
    public static OlmSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt and read
        JsonObject json = Pickles.Decrypt(pickle, key);
        Pickles.AssertConfig(json["config"]);
        JsonNode sessionKeys = Pickles.Required(json, "session_keys");
        SessionKeys keys = new(Pickles.Bytes(sessionKeys["identity_key"]), Pickles.Bytes(sessionKeys["base_key"]), Pickles.Bytes(sessionKeys["one_time_key"]));
        SendingRatchet sending = SendingRatchet.FromJson(Pickles.Required(json, "sending_ratchet"));
        JsonArray chains = Pickles.Required(json, "receiving_chains")["inner"] as JsonArray ?? [];
        // Return session
        return new OlmSession(keys, sending, [.. chains.Select(ReceiverChain.FromJson)]);
    }

    /// <summary>Return the ID of the session a pre-key message would create or continue.</summary>
    public static string PreKeySessionId(string preKeyCiphertext) => PreKeyMessage.Parse(OlmBase64.Decode(preKeyCiphertext)).Keys.SessionId;

    /// <summary>Session ID: SHA-256 of the identity, base and one-time keys.</summary>
    public string SessionId => keys.SessionId;

    /// <summary>Whether a message from the other side was decrypted, after which messages are no longer pre-key messages.</summary>
    internal bool HasReceivedMessage => receivingChains.Count > 0;

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/>.</summary>
    public OlmMessage Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Encrypt <paramref name="plaintext"/>: a pre-key message until the other side has answered.</summary>
    public OlmMessage Encrypt(byte[] plaintext)
    {
        // Next message key, starting our ratchet when inactive
        if (sending.IsInactive) sending = sending.Activate();
        (byte[] messageKey, ulong index) = sending.NextMessageKey();

        // Encrypt and MAC the encoded message
        using OlmCipher cipher = OlmCipher.ForOlm(messageKey);
        CryptographicOperations.ZeroMemory(messageKey);
        NormalMessage message = new(Curve.X25519PublicKey(sending.RatchetSecret!), index, cipher.Encrypt(plaintext), []);
        message = message with { Mac = cipher.Mac(message.MacInput())[..OlmCipher.TruncatedMacLength] };

        // Return normal or pre-key message
        if (HasReceivedMessage) return new OlmMessage(1, OlmBase64.Encode(message.ToBytes()));
        return new OlmMessage(0, OlmBase64.Encode(new PreKeyMessage(keys, message).ToBytes()));
    }

    /// <summary>Decrypt a message of this session; throw <see cref="OlmException"/> when it does not decrypt.</summary>
    public byte[] Decrypt(OlmMessage message)
    {
        // Parse either type
        byte[] bytes = OlmBase64.Decode(message.Body);
        NormalMessage normal = message.Type switch
        {
            0 => PreKeyMessage.Parse(bytes).Message,
            1 => NormalMessage.Parse(bytes),
            _ => throw new OlmException($"Unknown Olm message type {message.Type}."),
        };
        // Return plaintext
        return Decrypt(normal);
    }

    /// <summary>Decrypt with the chain of the message's ratchet key, or a new chain when the other side ratcheted; state changes only on success.</summary>
    internal byte[] Decrypt(NormalMessage message)
    {
        // Known chain
        if (receivingChains.FirstOrDefault(c => c.RatchetKey.AsSpan().SequenceEqual(message.RatchetKey)) is ReceiverChain known) return known.Decrypt(message);

        // New chain from the other side's new ratchet key; as in vodozemac, activating an inactive ratchet sticks once the step worked
        SendingRatchet current = sending.IsInactive ? sending.Activate() : sending;
        (SendingRatchet next, ReceiverChain chain) = current.Advance(message.RatchetKey);
        sending = current;
        byte[] plaintext = chain.Decrypt(message);

        // Keep the new chain, dropping the oldest of too many
        sending = next;
        if (receivingChains.Count >= MaxReceivingChains) receivingChains.RemoveAt(0);
        receivingChains.Add(chain);
        // Return plaintext
        return plaintext;
    }

    /// <summary>Pickle the session in vodozemac's format, encrypted with 32-byte <paramref name="key"/>.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Session keys, ratchets and config
        JsonObject json = new()
        {
            ["session_keys"] = new JsonObject { ["identity_key"] = Pickles.Bytes(keys.IdentityKey), ["base_key"] = Pickles.Bytes(keys.BaseKey), ["one_time_key"] = Pickles.Bytes(keys.OneTimeKey) },
            ["sending_ratchet"] = sending.ToJson(),
            ["receiving_chains"] = new JsonObject { ["inner"] = new JsonArray([.. receivingChains.Select(c => (JsonNode)c.ToJson())]) },
            ["config"] = Pickles.Config(),
        };
        // Return encrypted pickle
        return Pickles.Encrypt(json, key);
    }

    /// <summary>Wipe secrets.</summary>
    public void Dispose()
    {
        // Clear keys
        sending.Clear();
        foreach (ReceiverChain chain in receivingChains) chain.Clear();
    }

    /// <summary>Expand a 3DH shared secret into the root key and the first chain key.</summary>
    private static (byte[] RootKey, byte[] ChainKey) ExpandRoot(byte[] sharedSecret)
    {
        // HKDF with a zero salt
        byte[] expanded = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 64, [0], "OLM_ROOT"u8.ToArray());
        // Return halves
        return (expanded[..32], expanded[32..]);
    }

    /// <summary>Root key ratchet step: HKDF of the DH of two ratchet keys, salted with the root key; returns the next root and chain keys.</summary>
    private static (byte[] RootKey, byte[] ChainKey) AdvanceRoot(byte[] rootKey, byte[] ratchetSecret, byte[] remoteRatchetKey)
    {
        // DH must be contributory
        byte[] shared = Curve.X25519AgreeOrThrow(ratchetSecret, remoteRatchetKey);
        byte[] output = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 64, rootKey, "OLM_RATCHET"u8.ToArray());
        CryptographicOperations.ZeroMemory(shared);
        // Return halves
        return (output[..32], output[32..]);
    }

    /// <summary>Message key of a chain key: HMAC with byte 1.</summary>
    private static byte[] MessageKey(byte[] chainKey) => HMACSHA256.HashData(chainKey, MessageKeySeed);

    /// <summary>Next chain key: HMAC with byte 2.</summary>
    private static byte[] NextChainKey(byte[] chainKey) => HMACSHA256.HashData(chainKey, AdvanceSeed);

    /// <summary>The keys that identify a session.</summary>
    /// <param name="IdentityKey">Curve25519 identity key of the side that started the session.</param>
    /// <param name="BaseKey">Ephemeral base key of the starting side.</param>
    /// <param name="OneTimeKey">The other side's one-time or fallback key.</param>
    internal sealed record SessionKeys(byte[] IdentityKey, byte[] BaseKey, byte[] OneTimeKey)
    {
        /// <summary>SHA-256 of the three keys, unpadded base64.</summary>
        public string SessionId => OlmBase64.Encode(SHA256.HashData([.. IdentityKey, .. BaseKey, .. OneTimeKey]));
    }

    /// <summary>Our side of the double ratchet: active with a sending chain, or inactive until we next send.</summary>
    private sealed class SendingRatchet
    {
        /// <summary>Whether we must create a new ratchet key before sending.</summary>
        public bool IsInactive { get; private init; }
        /// <summary>Root key; for inactive ratchets the one shared with the other side's key.</summary>
        public required byte[] RootKey { get; init; }
        /// <summary>Their ratchet key that our current chain answers, null for the first chain.</summary>
        public byte[]? RemoteRatchetKey { get; init; }
        /// <summary>Our ratchet secret, active only.</summary>
        public byte[]? RatchetSecret { get; init; }
        /// <summary>Sending chain key, active only.</summary>
        public byte[]? ChainKey { get; private set; }
        /// <summary>Index of the next message, active only.</summary>
        public ulong ChainIndex { get; private set; }
        /// <summary>Ratchet steps so far, null when unknown.</summary>
        public ulong? RatchetCount { get; init; }

        /// <summary>Create an active ratchet.</summary>
        public static SendingRatchet Active(byte[]? parentRatchetKey, ulong? count, byte[] rootKey, byte[] ratchetSecret, byte[] chainKey, ulong chainIndex) =>
            new() { RemoteRatchetKey = parentRatchetKey, RatchetCount = count, RootKey = rootKey, RatchetSecret = ratchetSecret, ChainKey = chainKey, ChainIndex = chainIndex };

        /// <summary>Create an inactive ratchet waiting for our next send.</summary>
        public static SendingRatchet Inactive(byte[] rootKey, byte[] remoteRatchetKey, ulong? count) =>
            new() { IsInactive = true, RootKey = rootKey, RemoteRatchetKey = remoteRatchetKey, RatchetCount = count };

        /// <summary>Start a new sending chain with a fresh ratchet key against the other side's current key.</summary>
        public SendingRatchet Activate()
        {
            // Root step with a new key
            byte[] secret = Curve.NewSecret();
            (byte[] root, byte[] chain) = AdvanceRoot(RootKey, secret, RemoteRatchetKey!);
            // Return active ratchet
            return Active(RemoteRatchetKey, RatchetCount + 1, root, secret, chain, 0);
        }

        /// <summary>Return the next message key and its index, advancing the chain.</summary>
        public (byte[] Key, ulong Index) NextMessageKey()
        {
            // Key, then step
            byte[] key = MessageKey(ChainKey!);
            ulong index = ChainIndex;
            byte[] old = ChainKey!;
            ChainKey = NextChainKey(old);
            CryptographicOperations.ZeroMemory(old);
            ChainIndex++;
            // Return key
            return (key, index);
        }

        /// <summary>Answer the other side's new ratchet key: returns our next inactive ratchet and their new receiving chain.</summary>
        public (SendingRatchet Next, ReceiverChain Chain) Advance(byte[] remoteRatchetKey)
        {
            // Root step with our current key
            (byte[] root, byte[] chain) = AdvanceRoot(RootKey, RatchetSecret!, remoteRatchetKey);
            ulong? count = RatchetCount + 1;
            // Return both halves
            return (Inactive(root, remoteRatchetKey, count), new ReceiverChain(remoteRatchetKey, chain, 0, [], count));
        }

        /// <summary>Write vodozemac's tagged JSON.</summary>
        public JsonObject ToJson()
        {
            // Inactive: their root key and ratchet key
            if (IsInactive)
                return new JsonObject { ["type"] = "inactive", ["root_key"] = new JsonObject { ["key"] = Pickles.Bytes(RootKey) }, ["ratchet_key"] = Pickles.Bytes(RemoteRatchetKey!), ["ratchet_count"] = Pickles.RatchetCount(RatchetCount) };

            // Return active ratchet
            return new JsonObject
            {
                ["type"] = "active",
                ["parent_ratchet_key"] = RemoteRatchetKey is null ? null : Pickles.Bytes(RemoteRatchetKey),
                ["ratchet_count"] = Pickles.RatchetCount(RatchetCount),
                ["active_ratchet"] = new JsonObject { ["root_key"] = Pickles.Bytes(RootKey), ["ratchet_key"] = Pickles.Bytes(RatchetSecret!) },
                ["symmetric_key_ratchet"] = new JsonObject { ["key"] = Pickles.Bytes(ChainKey!), ["index"] = ChainIndex },
            };
        }

        /// <summary>Read vodozemac's tagged JSON.</summary>
        public static SendingRatchet FromJson(JsonNode json)
        {
            // Inactive
            ulong? count = Pickles.RatchetCount(json["ratchet_count"]);
            string? type = json["type"]?.GetValue<string>();
            if (type == "inactive") return Inactive(Pickles.Bytes(Pickles.Required(json, "root_key")["key"]), Pickles.Bytes(json["ratchet_key"]), count);
            if (type != "active") throw new OlmException($"Invalid pickle: unknown ratchet type {type}.");

            // Return active
            JsonNode ratchet = Pickles.Required(json, "active_ratchet");
            JsonNode chain = Pickles.Required(json, "symmetric_key_ratchet");
            byte[]? parent = json["parent_ratchet_key"] is JsonNode p ? Pickles.Bytes(p) : null;
            return Active(parent, count, Pickles.Bytes(ratchet["root_key"]), Pickles.Bytes(ratchet["ratchet_key"]), Pickles.Bytes(chain["key"]), chain["index"]!.GetValue<ulong>());
        }

        /// <summary>Wipe secrets.</summary>
        public void Clear()
        {
            // Clear keys
            CryptographicOperations.ZeroMemory(RootKey);
            if (RatchetSecret is not null) CryptographicOperations.ZeroMemory(RatchetSecret);
            if (ChainKey is not null) CryptographicOperations.ZeroMemory(ChainKey);
        }
    }

    /// <summary>A chain of the other side's messages for one of their ratchet keys, with keys of skipped messages.</summary>
    private sealed class ReceiverChain
    {
        private const ulong MaxMessageGap = 2000;
        private const int MaxMessageKeys = 40;
        private byte[] chainKey;
        private ulong chainIndex;
        private readonly List<(byte[] Key, ulong Index)> skipped;
        private readonly ulong? ratchetCount;

        /// <summary>Create a chain.</summary>
        public ReceiverChain(byte[] ratchetKey, byte[] chainKey, ulong chainIndex, List<(byte[] Key, ulong Index)> skipped, ulong? ratchetCount)
        {
            // Store state
            RatchetKey = ratchetKey;
            this.chainKey = chainKey;
            this.chainIndex = chainIndex;
            this.skipped = skipped;
            this.ratchetCount = ratchetCount;
        }

        /// <summary>Their ratchet key of this chain.</summary>
        public byte[] RatchetKey { get; }

        /// <summary>Decrypt a message of this chain, using a skipped key or advancing; state changes only on success.</summary>
        public byte[] Decrypt(NormalMessage message)
        {
            // Too far ahead
            ulong target = message.ChainIndex;
            ulong gap = target > chainIndex ? target - chainIndex : 0;
            if (gap > MaxMessageGap) throw new OlmException($"The message gap was too big, got {gap}, max allowed {MaxMessageGap}.");

            // Earlier message: its skipped key, used once
            if (chainIndex > target)
            {
                int found = skipped.FindIndex(k => k.Index == target);
                if (found < 0) throw new OlmException($"The message key with the given index can't be created, message index: {target}.");
                byte[] plaintext = DecryptWith(skipped[found].Key, message);
                skipped.RemoveAt(found);
                return plaintext;
            }

            // Later message: advance a copy, keeping the last keys skipped on the way
            byte[] key = chainKey;
            ulong index = chainIndex;
            List<(byte[] Key, ulong Index)> newSkipped = [];
            while (index < target)
            {
                if (target - index <= MaxMessageKeys)
                {
                    if (newSkipped.Count >= MaxMessageKeys) newSkipped.RemoveAt(0);
                    newSkipped.Add((MessageKey(key), index));
                }
                key = NextChainKey(key);
                index++;
            }
            byte[] messageKey = MessageKey(key);
            byte[] result = DecryptWith(messageKey, message);

            // Commit
            chainKey = NextChainKey(key);
            chainIndex = index + 1;
            foreach ((byte[] Key, ulong Index) entry in newSkipped)
            {
                if (skipped.Count >= MaxMessageKeys) skipped.RemoveAt(0);
                skipped.Add(entry);
            }
            // Return plaintext
            return result;
        }

        /// <summary>Verify the truncated MAC and decrypt with one message key.</summary>
        private static byte[] DecryptWith(byte[] messageKey, NormalMessage message)
        {
            // Version 1 sessions use truncated MACs only
            using OlmCipher cipher = OlmCipher.ForOlm(messageKey);
            if (message.Mac.Length != OlmCipher.TruncatedMacLength) throw new OlmException($"Invalid MAC length: expected {OlmCipher.TruncatedMacLength}, got {message.Mac.Length}.");
            if (!cipher.VerifyTruncatedMac(message.MacInput(), message.Mac)) throw new OlmException("Failed decrypting Olm message, invalid MAC.");
            // Return plaintext
            return cipher.Decrypt(message.Ciphertext);
        }

        /// <summary>Write vodozemac's JSON.</summary>
        public JsonObject ToJson() => new()
        {
            ["ratchet_key"] = Pickles.Bytes(RatchetKey),
            ["hkdf_ratchet"] = new JsonObject { ["key"] = Pickles.Bytes(chainKey), ["index"] = chainIndex },
            ["skipped_message_keys"] = new JsonObject { ["inner"] = new JsonArray([.. skipped.Select(k => (JsonNode)new JsonObject { ["key"] = Pickles.Bytes(k.Key), ["index"] = k.Index })]) },
            ["ratchet_count"] = Pickles.RatchetCount(ratchetCount),
        };

        /// <summary>Read vodozemac's JSON.</summary>
        public static ReceiverChain FromJson(JsonNode? json)
        {
            // Chain key and skipped keys
            JsonNode chain = Pickles.Required(json, "hkdf_ratchet");
            JsonArray keys = json?["skipped_message_keys"]?["inner"] as JsonArray ?? [];
            List<(byte[] Key, ulong Index)> skipped = [.. keys.Select(k => (Pickles.Bytes(k?["key"]), k!["index"]!.GetValue<ulong>()))];
            // Return chain
            return new ReceiverChain(Pickles.Bytes(json!["ratchet_key"]), Pickles.Bytes(chain["key"]), chain["index"]!.GetValue<ulong>(), skipped, Pickles.RatchetCount(json["ratchet_count"]));
        }

        /// <summary>Wipe secrets.</summary>
        public void Clear()
        {
            // Clear keys
            CryptographicOperations.ZeroMemory(chainKey);
            foreach ((byte[] key, _) in skipped) CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>A normal Olm message: version, then protobuf ratchet key, chain index and ciphertext, then the MAC.</summary>
/// <param name="RatchetKey">Sender's current ratchet key.</param>
/// <param name="ChainIndex">Index of the message in its chain.</param>
/// <param name="Ciphertext">AES-CBC ciphertext.</param>
/// <param name="Mac">Truncated MAC over the encoded message without MAC.</param>
internal sealed record NormalMessage(byte[] RatchetKey, ulong ChainIndex, byte[] Ciphertext, byte[] Mac)
{
    /// <summary>Message version with truncated MACs.</summary>
    public const byte Version = 3;

    /// <summary>Return the bytes the MAC covers: version and protobuf fields, re-encoded as vodozemac does.</summary>
    public byte[] MacInput()
    {
        // Version, then fields 1, 2 and 4
        List<byte> output = [Version];
        Protobuf.WriteBytes(output, 1, RatchetKey);
        Protobuf.WriteVarint(output, 2, ChainIndex);
        Protobuf.WriteBytes(output, 4, Ciphertext);
        // Return bytes
        return [.. output];
    }

    /// <summary>Return the encoded message with its MAC.</summary>
    public byte[] ToBytes() => [.. MacInput(), .. Mac];

    /// <summary>Parse a message with a truncated MAC; throw <see cref="OlmException"/> when malformed.</summary>
    public static NormalMessage Parse(ReadOnlySpan<byte> bytes)
    {
        // Version and length
        if (bytes.Length == 0) throw new OlmException("The message is missing its version.");
        if (bytes[0] != Version) throw new OlmException($"Unsupported Olm message version {bytes[0]}.");
        if (bytes.Length < OlmCipher.TruncatedMacLength + 2) throw new OlmException("The message is too short.");

        // Fields between version and MAC
        (Dictionary<int, ulong> varints, Dictionary<int, byte[]> fields) = Protobuf.Read(bytes[1..^OlmCipher.TruncatedMacLength]);
        if (!varints.TryGetValue(2, out ulong chainIndex)) throw new OlmException("The message is missing its chain index.");
        byte[] ratchetKey = fields.GetValueOrDefault(1) ?? [];
        if (ratchetKey.Length != Curve.KeyLength) throw new OlmException("The message has an invalid ratchet key.");
        // Return message
        return new NormalMessage(ratchetKey, chainIndex, fields.GetValueOrDefault(4) ?? [], bytes[^OlmCipher.TruncatedMacLength..].ToArray());
    }
}

/// <summary>A pre-key Olm message: the session keys and the first normal message, so the receiver can create the session.</summary>
/// <param name="Keys">Keys identifying the session.</param>
/// <param name="Message">The wrapped message.</param>
internal sealed record PreKeyMessage(OlmSession.SessionKeys Keys, NormalMessage Message)
{
    /// <summary>Pre-key message version.</summary>
    public const byte Version = 3;

    /// <summary>Return the encoded message: version, then protobuf one-time key, base key, identity key and message.</summary>
    public byte[] ToBytes()
    {
        // Version, then fields 1 to 4
        List<byte> output = [Version];
        Protobuf.WriteBytes(output, 1, Keys.OneTimeKey);
        Protobuf.WriteBytes(output, 2, Keys.BaseKey);
        Protobuf.WriteBytes(output, 3, Keys.IdentityKey);
        Protobuf.WriteBytes(output, 4, Message.ToBytes());
        // Return bytes
        return [.. output];
    }

    /// <summary>Parse a pre-key message; throw <see cref="OlmException"/> when malformed.</summary>
    public static PreKeyMessage Parse(ReadOnlySpan<byte> bytes)
    {
        // Version
        if (bytes.Length == 0) throw new OlmException("The message is missing its version.");
        if (bytes[0] != Version) throw new OlmException($"Unsupported pre-key message version {bytes[0]}.");

        // Keys and the inner message
        (_, Dictionary<int, byte[]> fields) = Protobuf.Read(bytes[1..]);
        byte[] Key(int field) => fields.GetValueOrDefault(field) is { Length: Curve.KeyLength } key ? key : throw new OlmException("The pre-key message has an invalid key.");
        OlmSession.SessionKeys keys = new(Key(3), Key(2), Key(1));
        // Return message
        return new PreKeyMessage(keys, NormalMessage.Parse(fields.GetValueOrDefault(4) ?? []));
    }
}
