namespace Uniform.Matrix.Protocol.Server;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>Hashing, IDs, signatures and <see href="https://spec.matrix.org/latest/client-server-api/#redactions">redaction</see> of persistent data units, the events of the room graph.</summary>
/// <remarks>Events are <see cref="JsonObject"/>s, as signatures and hashes cover the exact fields, including ones this library does not model.</remarks>
public static class EventFormat
{
    private static readonly string[] TopLevelKeys = ["event_id", "type", "room_id", "sender", "state_key", "content", "hashes", "signatures", "depth", "prev_events", "auth_events", "origin_server_ts"];
    private static readonly string[] LegacyTopLevelKeys = ["origin", "membership", "prev_state"];

    /// <summary>Return the <see href="https://spec.matrix.org/latest/server-server-api/#calculating-the-content-hash-for-an-event">content hash</see>: SHA-256 of the canonical event without <c>unsigned</c>, <c>signatures</c> and <c>hashes</c>.</summary>
    public static string ContentHash(JsonObject pdu)
    {
        // Canonical form of the hashed fields
        JsonObject copy = Without(pdu, "unsigned", "signatures", "hashes");
        // Return unpadded base64 hash
        return Base64.EncodeUnpadded(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(copy))));
    }

    /// <summary>Return whether <c>hashes.sha256</c> matches the content; a mismatch means the event must be redacted before use.</summary>
    public static bool HasValidContentHash(JsonObject pdu) => pdu["hashes"]?["sha256"]?.GetValue<string>() is string hash && hash == ContentHash(pdu);

    /// <summary>Return the event ID: <c>$</c> and the URL-safe base64 <see href="https://spec.matrix.org/latest/server-server-api/#calculating-the-reference-hash-for-an-event">reference hash</see> of the redacted event.</summary>
    public static string ComputeEventId(JsonObject pdu, RoomVersion version)
    {
        // Redacted form without signatures and unsigned data
        JsonObject copy = Without(Redact(pdu, version), "signatures", "unsigned");
        // Return ID
        return "$" + Base64.EncodeUrlSafe(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(copy))));
    }

    /// <summary>Add the content hash and the server's signature over the redacted event, as when creating an event.</summary>
    /// <param name="pdu">Event to hash and sign in place.</param>
    /// <param name="version">Room version, for redaction.</param>
    /// <param name="serverName">Signing server.</param>
    /// <param name="keyId">Key ID, e.g. <c>ed25519:a_xyz</c>.</param>
    /// <param name="secret">Ed25519 seed, base64.</param>
    public static void HashAndSign(JsonObject pdu, RoomVersion version, string serverName, string keyId, string secret)
    {
        // Hash first: the signature covers the hash
        pdu["hashes"] = new JsonObject { ["sha256"] = ContentHash(pdu) };
        string signature = Ed25519.Sign(secret, SigningForm(pdu, version));
        CanonicalJson.AddSignature(pdu, serverName, keyId, signature);
    }

    /// <summary>Return whether <paramref name="serverName"/>'s key <paramref name="keyId"/> validly signed the redacted event.</summary>
    public static bool HasValidSignature(JsonObject pdu, RoomVersion version, string serverName, string keyId, string publicKey) =>
        CanonicalJson.GetSignature(pdu, serverName, keyId) is string signature && Ed25519.Verify(publicKey, SigningForm(pdu, version), signature);

    /// <summary>Return a copy with only the keys the room version keeps under redaction; content is emptied except for keys that protect the room's integrity.</summary>
    public static JsonObject Redact(JsonObject pdu, RoomVersion version)
    {
        // Top-level keys
        bool v11 = version.Redaction == RedactionRules.V11;
        JsonObject redacted = [];
        foreach ((string key, JsonNode? value) in pdu)
        {
            if (TopLevelKeys.Contains(key) || (!v11 && LegacyTopLevelKeys.Contains(key))) redacted[key] = value?.DeepClone();
        }

        // Content keys by event type
        JsonObject content = pdu["content"] as JsonObject ?? [];
        string? type = pdu["type"]?.GetValue<string>();
        string[] keep = type switch
        {
            "m.room.member" => ["membership", "join_authorised_via_users_server"],
            "m.room.create" => v11 ? [.. content.Select(c => c.Key)] : ["creator"],
            "m.room.join_rules" => ["join_rule", "allow"],
            "m.room.power_levels" => v11 ? ["ban", "events", "events_default", "invite", "kick", "redact", "state_default", "users", "users_default"] : ["ban", "events", "events_default", "kick", "redact", "state_default", "users", "users_default"],
            "m.room.history_visibility" => ["history_visibility"],
            "m.room.redaction" => v11 ? ["redacts"] : [],
            _ => [],
        };
        JsonObject kept = [];
        foreach (string key in keep)
        {
            if (content.TryGetPropertyValue(key, out JsonNode? value)) kept[key] = value?.DeepClone();
        }
        if (v11 && type == "m.room.member" && content["third_party_invite"]?["signed"] is JsonNode signed) kept["third_party_invite"] = new JsonObject { ["signed"] = signed.DeepClone() };
        if (redacted.ContainsKey("content")) redacted["content"] = kept;
        // Return redacted copy
        return redacted;
    }

    /// <summary>Return the canonical text a server signs: the redacted event without <c>signatures</c> and <c>unsigned</c>.</summary>
    private static string SigningForm(JsonObject pdu, RoomVersion version) => CanonicalJson.SigningForm(Redact(pdu, version));

    /// <summary>Return a copy of <paramref name="value"/> without <paramref name="keys"/>.</summary>
    private static JsonObject Without(JsonObject value, params string[] keys)
    {
        // Copy the rest
        JsonObject copy = [];
        foreach ((string key, JsonNode? node) in value)
        {
            if (!keys.Contains(key)) copy[key] = node?.DeepClone();
        }
        // Return copy
        return copy;
    }
}
