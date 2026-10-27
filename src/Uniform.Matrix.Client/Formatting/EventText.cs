namespace Uniform.Matrix.Client.Formatting;

using System.Text.Json;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;

/// <summary>Plain-text descriptions of room events for any user interface: message text as shown, and one-line descriptions of membership and state changes.</summary>
public static class EventText
{
    /// <summary>Return the visible text of a message: the new content of an edit, rendered from HTML when formatted, without a reply fallback quote; null for deleted or non-text events.</summary>
    /// <param name="clientEvent">Message event.</param>
    /// <param name="userName">Display name for a user ID, used for mention pills in formatted messages.</param>
    public static string? GetText(ClientEvent clientEvent, Func<string, string?>? userName = null)
    {
        // Assert message
        if (clientEvent.Unsigned?.RedactedBecause is not null || !clientEvent.TryGetContent(out MessageContent? message) || message.Body is null) return null;

        // Edits show their new content
        MessageContent shown = message.RelatesTo?.RelType == RelationTypes.Replace && message.NewContent?.Body is not null ? message.NewContent : message;
        if (shown.Format == MessageFormats.Html && shown.FormattedBody is { Length: > 0 } html) return HtmlText.ToText(html, userName);
        // Return plain text
        return ReferenceEquals(shown, message) ? StripReplyFallback(message.Body) : shown.Body;
    }

    /// <summary>Return the HTML <c>formatted_body</c> of the content <see cref="GetText"/> shows, or null for messages without formatting.</summary>
    /// <param name="clientEvent">Message event.</param>
    public static string? GetHtml(ClientEvent clientEvent)
    {
        // Assert message
        if (clientEvent.Unsigned?.RedactedBecause is not null || !clientEvent.TryGetContent(out MessageContent? message) || message.Body is null) return null;
        // Return the formatting of the shown content
        MessageContent shown = message.RelatesTo?.RelType == RelationTypes.Replace && message.NewContent?.Body is not null ? message.NewContent : message;
        return shown.Format == MessageFormats.Html && shown.FormattedBody is { Length: > 0 } html ? html : null;
    }

    /// <summary>Remove the <c>&gt; quoted</c> lines older clients put before reply text.</summary>
    public static string StripReplyFallback(string body)
    {
        // Assert quote at start
        if (!body.StartsWith("> ")) return body;
        // Drop quote lines and the blank line after them
        string[] lines = body.Split('\n');
        int first = Array.FindIndex(lines, l => !l.StartsWith('>'));
        if (first < 0) return body;
        // Return remaining text
        return string.Join('\n', lines[first..]).TrimStart('\n');
    }

    /// <summary>Format a byte count like <c>12 KB</c>.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };

    /// <summary>Describe a non-message event in one line, e.g. <c>Alice joined</c>; null for events that are not shown, and for messages.</summary>
    /// <param name="room">Room, for member names.</param>
    /// <param name="clientEvent">Event to describe.</param>
    public static string? Describe(Room room, ClientEvent clientEvent)
    {
        // Sender's name in the room
        string sender = room.GetMemberDisplayName(clientEvent.Sender);
        // Return description by type
        return clientEvent.Type switch
        {
            EventTypes.RoomEncrypted => "Encrypted message; the key has not arrived",
            EventTypes.RoomMember => DescribeMember(room, clientEvent),
            EventTypes.RoomTopic => clientEvent.TryGetContent(out TopicContent? topic) ? $"{sender} changed the topic to: {topic.Topic}" : null,
            EventTypes.RoomName => clientEvent.TryGetContent(out NameContent? name) ? $"{sender} renamed the room to {name.Name}" : null,
            EventTypes.RoomAvatar => clientEvent.TryGetContent(out RoomAvatarContent? avatar) && avatar.Url is { Length: > 0 } ? $"{sender} changed the room avatar" : $"{sender} removed the room avatar",
            EventTypes.RoomCreate => $"{sender} created the room",
            EventTypes.Reaction => clientEvent.TryGetContent(out ReactionContent? reaction) && reaction.RelatesTo?.Key is string key ? $"{sender} reacted {key}" : null,
            _ => null,
        };
    }

    /// <summary>Describe a membership change by comparing with the previous content, e.g. <c>Bob is now known as Robert</c>; null when only the avatar changed.</summary>
    /// <param name="room">Room, for member names.</param>
    /// <param name="clientEvent">Member event.</param>
    public static string? DescribeMember(Room room, ClientEvent clientEvent)
    {
        // Read new and previous membership
        if (!clientEvent.TryGetContent(out MemberContent? content) || clientEvent.StateKey is null) return null;
        MemberContent? previous = clientEvent.Unsigned?.PrevContent is JsonElement { ValueKind: JsonValueKind.Object } prev ? prev.Deserialize<MemberContent>(MatrixJson.Options) : null;
        string target = content.DisplayName ?? previous?.DisplayName ?? clientEvent.StateKey;
        string actor = room.GetMemberDisplayName(clientEvent.Sender);
        string reason = string.IsNullOrEmpty(content.Reason) ? "" : $" ({content.Reason})";

        // Name and avatar changes keep membership
        if (content.Membership == Memberships.Join && previous?.Membership == Memberships.Join)
            return previous.DisplayName != content.DisplayName ? $"{previous.DisplayName ?? clientEvent.StateKey} is now known as {target}" : null;

        // Return membership change
        return content.Membership switch
        {
            Memberships.Join => $"{target} joined",
            Memberships.Invite => $"{actor} invited {target}",
            Memberships.Leave when clientEvent.Sender == clientEvent.StateKey => previous?.Membership == Memberships.Invite ? $"{target} declined the invite" : $"{target} left{reason}",
            Memberships.Leave => previous?.Membership == Memberships.Ban ? $"{actor} unbanned {target}" : $"{actor} kicked {target}{reason}",
            Memberships.Ban => $"{actor} banned {target}{reason}",
            Memberships.Knock => $"{target} asked to join{reason}",
            _ => null,
        };
    }
}
