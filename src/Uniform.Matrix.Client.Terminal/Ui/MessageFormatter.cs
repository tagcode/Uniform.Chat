namespace Uniform.Matrix.Client.Terminal.Ui;

using System.Text.Json;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Core;

/// <summary>Kind of a timeline line, used for coloring.</summary>
public enum EntryKind
{
    /// <summary>Message from another user.</summary>
    Message,
    /// <summary>Message from the logged-in user.</summary>
    Own,
    /// <summary>Message mentioning the logged-in user.</summary>
    Highlight,
    /// <summary>Action like <c>* alice waves</c>.</summary>
    Emote,
    /// <summary>Automated message, e.g. from a bot.</summary>
    Notice,
    /// <summary>Membership, topic, name or reaction change.</summary>
    Info,
    /// <summary>Own message not yet accepted by the server.</summary>
    Pending,
    /// <summary>Own message the server rejected.</summary>
    Failed,
    /// <summary>ASCII preview line of an image.</summary>
    Preview,
}

/// <summary>One formatted timeline item, wrapped to lines by <see cref="TimelinePane"/>.</summary>
public sealed record TimelineEntry
{
    /// <summary>Text without line breaks for wrapping.</summary>
    public required string Text { get; init; }
    /// <summary>Kind for coloring.</summary>
    public required EntryKind Kind { get; init; }
    /// <summary>Server timestamp in milliseconds; orders entries and drives day separators.</summary>
    public long Timestamp { get; init; }
    /// <summary>Event ID, null for pending messages.</summary>
    public string? EventId { get; init; }
    /// <summary>Columns to indent continuation lines, so they align after the nick.</summary>
    public int Indent { get; init; }
    /// <summary>Source event, for message actions; null for pending messages.</summary>
    public ClientEvent? Event { get; init; }
    /// <summary>Content URI of an image to preview under the text.</summary>
    public string? ImageUri { get; init; }
}

/// <summary>Turns Matrix events into irssi-style timeline text; call under the <see cref="Uniform.Matrix.Client.Services.SyncProcessor"/> lock as it reads <see cref="Room"/>.</summary>
public static class MessageFormatter
{
    /// <summary>Format <paramref name="clientEvent"/> of <paramref name="room"/>, or return null for events not shown, e.g. power levels.</summary>
    /// <param name="room">Room for member names.</param>
    /// <param name="clientEvent">Event to format.</param>
    /// <param name="ownUserId">Logged-in user, for own and highlighted lines.</param>
    /// <param name="findEvent">Finds an earlier event by ID, to quote the message a reply answers; replies show no quote when null or not found.</param>
    public static TimelineEntry? Format(Room room, ClientEvent clientEvent, string ownUserId, Func<string, ClientEvent?>? findEvent = null)
    {
        // Common parts
        string time = clientEvent.Timestamp.ToLocalTime().ToString("HH:mm");
        string nick = room.GetMemberDisplayName(clientEvent.Sender);
        string? text = clientEvent.Type switch
        {
            EventTypes.RoomMessage => null,
            EventTypes.RoomEncrypted => $"<{nick}> [encrypted message; the key has not arrived]",
            _ => EventText.Describe(room, clientEvent) is string description ? $"-!- {description}" : null,
        };

        // Non-message events
        if (clientEvent.Type != EventTypes.RoomMessage) return text is { Length: > 0 } ? new TimelineEntry { Text = $"{time} {text}", Kind = EntryKind.Info, Timestamp = clientEvent.OriginServerTs, EventId = clientEvent.EventId, Indent = 6, Event = clientEvent } : null;

        // Messages
        (string body, EntryKind kind) = FormatMessage(room, clientEvent, nick, ownUserId, findEvent);
        int indent = kind == EntryKind.Emote ? time.Length + 4 : time.Length + TextWrapper.Columns(nick) + 4;
        // Return entry
        string? image = clientEvent.TryGetContent(out MessageContent? content) && content.MessageType == MessageTypes.Image && clientEvent.Unsigned?.RedactedBecause is null && MxcUri.TryParse(content.MediaUrl, out _) ? content.MediaUrl : null;
        return new TimelineEntry { Text = $"{time} {body}", Kind = kind, Timestamp = clientEvent.OriginServerTs, EventId = clientEvent.EventId, Indent = indent, Event = clientEvent, ImageUri = image };
    }

    /// <summary>Return the visible text of a message, see <see cref="EventText.GetText"/>.</summary>
    /// <param name="clientEvent">Message event.</param>
    /// <param name="userName">Display name for a user ID, used for mention pills in formatted messages.</param>
    public static string? GetText(ClientEvent clientEvent, Func<string, string?>? userName = null) => EventText.GetText(clientEvent, userName);

    /// <summary>Shorten <paramref name="text"/> to one line of at most <paramref name="maxColumns"/> columns.</summary>
    public static string Snippet(string text, int maxColumns)
    {
        // First line only
        string line = text.Split('\n')[0].Trim();
        if (TextWrapper.Columns(line) <= maxColumns) return line;
        // Cut by columns and mark
        IReadOnlyList<string> parts = TextWrapper.Wrap(line, maxColumns - 1);
        return parts[0] + "…";
    }

    /// <summary>Format a queued message of the logged-in user as pending or failed.</summary>
    public static TimelineEntry FormatOutbox(OutboxMessage message, string ownNick)
    {
        // Read content
        MessageContent? content = JsonSerializer.Deserialize<MessageContent>(message.ContentJson, MatrixJson.Options);
        string body = content?.MessageType == MessageTypes.Emote ? $" * {ownNick} {content.Body}" : $"<{ownNick}> {content?.Body}";
        string time = message.CreatedAt.ToLocalTime().ToString("HH:mm");

        // Return pending or failed line
        bool failed = message.State == OutboxState.Failed;
        string suffix = failed ? $"  [not sent: {message.LastError}]" : "  [sending…]";
        return new TimelineEntry { Text = $"{time} {body}{suffix}", Kind = failed ? EntryKind.Failed : EntryKind.Pending, Timestamp = message.CreatedAt.ToUnixTimeMilliseconds(), Indent = time.Length + TextWrapper.Columns(ownNick) + 4 };
    }

    /// <summary>Format an <see cref="EventTypes.RoomMessage"/> body and pick its kind.</summary>
    private static (string Body, EntryKind Kind) FormatMessage(Room room, ClientEvent clientEvent, string nick, string ownUserId, Func<string, ClientEvent?>? findEvent)
    {
        // Deleted message
        if (GetText(clientEvent, room.GetMemberDisplayName) is not string text || !clientEvent.TryGetContent(out MessageContent? message)) return ($"<{nick}> [message deleted]", EntryKind.Info);

        // Edits show the new text; replies quote what they answer
        bool own = clientEvent.Sender == ownUserId;
        bool mentioned = !own && (message.Mentions?.UserIds?.Contains(ownUserId) == true || message.Mentions?.Room == true);
        string edited = message.RelatesTo?.RelType == RelationTypes.Replace ? " (edited)" : "";
        string quote = FormatQuote(room, message.RelatesTo?.InReplyTo?.EventId, findEvent);
        string body = $"{quote}{text}{edited}";

        // Return body and kind by message type
        return message.MessageType switch
        {
            MessageTypes.Emote => ($" * {nick} {body}", EntryKind.Emote),
            MessageTypes.Notice => ($"-{nick}- {body}", EntryKind.Notice),
            MessageTypes.Image or MessageTypes.File or MessageTypes.Audio or MessageTypes.Video => ($"<{nick}> [{message.MessageType[2..]}: {message.Filename ?? text}{(message.Info?.Size is long size ? $", {EventText.FormatSize(size)}" : "")}]", own ? EntryKind.Own : EntryKind.Message),
            _ => ($"<{nick}> {body}", own ? EntryKind.Own : mentioned ? EntryKind.Highlight : EntryKind.Message),
        };
    }

    /// <summary>Return <c>[re nick: snippet] </c> for a reply whose target is known, otherwise <c>[re] </c> or empty.</summary>
    private static string FormatQuote(Room room, string? replyToId, Func<string, ClientEvent?>? findEvent)
    {
        // Not a reply
        if (replyToId is null) return "";
        // Target from the caller, else the in-memory timeline
        ClientEvent? target = findEvent?.Invoke(replyToId) ?? (room.TryGetEvent(replyToId, out ClientEvent? found) ? found : null);
        if (target is null || GetText(target, room.GetMemberDisplayName) is not string targetText) return "[re] ";
        // Return quote
        return $"[re {room.GetMemberDisplayName(target.Sender)}: {Snippet(targetText, 30)}] ";
    }
}
