namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using System.Text.Json;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Core;

/// <summary>The open room's timeline as items: events become messages, while edits, deletions and reactions change the messages they refer to.</summary>
/// <remarks>Methods taking a <see cref="Room"/> read room state, so call them under the room store's lock.</remarks>
public sealed class MessageList
{
    private static readonly TimeSpan HeaderGap = TimeSpan.FromMinutes(5);
    private readonly string ownUserId;
    private readonly Dictionary<string, MessageViewModel> byEventId = [];

    /// <summary>Create an empty list for <paramref name="ownUserId"/>.</summary>
    public MessageList(string ownUserId)
    {
        // Own messages are marked
        this.ownUserId = ownUserId;
    }

    /// <summary>Return whether the user has blocked, i.e. ignores, a sender, whose messages are then not shown; null shows all.</summary>
    public Func<string, bool>? IsBlocked { get; init; }

    /// <summary>Items in time order, unsent messages last.</summary>
    public ObservableCollection<MessageViewModel> Items { get; } = [];

    /// <summary>Time of the oldest event shown, for loading older history.</summary>
    public long? OldestTimestamp => Items.FirstOrDefault(m => m.EventId is not null)?.Timestamp.ToUnixTimeMilliseconds();

    /// <summary>Newest event ID shown, for read markers.</summary>
    public string? NewestEventId => Items.LastOrDefault(m => m.EventId is not null)?.EventId;

    /// <summary>Replace the items with <paramref name="events"/>, oldest first.</summary>
    public void Reset(Room room, IEnumerable<ClientEvent> events)
    {
        // Start over
        Items.Clear();
        byEventId.Clear();
        Append(room, events);
    }

    /// <summary>Add newer events, oldest first, before the unsent messages.</summary>
    public void Append(Room room, IEnumerable<ClientEvent> events)
    {
        // Each event becomes an item or changes one
        int insertAt = Items.TakeWhile(m => !m.IsPending).Count();
        foreach (ClientEvent clientEvent in events)
        {
            if (clientEvent.EventId is not null && byEventId.ContainsKey(clientEvent.EventId)) continue;
            if (ApplyToExisting(room, clientEvent)) continue;
            if (Create(room, clientEvent) is not MessageViewModel message) continue;
            Items.Insert(insertAt++, message);
            byEventId[clientEvent.EventId!] = message;
        }
        UpdateHeaders();
    }

    /// <summary>Add older events, oldest first, before the current items.</summary>
    public void Prepend(Room room, IEnumerable<ClientEvent> events)
    {
        // New items first, then the changes they carry
        List<ClientEvent> older = [.. events.Where(e => e.EventId is null || !byEventId.ContainsKey(e.EventId))];
        int insertAt = 0;
        foreach (ClientEvent clientEvent in older)
        {
            if (IsChange(clientEvent) || Create(room, clientEvent) is not MessageViewModel message) continue;
            Items.Insert(insertAt++, message);
            byEventId[clientEvent.EventId!] = message;
        }
        foreach (ClientEvent clientEvent in older.Where(IsChange)) ApplyToExisting(room, clientEvent);
        UpdateHeaders();
    }

    /// <summary>Show the room's unsent messages after the timeline, replacing the previous ones.</summary>
    public void SetPending(Room room, IEnumerable<OutboxMessage> unsent)
    {
        // Drop the old unsent items
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].IsPending) Items.RemoveAt(i);
        }

        // One item per outbox message, by its content; reactions and edits show once sent
        string ownName = room.GetMemberDisplayName(ownUserId);
        foreach (OutboxMessage message in unsent)
        {
            if (message.EventType != EventTypes.RoomMessage) continue;
            MessageContent? content = JsonSerializer.Deserialize<MessageContent>(message.ContentJson, MatrixJson.Options);
            if (content?.RelatesTo?.RelType == RelationTypes.Replace) continue;
            string state = message.State == OutboxState.Failed ? $"Not sent: {message.LastError}" : "Sending…";
            Items.Add(new MessageViewModel { SenderId = ownUserId, SenderName = ownName, Timestamp = message.CreatedAt, Kind = content?.MessageType == MessageTypes.Emote ? MessageKind.Emote : MessageKind.Message, IsOwn = true, OutboxId = message.Id, Text = content?.Body ?? "", SendState = state });
        }
        UpdateHeaders();
    }

    /// <summary>Return the item of <paramref name="eventId"/>, or null.</summary>
    public MessageViewModel? Find(string eventId) => byEventId.GetValueOrDefault(eventId);

    /// <summary>Return the item for an event, or null for events that are not shown.</summary>
    private MessageViewModel? Create(Room room, ClientEvent clientEvent)
    {
        // Nothing from blocked users but their state changes; non-messages: a description, or nothing
        if (clientEvent.StateKey is null && clientEvent.Sender != ownUserId && IsBlocked?.Invoke(clientEvent.Sender) == true) return null;
        string sender = room.GetMemberDisplayName(clientEvent.Sender);
        bool own = clientEvent.Sender == ownUserId;
        if (clientEvent.Type != EventTypes.RoomMessage)
        {
            if (EventText.Describe(room, clientEvent) is not string description) return null;
            return new MessageViewModel { EventId = clientEvent.EventId, SenderId = clientEvent.Sender, SenderName = sender, Timestamp = clientEvent.Timestamp, Kind = MessageKind.Info, IsOwn = own, Text = description };
        }

        // Deleted messages keep their place
        if (EventText.GetText(clientEvent, room.GetMemberDisplayName) is not string text || !clientEvent.TryGetContent(out MessageContent? content))
            return new MessageViewModel { EventId = clientEvent.EventId, SenderId = clientEvent.Sender, SenderName = sender, Timestamp = clientEvent.Timestamp, Kind = MessageKind.Info, IsOwn = own, Text = "Message deleted", IsDeleted = true };

        // Kind by message type; attachments show their name and size
        MessageKind kind = content.MessageType switch
        {
            MessageTypes.Emote => MessageKind.Emote,
            MessageTypes.Notice => MessageKind.Notice,
            MessageTypes.Image or MessageTypes.File or MessageTypes.Audio or MessageTypes.Video => MessageKind.Attachment,
            _ => MessageKind.Message,
        };
        if (kind == MessageKind.Attachment) text = $"{content.Filename ?? text}{(content.Info?.Size is long size ? $" · {EventText.FormatSize(size)}" : "")}";
        bool mentioned = !own && (content.Mentions?.UserIds?.Contains(ownUserId) == true || content.Mentions?.Room == true);
        string? image = content.MessageType == MessageTypes.Image && MxcUri.TryParse(content.MediaUrl, out _) ? content.MediaUrl : null;
        // Return message
        return new MessageViewModel
        {
            EventId = clientEvent.EventId,
            SenderId = clientEvent.Sender,
            SenderName = sender,
            SenderAvatarUri = room.Members.TryGetValue(clientEvent.Sender, out RoomMemberInfo? member) && MxcUri.TryParse(member.AvatarUrl, out _) ? member.AvatarUrl : null,
            Timestamp = clientEvent.Timestamp,
            Kind = kind,
            IsOwn = own,
            IsHighlight = mentioned,
            ReplyQuote = Quote(room, content.RelatesTo?.InReplyTo?.EventId),
            ImageUri = image,
            Text = text,
            Html = kind == MessageKind.Attachment ? null : EventText.GetHtml(clientEvent),
            FileName = kind == MessageKind.Attachment ? content.Filename ?? content.Body : null,
            Content = kind == MessageKind.Attachment ? content : null,
            Event = clientEvent,
            Source = SourceOf(content),
            IsEdited = content.RelatesTo?.RelType == RelationTypes.Replace,
        };
    }

    /// <summary>Apply an edit, deletion or reaction to the message it refers to; return whether the event was such a change.</summary>
    private bool ApplyToExisting(Room room, ClientEvent clientEvent)
    {
        // Deletion: the message stays as deleted
        if (clientEvent.Type == EventTypes.RoomRedaction)
        {
            string? target = clientEvent.Redacts ?? (clientEvent.Content.ValueKind == JsonValueKind.Object && clientEvent.Content.TryGetProperty("redacts", out JsonElement redacts) ? redacts.GetString() : null);
            if (target is not null && byEventId.TryGetValue(target, out MessageViewModel? deleted))
            {
                deleted.Html = null;
                deleted.Text = "Message deleted";
                deleted.IsDeleted = true;
            }
            return true;
        }

        // Reaction: counted on the message
        if (clientEvent.Type == EventTypes.Reaction)
        {
            if (clientEvent.TryGetContent(out ReactionContent? reaction) && reaction.RelatesTo?.EventId is string reacted && reaction.RelatesTo.Key is string key && byEventId.TryGetValue(reacted, out MessageViewModel? message)) AddReaction(message, key, clientEvent.Sender);
            return true;
        }

        // Edit: the message shows its new text
        if (clientEvent.Type != EventTypes.RoomMessage || !clientEvent.TryGetContent(out MessageContent? content) || content.RelatesTo?.RelType != RelationTypes.Replace || content.RelatesTo.EventId is not string edited) return false;
        if (!byEventId.TryGetValue(edited, out MessageViewModel? original)) return false;
        if (EventText.GetText(clientEvent, room.GetMemberDisplayName) is string text) original.Text = text;
        original.Html = EventText.GetHtml(clientEvent);
        original.Source = SourceOf(content);
        original.IsEdited = true;
        return true;
    }

    /// <summary>Return the plain body as written: the new content of an edit, without a reply fallback quote.</summary>
    private static string SourceOf(MessageContent content) =>
        content.RelatesTo?.RelType == RelationTypes.Replace && content.NewContent?.Body is string edited ? edited : EventText.StripReplyFallback(content.Body ?? "");

    /// <summary>Return whether an event changes another one instead of being shown itself.</summary>
    private static bool IsChange(ClientEvent clientEvent) =>
        clientEvent.Type is EventTypes.RoomRedaction or EventTypes.Reaction ||
        clientEvent.Type == EventTypes.RoomMessage && clientEvent.TryGetContent(out MessageContent? content) && content.RelatesTo?.RelType == RelationTypes.Replace;

    /// <summary>Count a reaction once per sender and key.</summary>
    private static void AddReaction(MessageViewModel message, string key, string sender)
    {
        // Senders per key, shown in first-seen order
        if (!message.ReactionSenders.TryGetValue(key, out HashSet<string>? senders)) message.ReactionSenders[key] = senders = [];
        if (!senders.Add(sender)) return;
        message.Reactions = string.Join("  ", message.ReactionSenders.Select(r => $"{r.Key} {r.Value.Count}"));
    }

    /// <summary>Return <c>Bob: snippet</c> for a reply to a known message, <c>Reply</c> for an unknown one, or null for no reply.</summary>
    private string? Quote(Room room, string? replyTo)
    {
        // Assert reply
        if (replyTo is null) return null;
        // Return quote of the answered message
        return byEventId.TryGetValue(replyTo, out MessageViewModel? target) ? $"{target.SenderName}: {Snippet(target.Text)}" : "Reply";
    }

    /// <summary>Shorten text to its first line, at most <paramref name="length"/> characters.</summary>
    internal static string Snippet(string text, int length = 60)
    {
        // First line, cut with an ellipsis
        string line = text.Split('\n')[0].Trim();
        // Return snippet
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }

    /// <summary>Show the day above the first message of each day, and the sender unless the previous message is from the same sender within a few minutes.</summary>
    private void UpdateHeaders()
    {
        // Compare each item with the one before, in local time
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        MessageViewModel? previous = null;
        foreach (MessageViewModel message in Items)
        {
            DateOnly day = DateOnly.FromDateTime(message.Timestamp.LocalDateTime);
            bool newDay = previous is null || DateOnly.FromDateTime(previous.Timestamp.LocalDateTime) != day;
            message.Day = newDay ? FormatDay(day, today) : null;
            message.ShowHeader = newDay || message.IsInfo || previous!.IsInfo || previous.SenderId != message.SenderId || message.Timestamp - previous.Timestamp > HeaderGap;
            previous = message;
        }
    }

    /// <summary>Return <c>Today</c>, <c>Yesterday</c>, the weekday within a week, or the date.</summary>
    internal static string FormatDay(DateOnly day, DateOnly today) => (today.DayNumber - day.DayNumber) switch
    {
        0 => "Today",
        1 => "Yesterday",
        > 1 and < 7 => day.ToString("dddd"),
        _ when day.Year == today.Year => day.ToString("dddd d MMMM"),
        _ => day.ToString("d MMMM yyyy"),
    };
}
