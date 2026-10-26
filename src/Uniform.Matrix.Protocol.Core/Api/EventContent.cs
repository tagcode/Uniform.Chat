namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json.Serialization;

// Content records are lenient (no required members) because other clients may send incomplete content.

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroommessage">m.room.message</see>.</summary>
public sealed record MessageContent
{
    /// <summary>Message type, see <see cref="MessageTypes"/>.</summary>
    [JsonPropertyName("msgtype")]
    public string? MessageType { get; init; }
    /// <summary>Plain text body; fallback for every message type.</summary>
    public string? Body { get; init; }
    /// <summary>Format of <see cref="FormattedBody"/>, usually <c>org.matrix.custom.html</c>.</summary>
    public string? Format { get; init; }
    /// <summary>Formatted body, e.g. HTML.</summary>
    public string? FormattedBody { get; init; }
    /// <summary>Content URI of an attachment in an unencrypted room.</summary>
    public string? Url { get; init; }
    /// <summary>Attachment in an <see href="https://spec.matrix.org/latest/client-server-api/#sending-encrypted-attachments">encrypted room</see>, instead of <see cref="Url"/>.</summary>
    public EncryptedFile? File { get; init; }
    /// <summary>Content URI of the attachment, from <see cref="Url"/> or <see cref="File"/>.</summary>
    [JsonIgnore]
    public string? MediaUrl => Url ?? File?.Url;
    /// <summary>Original file name of an attachment when <see cref="Body"/> is a caption.</summary>
    public string? Filename { get; init; }
    /// <summary>Metadata of an attachment.</summary>
    public MediaInfo? Info { get; init; }
    /// <summary>Relation to another event, e.g. reply or edits.</summary>
    [JsonPropertyName("m.relates_to")]
    public Relation? RelatesTo { get; init; }
    /// <summary>Replacement content of an <see href="https://spec.matrix.org/latest/client-server-api/#event-replacements">edit</see>.</summary>
    [JsonPropertyName("m.new_content")]
    public MessageContent? NewContent { get; init; }
    /// <summary>Users and room <see href="https://spec.matrix.org/latest/client-server-api/#user-and-room-mentions">mentioned</see>; when set, servers notify only these instead of matching the body.</summary>
    [JsonPropertyName("m.mentions")]
    public Mentions? Mentions { get; init; }
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#extensions-to-mroommessage-msgtypes">Encrypted attachment</see>: where the ciphertext is and how to decrypt it.</summary>
public sealed record EncryptedFile
{
    /// <summary>Content URI of the ciphertext.</summary>
    public string? Url { get; init; }
    /// <summary>AES-256-CTR key.</summary>
    public JsonWebKey? Key { get; init; }
    /// <summary>Initial counter block, unpadded base64.</summary>
    public string? Iv { get; init; }
    /// <summary>Hashes of the ciphertext by algorithm; <c>sha256</c> is required.</summary>
    public Dictionary<string, string>? Hashes { get; init; }
    /// <summary>Version of the format; <c>v2</c>.</summary>
    public string? V { get; init; }
}

/// <summary><see href="https://www.rfc-editor.org/rfc/rfc7517">JSON Web Key</see> of an <see cref="EncryptedFile"/>.</summary>
public sealed record JsonWebKey
{
    /// <summary>Key type; <c>oct</c>.</summary>
    public string? Kty { get; init; }
    /// <summary>Allowed operations; <c>encrypt</c> and <c>decrypt</c>.</summary>
    public IReadOnlyList<string>? KeyOps { get; init; }
    /// <summary>Algorithm; <c>A256CTR</c>.</summary>
    public string? Alg { get; init; }
    /// <summary>Key, unpadded base64url.</summary>
    public string? K { get; init; }
    /// <summary>Extractable; <c>true</c>.</summary>
    public bool? Ext { get; init; }
}

/// <summary>Attachment metadata of <see cref="MessageContent.Info"/>, see <see href="https://spec.matrix.org/latest/client-server-api/#mfile">m.file</see>.</summary>
public sealed record MediaInfo
{
    /// <summary>MIME type, e.g. <c>image/png</c>.</summary>
    public string? Mimetype { get; init; }
    /// <summary>Size in bytes.</summary>
    public long? Size { get; init; }
    /// <summary>Image or video width in pixels.</summary>
    [JsonPropertyName("w")]
    public int? Width { get; init; }
    /// <summary>Image or video height in pixels.</summary>
    [JsonPropertyName("h")]
    public int? Height { get; init; }
    /// <summary>Audio or video duration in milliseconds.</summary>
    public long? Duration { get; init; }
}

/// <summary>Intentional <see href="https://spec.matrix.org/latest/client-server-api/#user-and-room-mentions">mentions</see> of a message.</summary>
public sealed record Mentions
{
    /// <summary>Mentioned user IDs.</summary>
    public IReadOnlyList<string>? UserIds { get; init; }
    /// <summary>Whether the whole room is mentioned, like <c>@room</c>.</summary>
    public bool? Room { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#event-annotations-and-reactions">m.reaction</see>.</summary>
public sealed record ReactionContent
{
    /// <summary>Annotation relation holding the target event and the reaction key.</summary>
    [JsonPropertyName("m.relates_to")]
    public Relation? RelatesTo { get; init; }
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#forming-relationships-between-events">Relation</see> between events.</summary>
public sealed record Relation
{
    /// <summary>Relation type, see <see cref="RelationTypes"/>.</summary>
    public string? RelType { get; init; }
    /// <summary>Annotation key, e.g. the emoji of a reaction.</summary>
    public string? Key { get; init; }
    /// <summary>Target event.</summary>
    public string? EventId { get; init; }
    /// <summary>Event this one <see href="https://spec.matrix.org/latest/client-server-api/#rich-replies">replies</see> to.</summary>
    [JsonPropertyName("m.in_reply_to")]
    public InReplyTo? InReplyTo { get; init; }
}

/// <summary>Reply target of <see cref="Relation.InReplyTo"/>.</summary>
public sealed record InReplyTo
{
    /// <summary>Event replied to.</summary>
    public string? EventId { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroommember">m.room.member</see>; state key is the user ID.</summary>
public sealed record MemberContent
{
    /// <summary>Membership state, see <see cref="Memberships"/>.</summary>
    public string? Membership { get; init; }
    /// <summary>Display name in this room.</summary>
    [JsonPropertyName("displayname")]
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>Reason for a kick, ban, leave or invites.</summary>
    public string? Reason { get; init; }
    /// <summary>Whether an invite is for a direct chat.</summary>
    public bool? IsDirect { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomname">m.room.name</see>.</summary>
public sealed record NameContent
{
    /// <summary>Room name.</summary>
    public string? Name { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomtopic">m.room.topic</see>.</summary>
public sealed record TopicContent
{
    /// <summary>Room topic.</summary>
    public string? Topic { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomcanonical_alias">m.room.canonical_alias</see>.</summary>
public sealed record CanonicalAliasContent
{
    /// <summary>Main alias, e.g. <c>#general:matrix.org</c>.</summary>
    public string? Alias { get; init; }
    /// <summary>Other aliases.</summary>
    public IReadOnlyList<string>? AltAliases { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomcreate">m.room.create</see>.</summary>
public sealed record CreateContent
{
    /// <summary>Room creator in room versions before 11; uses the event sender otherwise.</summary>
    public string? Creator { get; init; }
    /// <summary>Room version.</summary>
    public string? RoomVersion { get; init; }
    /// <summary>Room type, e.g. <c>m.space</c>; null for chat rooms.</summary>
    public string? Type { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mroomredaction">m.room.redaction</see>.</summary>
public sealed record RedactionContent
{
    /// <summary>Redacted event ID in room version 11 and later.</summary>
    public string? Redacts { get; init; }
    /// <summary>Reason for the redaction.</summary>
    public string? Reason { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mtyping">m.typing</see>.</summary>
public sealed record TypingContent
{
    /// <summary>Users currently typing.</summary>
    public IReadOnlyList<string> UserIds { get; init; } = [];
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mpresence">m.presence</see>, also the presence status response.</summary>
public sealed record PresenceContent
{
    /// <summary>Presence state, see <see cref="PresenceStates"/>.</summary>
    public string? Presence { get; init; }
    /// <summary>Status message.</summary>
    public string? StatusMsg { get; init; }
    /// <summary>Milliseconds since the user was last active.</summary>
    public long? LastActiveAgo { get; init; }
    /// <summary>Whether the user is currently active.</summary>
    public bool? CurrentlyActive { get; init; }
    /// <summary>Display name, sent in presence events only.</summary>
    [JsonPropertyName("displayname")]
    public string? DisplayName { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mreceipt">m.receipt</see>: event ID, then receipt type, then user ID.</summary>
/// <example><code><![CDATA[
/// foreach ((string eventId, string receiptType, string userId, ReceiptInfo info) in receipts.Flatten())
///     Console.WriteLine($"{userId} read up to {eventId}");
/// ]]></code></example>
public sealed class ReceiptContent : Dictionary<string, Dictionary<string, Dictionary<string, ReceiptInfo>>>
{
    /// <summary>List every receipt as one tuple.</summary>
    public IEnumerable<(string EventId, string ReceiptType, string UserId, ReceiptInfo Info)> Flatten() =>
        from byEvent in this
        from byType in byEvent.Value
        from byUser in byType.Value
        select (byEvent.Key, byType.Key, byUser.Key, byUser.Value);
}

/// <summary>One receipt of <see cref="ReceiptContent"/>.</summary>
public sealed record ReceiptInfo
{
    /// <summary>Time of reading in milliseconds since Unix epoch.</summary>
    public long? Ts { get; init; }
    /// <summary>Thread the receipt is for; <c>main</c> or null for the main timeline.</summary>
    public string? ThreadId { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mdirect">m.direct</see>: user ID to direct-message room IDs.</summary>
public sealed class DirectContent : Dictionary<string, List<string>>;

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mignored_user_list">m.ignored_user_list</see>.</summary>
public sealed record IgnoredUserListContent
{
    /// <summary>Ignored user IDs; values are empty objects.</summary>
    public IReadOnlyDictionary<string, object> IgnoredUsers { get; init; } = new Dictionary<string, object>();
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mtag">m.tag</see>, also the tags response.</summary>
public sealed record TagContent
{
    /// <summary>Tags keyed by name, see <see cref="RoomTags"/>.</summary>
    public IReadOnlyDictionary<string, TagInfo> Tags { get; init; } = new Dictionary<string, TagInfo>();
}

/// <summary>One tag of <see cref="TagContent"/>.</summary>
public sealed record TagInfo
{
    /// <summary>Position among rooms with the same tag, from 0 to 1.</summary>
    public double? Order { get; init; }
}

/// <summary>Content of <see href="https://spec.matrix.org/latest/client-server-api/#mfully_read">m.fully_read</see>.</summary>
public sealed record FullyReadContent
{
    /// <summary>Event up to which the user has read everything.</summary>
    public string? EventId { get; init; }
}
