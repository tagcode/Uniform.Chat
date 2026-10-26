// Ported from vodozemac (https://github.com/matrix-org/vodozemac), Copyright 2021 The Matrix.org Foundation C.I.C., Damir Jelić and Denis Kasak,
// under the Apache License 2.0; translated from Rust to C#. See THIRD-PARTY-NOTICES.md.

namespace Uniform.Matrix.Protocol.Crypto;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>An Olm account: the device's identity keys, one-time keys and fallback keys.</summary>
/// <remarks>Ported from vodozemac 0.11.1 (<c>olm/account</c>). Pickles use vodozemac's format.</remarks>
public sealed class OlmAccount : IDisposable
{
    private const int PublicMaxOneTimeKeys = 50;
    private const int MaxStoredOneTimeKeys = 100 * PublicMaxOneTimeKeys;
    private readonly byte[] signingSeed;
    private readonly byte[] identitySecret;
    private readonly SortedDictionary<ulong, byte[]> oneTimeSecrets;
    private readonly SortedDictionary<ulong, byte[]> unpublishedOneTimeKeys;
    private readonly Dictionary<string, ulong> oneTimeKeyIds;
    private ulong nextOneTimeKeyId;
    private ulong nextFallbackKeyId;
    private StoredFallbackKey? fallbackKey;
    private StoredFallbackKey? previousFallbackKey;

    /// <summary>Wrap loaded or created state.</summary>
    private OlmAccount(byte[] signingSeed, byte[] identitySecret, SortedDictionary<ulong, byte[]> oneTimeSecrets, SortedDictionary<ulong, byte[]> unpublishedOneTimeKeys, ulong nextOneTimeKeyId, ulong nextFallbackKeyId, StoredFallbackKey? fallbackKey, StoredFallbackKey? previousFallbackKey)
    {
        // Store state, indexing one-time keys by public key
        this.signingSeed = signingSeed;
        this.identitySecret = identitySecret;
        this.oneTimeSecrets = oneTimeSecrets;
        this.unpublishedOneTimeKeys = unpublishedOneTimeKeys;
        this.nextOneTimeKeyId = nextOneTimeKeyId;
        this.nextFallbackKeyId = nextFallbackKeyId;
        this.fallbackKey = fallbackKey;
        this.previousFallbackKey = previousFallbackKey;
        oneTimeKeyIds = oneTimeSecrets.ToDictionary(k => OlmBase64.Encode(Curve.X25519PublicKey(k.Value)), k => k.Key);
        IdentityKeys = new IdentityKeys(OlmBase64.Encode(Curve.X25519PublicKey(identitySecret)), OlmBase64.Encode(Curve.Ed25519PublicKey(signingSeed)));
    }

    /// <summary>Create an account with new identity keys.</summary>
    public static OlmAccount Create() => new(Curve.NewSecret(), Curve.NewSecret(), [], [], 0, 0, null, null);

    /// <summary>Restore an account from <see cref="Pickle"/>; throw <see cref="OlmException"/> for a wrong key.</summary>
    public static OlmAccount FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt
        JsonObject json = Pickles.Decrypt(pickle, key);

        // One-time keys by key ID; the public map lists only unpublished keys
        JsonNode oneTime = Pickles.Required(json, "one_time_keys");
        SortedDictionary<ulong, byte[]> secrets = ReadKeyMap(oneTime["private_keys"]);
        SortedDictionary<ulong, byte[]> unpublished = ReadKeyMap(oneTime["public_keys"]);
        ulong nextId = (oneTime["next_key_id"] ?? oneTime["key_id"])?.GetValue<ulong>() ?? 0;

        // Fallback keys
        JsonNode fallback = Pickles.Required(json, "fallback_keys");
        // Return account
        return new OlmAccount(Pickles.SigningKey(json["signing_key"]), Pickles.Bytes(json["diffie_hellman_key"]), secrets, unpublished, nextId,
            fallback["key_id"]?.GetValue<ulong>() ?? 0, StoredFallbackKey.FromJson(fallback["fallback_key"]), StoredFallbackKey.FromJson(fallback["previous_fallback_key"]));
    }

    /// <summary>Public identity keys.</summary>
    public IdentityKeys IdentityKeys { get; }

    /// <summary>How many one-time keys to keep on the server; the account stores more, as some may be claimed before their messages arrive.</summary>
    public int MaxOneTimeKeys => PublicMaxOneTimeKeys;

    /// <summary>One-time keys not yet published, by key ID.</summary>
    public IReadOnlyDictionary<string, string> OneTimeKeys => unpublishedOneTimeKeys.ToDictionary(k => KeyId(k.Key), k => OlmBase64.Encode(k.Value));

    /// <summary>The fallback key when not yet published, by key ID; empty otherwise.</summary>
    public IReadOnlyDictionary<string, string> FallbackKey =>
        fallbackKey is { Published: false } key ? new Dictionary<string, string> { [KeyId(key.KeyId)] = OlmBase64.Encode(Curve.X25519PublicKey(key.Secret)) } : new Dictionary<string, string>();

    /// <summary>Sign UTF-8 <paramref name="message"/> with the Ed25519 identity key; return the signature in unpadded base64.</summary>
    public string Sign(string message) => OlmBase64.Encode(Curve.Ed25519Sign(signingSeed, Encoding.UTF8.GetBytes(message)));

    /// <summary>Generate <paramref name="count"/> one-time keys, dropping the oldest beyond the storage limit.</summary>
    public void GenerateOneTimeKeys(int count)
    {
        // New keys with increasing IDs
        for (int i = 0; i < count; i++)
        {
            if (oneTimeSecrets.Count >= MaxStoredOneTimeKeys) RemoveOneTimeKey(oneTimeSecrets.Keys.First());
            byte[] secret = Curve.NewSecret();
            byte[] publicKey = Curve.X25519PublicKey(secret);
            ulong keyId = nextOneTimeKeyId;
            oneTimeSecrets[keyId] = secret;
            unpublishedOneTimeKeys[keyId] = publicKey;
            oneTimeKeyIds[OlmBase64.Encode(publicKey)] = keyId;
            nextOneTimeKeyId = unchecked(nextOneTimeKeyId + 1);
        }
    }

    /// <summary>Generate a new fallback key; the previous one keeps working until the next.</summary>
    public void GenerateFallbackKey()
    {
        // Shift current to previous
        if (previousFallbackKey is not null) CryptographicOperations.ZeroMemory(previousFallbackKey.Secret);
        previousFallbackKey = fallbackKey;
        fallbackKey = new StoredFallbackKey(nextFallbackKeyId++, Curve.NewSecret(), Published: false);
    }

    /// <summary>Mark current one-time and fallback keys as published, so they are not returned again.</summary>
    public void MarkKeysAsPublished()
    {
        // Forget public parts; secrets stay for incoming sessions
        unpublishedOneTimeKeys.Clear();
        if (fallbackKey is not null) fallbackKey = fallbackKey with { Published = true };
    }

    /// <summary>Start a session with a device from its identity key and a claimed one-time key, both unpadded base64.</summary>
    public OlmSession CreateOutboundSession(string identityKey, string oneTimeKey)
    {
        // Our identity and a fresh base key against theirs: 3DH
        byte[] theirIdentity = OlmBase64.DecodeKey(identityKey);
        byte[] theirOneTimeKey = OlmBase64.DecodeKey(oneTimeKey);
        byte[] baseSecret = Curve.NewSecret();
        byte[] shared = [.. Curve.X25519AgreeOrThrow(identitySecret, theirOneTimeKey), .. Curve.X25519AgreeOrThrow(baseSecret, theirIdentity), .. Curve.X25519AgreeOrThrow(baseSecret, theirOneTimeKey)];

        // Session identified by our identity, our base key and their one-time key
        OlmSession.SessionKeys keys = new(Curve.X25519PublicKey(identitySecret), Curve.X25519PublicKey(baseSecret), theirOneTimeKey);
        OlmSession session = OlmSession.CreateOutbound(shared, keys);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(baseSecret);
        // Return session
        return session;
    }

    /// <summary>Create a session from a pre-key message of <paramref name="identityKey"/>'s device; use up the one-time key only when the message decrypts.</summary>
    /// <returns>The session and the decrypted first message.</returns>
    public (OlmSession Session, byte[] Plaintext) CreateInboundSession(string identityKey, string preKeyCiphertext)
    {
        // Message must come from the given identity
        PreKeyMessage message = PreKeyMessage.Parse(OlmBase64.Decode(preKeyCiphertext));
        if (!message.Keys.IdentityKey.AsSpan().SequenceEqual(OlmBase64.DecodeKey(identityKey))) throw new OlmException("The identity key doesn't match the one in the pre-key message.");

        // Our one-time or fallback key it used
        string oneTimeKey = OlmBase64.Encode(message.Keys.OneTimeKey);
        byte[] secret = FindOneTimeSecret(oneTimeKey) ?? throw new OlmException($"The pre-key message contained an unknown one-time key: {oneTimeKey}.");

        // 3DH from the receiving side
        byte[] shared = [.. Curve.X25519AgreeOrThrow(secret, message.Keys.IdentityKey), .. Curve.X25519AgreeOrThrow(identitySecret, message.Keys.BaseKey), .. Curve.X25519AgreeOrThrow(secret, message.Keys.BaseKey)];
        OlmSession session = OlmSession.CreateInbound(shared, message.Message.RatchetKey, message.Keys);
        CryptographicOperations.ZeroMemory(shared);

        // Decrypt first; a forged message must not use up the key
        byte[] plaintext = session.Decrypt(message.Message);
        if (oneTimeKeyIds.TryGetValue(oneTimeKey, out ulong keyId)) RemoveOneTimeKey(keyId);
        // Return session and plaintext
        return (session, plaintext);
    }

    /// <summary>Pickle the account in vodozemac's format, encrypted with 32-byte <paramref name="key"/>.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Keys and counters
        JsonObject json = new()
        {
            ["signing_key"] = Pickles.SigningKey(signingSeed),
            ["diffie_hellman_key"] = Pickles.Bytes(identitySecret),
            ["one_time_keys"] = new JsonObject { ["next_key_id"] = nextOneTimeKeyId, ["public_keys"] = WriteKeyMap(unpublishedOneTimeKeys), ["private_keys"] = WriteKeyMap(oneTimeSecrets) },
            ["fallback_keys"] = new JsonObject { ["key_id"] = nextFallbackKeyId, ["fallback_key"] = fallbackKey?.ToJson(), ["previous_fallback_key"] = previousFallbackKey?.ToJson() },
        };
        // Return encrypted pickle
        return Pickles.Encrypt(json, key);
    }

    /// <summary>Wipe secrets.</summary>
    public void Dispose()
    {
        // Clear keys
        CryptographicOperations.ZeroMemory(signingSeed);
        CryptographicOperations.ZeroMemory(identitySecret);
        foreach (byte[] secret in oneTimeSecrets.Values) CryptographicOperations.ZeroMemory(secret);
        if (fallbackKey is not null) CryptographicOperations.ZeroMemory(fallbackKey.Secret);
        if (previousFallbackKey is not null) CryptographicOperations.ZeroMemory(previousFallbackKey.Secret);
    }

    /// <summary>Return the secret of a one-time key, else of the current or previous fallback key.</summary>
    private byte[]? FindOneTimeSecret(string publicKey)
    {
        // One-time keys first
        if (oneTimeKeyIds.TryGetValue(publicKey, out ulong keyId) && oneTimeSecrets.TryGetValue(keyId, out byte[]? secret)) return secret;
        // Return a matching fallback key
        return new[] { fallbackKey, previousFallbackKey }.FirstOrDefault(f => f is not null && OlmBase64.Encode(Curve.X25519PublicKey(f.Secret)) == publicKey)?.Secret;
    }

    /// <summary>Forget a one-time key.</summary>
    private void RemoveOneTimeKey(ulong keyId)
    {
        // Drop from all indexes
        if (!oneTimeSecrets.Remove(keyId, out byte[]? secret)) return;
        oneTimeKeyIds.Remove(OlmBase64.Encode(Curve.X25519PublicKey(secret)));
        unpublishedOneTimeKeys.Remove(keyId);
        CryptographicOperations.ZeroMemory(secret);
    }

    /// <summary>Format a key ID as vodozemac does: the 8-byte big-endian counter in unpadded base64.</summary>
    private static string KeyId(ulong id)
    {
        // Big-endian bytes
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, id);
        // Return base64
        return OlmBase64.Encode(bytes);
    }

    /// <summary>Read a JSON map of key ID to key bytes.</summary>
    private static SortedDictionary<ulong, byte[]> ReadKeyMap(JsonNode? node)
    {
        // Numeric string keys
        SortedDictionary<ulong, byte[]> keys = [];
        foreach ((string id, JsonNode? key) in node as JsonObject ?? []) keys[ulong.Parse(id)] = Pickles.Bytes(key);
        // Return keys
        return keys;
    }

    /// <summary>Write a map of key ID to key bytes.</summary>
    private static JsonObject WriteKeyMap(SortedDictionary<ulong, byte[]> keys)
    {
        // Numeric string keys
        JsonObject map = [];
        foreach ((ulong id, byte[] key) in keys) map[id.ToString()] = Pickles.Bytes(key);
        // Return map
        return map;
    }

    /// <summary>A fallback key: used when one-time keys run out, until replaced.</summary>
    /// <param name="KeyId">Key ID.</param>
    /// <param name="Secret">X25519 secret.</param>
    /// <param name="Published">Whether it was uploaded.</param>
    private sealed record StoredFallbackKey(ulong KeyId, byte[] Secret, bool Published)
    {
        /// <summary>Write vodozemac's JSON.</summary>
        public JsonObject ToJson() => new() { ["key_id"] = KeyId, ["key"] = Pickles.Bytes(Secret), ["published"] = Published };

        /// <summary>Read vodozemac's JSON; null for none.</summary>
        public static StoredFallbackKey? FromJson(JsonNode? json) => json is null ? null : new StoredFallbackKey(json["key_id"]!.GetValue<ulong>(), Pickles.Bytes(json["key"]), json["published"]?.GetValue<bool>() ?? false);
    }
}
