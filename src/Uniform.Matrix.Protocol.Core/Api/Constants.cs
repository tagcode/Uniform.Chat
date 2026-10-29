namespace Uniform.Matrix.Protocol.Core;

/// <summary>Well-known <see href="https://spec.matrix.org/latest/client-server-api/#events">event types</see>.</summary>
public static class EventTypes
{
    /// <summary>Room message, content <see cref="MessageContent"/>.</summary>
    public const string RoomMessage = "m.room.message";
    /// <summary>Membership change, content <see cref="MemberContent"/>.</summary>
    public const string RoomMember = "m.room.member";
    /// <summary>Room name, content <see cref="NameContent"/>.</summary>
    public const string RoomName = "m.room.name";
    /// <summary>Room topic, content <see cref="TopicContent"/>.</summary>
    public const string RoomTopic = "m.room.topic";
    /// <summary>Room avatar, content <see cref="RoomAvatarContent"/>.</summary>
    public const string RoomAvatar = "m.room.avatar";
    /// <summary>Main room alias, content <see cref="CanonicalAliasContent"/>.</summary>
    public const string RoomCanonicalAlias = "m.room.canonical_alias";
    /// <summary>First event of a room, content <see cref="CreateContent"/>.</summary>
    public const string RoomCreate = "m.room.create";
    /// <summary>Removes content of another event, content <see cref="RedactionContent"/>.</summary>
    public const string RoomRedaction = "m.room.redaction";
    /// <summary>End-to-end encrypted event, content <see cref="EncryptedContent"/>; decrypted by Uniform.Matrix.Protocol.Crypto.</summary>
    public const string RoomEncrypted = "m.room.encrypted";
    /// <summary>To-device event carrying a Megolm session key, content <see cref="RoomKeyContent"/>.</summary>
    public const string RoomKey = "m.room_key";
    /// <summary>To-device request for a Megolm session key from another of the user's devices.</summary>
    public const string RoomKeyRequest = "m.room_key_request";
    /// <summary>To-device answer to <see cref="RoomKeyRequest"/>, carrying an exported session key.</summary>
    public const string ForwardedRoomKey = "m.forwarded_room_key";
    /// <summary>To-device event with no content, sent over a new Olm session to replace a wedged one.</summary>
    public const string Dummy = "m.dummy";
    /// <summary>To-device request for a secret, e.g. a cross-signing key, from another of the user's devices.</summary>
    public const string SecretRequest = "m.secret.request";
    /// <summary>To-device answer to <see cref="SecretRequest"/>.</summary>
    public const string SecretSend = "m.secret.send";
    /// <summary>To-device request to start <see href="https://spec.matrix.org/latest/client-server-api/#key-verification-framework">key verification</see>.</summary>
    public const string KeyVerificationRequest = "m.key.verification.request";
    /// <summary>Key verification: the other device accepts the request.</summary>
    public const string KeyVerificationReady = "m.key.verification.ready";
    /// <summary>Key verification: begins a method, e.g. SAS.</summary>
    public const string KeyVerificationStart = "m.key.verification.start";
    /// <summary>SAS verification: the other device accepts the start with a commitment.</summary>
    public const string KeyVerificationAccept = "m.key.verification.accept";
    /// <summary>SAS verification: ephemeral public key.</summary>
    public const string KeyVerificationKey = "m.key.verification.key";
    /// <summary>SAS verification: MACs of the keys being verified.</summary>
    public const string KeyVerificationMac = "m.key.verification.mac";
    /// <summary>Key verification finished.</summary>
    public const string KeyVerificationDone = "m.key.verification.done";
    /// <summary>Key verification cancelled.</summary>
    public const string KeyVerificationCancel = "m.key.verification.cancel";
    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#voice-over-ip">1:1 call</see>: the caller's offer, content <see cref="CallInviteContent"/>.</summary>
    public const string CallInvite = "m.call.invite";
    /// <summary>1:1 call: trickled ICE candidates, content <see cref="CallCandidatesContent"/>.</summary>
    public const string CallCandidates = "m.call.candidates";
    /// <summary>1:1 call: the callee's answer, content <see cref="CallAnswerContent"/>.</summary>
    public const string CallAnswer = "m.call.answer";
    /// <summary>1:1 call: the caller picks one of the answers, content <see cref="CallSelectAnswerContent"/>.</summary>
    public const string CallSelectAnswer = "m.call.select_answer";
    /// <summary>1:1 call: the callee declines before answering, content <see cref="CallRejectContent"/>.</summary>
    public const string CallReject = "m.call.reject";
    /// <summary>1:1 call: renegotiation, e.g. to add video; not supported here.</summary>
    public const string CallNegotiate = "m.call.negotiate";
    /// <summary>1:1 call: ends the call, content <see cref="CallHangupContent"/>.</summary>
    public const string CallHangup = "m.call.hangup";
    /// <summary>Room power levels.</summary>
    public const string RoomPowerLevels = "m.room.power_levels";
    /// <summary>State event marking a room as end-to-end encrypted.</summary>
    public const string RoomEncryption = "m.room.encryption";
    /// <summary>Reaction to another event, content <see cref="ReactionContent"/>.</summary>
    public const string Reaction = "m.reaction";
    /// <summary>Room account data holding tags, content <see cref="TagContent"/>.</summary>
    public const string Tag = "m.tag";
    /// <summary>Account data listing ignored users, content <see cref="IgnoredUserListContent"/>.</summary>
    public const string IgnoredUserList = "m.ignored_user_list";
    /// <summary>Ephemeral list of typing users, content <see cref="TypingContent"/>.</summary>
    public const string Typing = "m.typing";
    /// <summary>Ephemeral read receipts.</summary>
    public const string Receipt = "m.receipt";
    /// <summary>Presence of a user, content <see cref="PresenceContent"/>.</summary>
    public const string Presence = "m.presence";
    /// <summary>Room account data holding the fully read marker.</summary>
    public const string FullyRead = "m.fully_read";
    /// <summary>Account data listing direct-message rooms, content <see cref="DirectContent"/>.</summary>
    public const string Direct = "m.direct";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#forming-relationships-between-events">Relation types</see> of <see cref="Relation.RelType"/>.</summary>
public static class RelationTypes
{
    /// <summary>Edit of the target event.</summary>
    public const string Replace = "m.replace";
    /// <summary>Annotation such as a reaction; see <see cref="Relation.Key"/>.</summary>
    public const string Annotation = "m.annotation";
    /// <summary>Message in the thread of the target event.</summary>
    public const string Thread = "m.thread";
    /// <summary>Generic reference to the target event.</summary>
    public const string Reference = "m.reference";
}

/// <summary>Formats of <see cref="MessageContent.Format"/>.</summary>
public static class MessageFormats
{
    /// <summary>HTML subset of <see href="https://spec.matrix.org/latest/client-server-api/#mroommessage-msgtypes">formatted messages</see>.</summary>
    public const string Html = "org.matrix.custom.html";
}

/// <summary>Predefined <see href="https://spec.matrix.org/latest/client-server-api/#room-tagging">room tags</see>.</summary>
public static class RoomTags
{
    /// <summary>Favourite room.</summary>
    public const string Favourite = "m.favourite";
    /// <summary>Low priority room.</summary>
    public const string LowPriority = "m.lowpriority";
    /// <summary>Room for server notices.</summary>
    public const string ServerNotice = "m.server_notice";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#mroommessage-msgtypes">Message types</see> of <see cref="MessageContent.MessageType"/>.</summary>
public static class MessageTypes
{
    /// <summary>Plain text message.</summary>
    public const string Text = "m.text";
    /// <summary>Action, shown like <c>* alice waves</c>.</summary>
    public const string Emote = "m.emote";
    /// <summary>Automated message, e.g. from a bot.</summary>
    public const string Notice = "m.notice";
    /// <summary>Image attachment.</summary>
    public const string Image = "m.image";
    /// <summary>File attachment.</summary>
    public const string File = "m.file";
    /// <summary>Audio attachment.</summary>
    public const string Audio = "m.audio";
    /// <summary>Video attachment.</summary>
    public const string Video = "m.video";
    /// <summary>Geographic location.</summary>
    public const string Location = "m.location";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#mroommember">Membership</see> states of <see cref="MemberContent.Membership"/>.</summary>
public static class Memberships
{
    /// <summary>User is in the room.</summary>
    public const string Join = "join";
    /// <summary>User is invited.</summary>
    public const string Invite = "invite";
    /// <summary>User left or was kicked.</summary>
    public const string Leave = "leave";
    /// <summary>User is banned.</summary>
    public const string Ban = "ban";
    /// <summary>User asked to join.</summary>
    public const string Knock = "knock";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#presence">Presence</see> states.</summary>
public static class PresenceStates
{
    /// <summary>User is active.</summary>
    public const string Online = "online";
    /// <summary>User is away.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>User is not connected.</summary>
    public const string Offline = "offline";
}

/// <summary>Presets of <see cref="CreateRoomRequest.Preset"/>.</summary>
public static class RoomPresets
{
    /// <summary>Invite only; guests can join.</summary>
    public const string PrivateChat = "private_chat";
    /// <summary>Like <see cref="PrivateChat"/>, invitees get the creator's power level.</summary>
    public const string TrustedPrivateChat = "trusted_private_chat";
    /// <summary>Anyone can join.</summary>
    public const string PublicChat = "public_chat";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#receipts">Receipt</see> types.</summary>
public static class ReceiptTypes
{
    /// <summary>Public read receipt.</summary>
    public const string Read = "m.read";
    /// <summary>Read receipt only visible to the user.</summary>
    public const string ReadPrivate = "m.read.private";
}
