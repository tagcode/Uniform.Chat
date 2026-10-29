namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

// Room history, sending, redaction, typing and read markers.
public sealed partial class MatrixClient
{
    /// <summary>Return a page of <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidmessages">room history</see>.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="from">Token to start from, e.g. <see cref="Timeline.PrevBatch"/> or <see cref="MessagesResponse.End"/>; null starts from the newest event when going <see cref="Direction.Backward"/>.</param>
    /// <param name="direction">Towards older or newer events.</param>
    /// <param name="limit">Maximum number of events; server default when null.</param>
    /// <param name="filter">Inline <see cref="RoomEventFilter"/> JSON.</param>
    /// <param name="to">Token to stop at.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MessagesResponse> GetMessagesAsync(string roomId, string? from = null, Direction direction = Direction.Backward, int? limit = null, string? filter = null, string? to = null, CancellationToken cancellationToken = default)
    {
        // Build query
        string path = WithQuery($"_matrix/client/v3/rooms/{Escape(roomId)}/messages",
        [
            ("from", from),
            ("to", to),
            ("dir", direction == Direction.Backward ? "b" : "f"),
            ("limit", limit?.ToString()),
            ("filter", filter),
        ]);
        // Return page
        return SendAsync<MessagesResponse>(HttpMethod.Get, path, body: null, authenticated: true, cancellationToken);
    }

    /// <summary>Return an event with the events <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomidcontexteventid">around it</see> and the state at it.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="eventId">Event to center on.</param>
    /// <param name="limit">Maximum number of events before and after together; server default when null.</param>
    /// <param name="filter">Inline <see cref="RoomEventFilter"/> JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ContextResponse> GetContextAsync(string roomId, string eventId, int? limit = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        // Build query
        string path = WithQuery($"_matrix/client/v3/rooms/{Escape(roomId)}/context/{Escape(eventId)}", [("limit", limit?.ToString()), ("filter", filter)]);
        // Return context
        return SendAsync<ContextResponse>(HttpMethod.Get, path, body: null, authenticated: true, cancellationToken);
    }

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3roomsroomideventeventid">one event</see> by ID.</summary>
    public Task<ClientEvent> GetEventAsync(string roomId, string eventId, CancellationToken cancellationToken = default) =>
        SendAsync<ClientEvent>(HttpMethod.Get, $"_matrix/client/v3/rooms/{Escape(roomId)}/event/{Escape(eventId)}", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3roomsroomidsendeventtypetxnid">Sends</see> a room event and returns its event ID.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="eventType">Event type, see <see cref="EventTypes"/>.</param>
    /// <param name="content">Event content; serialized with its runtime type.</param>
    /// <param name="transactionId">Reuse when retrying the same send so the server deduplicates it; generated when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> SendEventAsync(string roomId, string eventType, object content, string? transactionId = null, CancellationToken cancellationToken = default)
    {
        // Send
        string txnId = transactionId ?? NewTransactionId();
        EventIdResponse response = await SendAsync<EventIdResponse>(HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/send/{Escape(eventType)}/{Escape(txnId)}", content, authenticated: true, cancellationToken);
        // Return event ID
        return response.EventId;
    }

    /// <summary>Send an <see cref="EventTypes.RoomMessage"/> and return its event ID.</summary>
    public Task<string> SendMessageAsync(string roomId, MessageContent content, string? transactionId = null, CancellationToken cancellationToken = default) =>
        SendEventAsync(roomId, EventTypes.RoomMessage, content, transactionId, cancellationToken);

    /// <summary>Send a <see cref="MessageTypes.Text"/> message.</summary>
    public Task<string> SendTextAsync(string roomId, string text, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, new MessageContent { MessageType = MessageTypes.Text, Body = text }, cancellationToken: cancellationToken);

    /// <summary>Send a <see cref="MessageTypes.Emote"/> message, like IRC <c>/me</c>.</summary>
    public Task<string> SendEmoteAsync(string roomId, string text, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, new MessageContent { MessageType = MessageTypes.Emote, Body = text }, cancellationToken: cancellationToken);

    /// <summary>Send a <see cref="MessageTypes.Notice"/> message.</summary>
    public Task<string> SendNoticeAsync(string roomId, string text, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, new MessageContent { MessageType = MessageTypes.Notice, Body = text }, cancellationToken: cancellationToken);

    /// <summary>Send an HTML message with a plain-text fallback.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="text">Plain-text body for clients that do not render HTML.</param>
    /// <param name="html">Body in the <see href="https://spec.matrix.org/latest/client-server-api/#mroommessage-msgtypes">allowed HTML subset</see>.</param>
    /// <param name="mentions">Users or room to notify; none when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<string> SendHtmlAsync(string roomId, string text, string html, Mentions? mentions = null, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, new MessageContent { MessageType = MessageTypes.Text, Body = text, Format = MessageFormats.Html, FormattedBody = html, Mentions = mentions }, cancellationToken: cancellationToken);

    /// <summary>Send a <see href="https://spec.matrix.org/latest/client-server-api/#rich-replies">reply</see> to <paramref name="replyTo"/>, mentioning its sender.</summary>
    public Task<string> SendReplyAsync(string roomId, ClientEvent replyTo, string text, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, CreateReplyContent(replyTo, text, UserId), cancellationToken: cancellationToken);

    /// <summary>Send an <see href="https://spec.matrix.org/latest/client-server-api/#event-replacements">edit</see> replacing the text of <paramref name="eventId"/>.</summary>
    public Task<string> SendEditAsync(string roomId, string eventId, string newText, CancellationToken cancellationToken = default) =>
        SendMessageAsync(roomId, CreateEditContent(eventId, newText), cancellationToken: cancellationToken);

    /// <summary>Send a <see href="https://spec.matrix.org/latest/client-server-api/#event-annotations-and-reactions">reaction</see> <paramref name="key"/>, usually an emoji, to <paramref name="eventId"/>.</summary>
    public Task<string> SendReactionAsync(string roomId, string eventId, string key, CancellationToken cancellationToken = default) =>
        SendEventAsync(roomId, EventTypes.Reaction, CreateReactionContent(eventId, key), cancellationToken: cancellationToken);

    /// <summary>Build <see href="https://spec.matrix.org/latest/client-server-api/#rich-replies">reply</see> content that mentions the replied sender unless it is <paramref name="ownUserId"/>.</summary>
    /// <param name="replyTo">Event replied to.</param>
    /// <param name="text">Reply text.</param>
    /// <param name="ownUserId">Logged-in user, not mentioned when replying to self.</param>
    /// <param name="alsoMentioned">Further users to mention, e.g. completed nicks.</param>
    /// <param name="html">Formatted body in the <see href="https://spec.matrix.org/latest/client-server-api/#mroommessage-msgtypes">allowed HTML subset</see>; plain text only when null.</param>
    public static MessageContent CreateReplyContent(ClientEvent replyTo, string text, string? ownUserId, IEnumerable<string>? alsoMentioned = null, string? html = null)
    {
        // Mention the replied user unless replying to self
        List<string> mentioned = [.. (alsoMentioned ?? []).Append(replyTo.Sender).Where(u => u != ownUserId).Distinct()];
        // Return content
        return new MessageContent
        {
            MessageType = MessageTypes.Text,
            Body = text,
            Format = html is null ? null : MessageFormats.Html,
            FormattedBody = html,
            RelatesTo = new Relation { InReplyTo = new InReplyTo { EventId = replyTo.EventId } },
            Mentions = new Mentions { UserIds = mentioned },
        };
    }

    /// <summary>Build <see href="https://spec.matrix.org/latest/client-server-api/#event-replacements">edit</see> content replacing the text of <paramref name="eventId"/>, optionally with a formatted <paramref name="html"/> body.</summary>
    public static MessageContent CreateEditContent(string eventId, string newText, string? html = null) => new()
    {
        // Body with star is the fallback for clients that do not support edits
        MessageType = MessageTypes.Text,
        Body = $"* {newText}",
        Format = html is null ? null : MessageFormats.Html,
        FormattedBody = html is null ? null : $"* {html}",
        NewContent = new MessageContent { MessageType = MessageTypes.Text, Body = newText, Format = html is null ? null : MessageFormats.Html, FormattedBody = html },
        RelatesTo = new Relation { RelType = RelationTypes.Replace, EventId = eventId },
    };

    /// <summary>Build <see href="https://spec.matrix.org/latest/client-server-api/#event-annotations-and-reactions">reaction</see> content.</summary>
    public static ReactionContent CreateReactionContent(string eventId, string key) => new() { RelatesTo = new Relation { RelType = RelationTypes.Annotation, EventId = eventId, Key = key } };

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3roomsroomidredacteventidtxnid">Redacts</see> an event and returns the redaction's event ID.</summary>
    public async Task<string> RedactAsync(string roomId, string eventId, string? reason = null, string? transactionId = null, CancellationToken cancellationToken = default)
    {
        // Send
        string txnId = transactionId ?? NewTransactionId();
        EventIdResponse response = await SendAsync<EventIdResponse>(HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/redact/{Escape(eventId)}/{Escape(txnId)}", new { reason }, authenticated: true, cancellationToken);
        // Return event ID
        return response.EventId;
    }

    /// <summary>Set whether the user is <see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3roomsroomidtypinguserid">typing</see> in a room.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="typing">Whether the user is typing.</param>
    /// <param name="timeout">How long typing lasts unless renewed; used only when <paramref name="typing"/> is true.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetTypingAsync(string roomId, bool typing, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // Build body
        long? timeoutMs = typing ? (long)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds : null;
        // Send
        return SendAsync(HttpMethod.Put, $"_matrix/client/v3/rooms/{Escape(roomId)}/typing/{Escape(RequireSession().UserId)}", new { typing, timeout = timeoutMs }, authenticated: true, cancellationToken);
    }

    /// <summary>Send a <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidreceiptreceipttypeeventid">read receipt</see> up to <paramref name="eventId"/>.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="eventId">Last read event.</param>
    /// <param name="receiptType">Receipt type, see <see cref="ReceiptTypes"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendReceiptAsync(string roomId, string eventId, string receiptType = ReceiptTypes.Read, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/receipt/{Escape(receiptType)}/{Escape(eventId)}", new { }, authenticated: true, cancellationToken);

    /// <summary>Set the <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3roomsroomidread_markers">fully read marker</see> and optionally a read receipt.</summary>
    /// <param name="roomId">Room ID.</param>
    /// <param name="fullyRead">Event up to which the user has read everything.</param>
    /// <param name="read">Event for a public read receipt; none when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetReadMarkersAsync(string roomId, string fullyRead, string? read = null, CancellationToken cancellationToken = default)
    {
        // Build body with dotted keys
        Dictionary<string, string> body = new() { [EventTypes.FullyRead] = fullyRead };
        if (read is not null) body[ReceiptTypes.Read] = read;
        // Send
        return SendAsync(HttpMethod.Post, $"_matrix/client/v3/rooms/{Escape(roomId)}/read_markers", body, authenticated: true, cancellationToken);
    }

    /// <summary>Return <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3voipturnserver">TURN credentials</see> for calls; empty when the server has none.</summary>
    public Task<TurnServerResponse> GetTurnServerAsync(CancellationToken cancellationToken = default) =>
        SendAsync<TurnServerResponse>(HttpMethod.Get, "_matrix/client/v3/voip/turnServer", body: null, authenticated: true, cancellationToken);
}
