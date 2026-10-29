namespace Uniform.Matrix.Client.Services;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Sends queued messages in order, keeping them in the database until the server accepts them.</summary>
/// <remarks>Each message keeps its transaction ID across retries and restarts, so the server never stores a resent message twice.</remarks>
public sealed class OutboxSender
{
    private readonly ClientStore store;
    private readonly CryptoService? crypto;
    private readonly SyncProcessor? processor;
    private readonly MatrixOptions options;
    private readonly ILogger<OutboxSender> logger;
    private readonly SemaphoreSlim signal = new(0);

    /// <summary>Raised with the room ID when a message of that room is queued, sent or failed; fires on the sending thread.</summary>
    public event Action<string>? RoomChanged;

    /// <summary>Create a sender over <paramref name="store"/>.</summary>
    /// <param name="store">Queue storage.</param>
    /// <param name="options">Retry settings.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="crypto">Encryption for encrypted rooms; without it, messages to encrypted rooms are refused.</param>
    /// <param name="processor">Room state, to know which rooms are encrypted and who is in them.</param>
    public OutboxSender(ClientStore store, IOptions<MatrixOptions> options, ILogger<OutboxSender> logger, CryptoService? crypto = null, SyncProcessor? processor = null)
    {
        // Encryption dependencies
        this.crypto = crypto;
        this.processor = processor;
        // Store dependencies
        this.store = store;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>Queue a text message to <paramref name="roomId"/> and wakes the sender.</summary>
    public Task<OutboxMessage> EnqueueTextAsync(string roomId, string text, CancellationToken cancellationToken = default) =>
        EnqueueAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = text }, cancellationToken);

    /// <summary>Queue an emote, like IRC <c>/me</c>, to <paramref name="roomId"/> and wakes the sender.</summary>
    public Task<OutboxMessage> EnqueueEmoteAsync(string roomId, string text, CancellationToken cancellationToken = default) =>
        EnqueueAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Emote, Body = text }, cancellationToken);

    /// <summary>Queue an event and wakes the sender.</summary>
    public async Task<OutboxMessage> EnqueueAsync(string roomId, string eventType, object content, CancellationToken cancellationToken = default)
    {
        // Persist before sending, so it survives a crash
        OutboxMessage message = await store.EnqueueAsync(roomId, eventType, content, cancellationToken);
        RoomChanged?.Invoke(roomId);
        signal.Release();
        // Return queued message
        return message;
    }

    /// <summary>Try the pending messages again now, e.g. after the network changed, instead of after the retry interval.</summary>
    public void Wake() => signal.Release();

    /// <summary>Send pending messages until done or <paramref name="cancellationToken"/> is cancelled, retrying transient failures after <see cref="MatrixOptions.OutboxRetryInterval"/>.</summary>
    public async Task RunAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Send, then wait for a new message or the retry interval
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendPendingAsync(client, cancellationToken);
            await signal.WaitAsync(options.OutboxRetryInterval, cancellationToken);
        }
    }

    /// <summary>Send pending messages once; stop at the first transient failure to keep order. Returns the number sent.</summary>
    public async Task<int> SendPendingAsync(MatrixClient client, CancellationToken cancellationToken)
    {
        // Send oldest first
        int sent = 0;
        foreach (OutboxMessage message in await store.GetPendingAsync(cancellationToken))
        {
            if (!await TrySendAsync(client, message, cancellationToken)) break;
            sent++;
        }
        // Return count
        return sent;
    }

    /// <summary>Send one message; return false on a transient failure, true when sent or permanently failed.</summary>
    private async Task<bool> TrySendAsync(MatrixClient client, OutboxMessage message, CancellationToken cancellationToken)
    {
        // Send with the stored transaction ID
        try
        {
            JsonElement content = JsonDocument.Parse(message.ContentJson).RootElement;
            (string eventType, object body) = await EncryptIfNeededAsync(client, message, content, cancellationToken);
            string eventId = await client.SendEventAsync(message.RoomId, eventType, body, message.TransactionId, cancellationToken);
            await store.MarkSentAsync(message.Id, eventId, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            logger.LogInformation("Sent outbox message {Id} to {RoomId} as {EventId}", message.Id, message.RoomId, eventId);
            return true;
        }
        catch (EncryptionUnavailableException ex)
        {
            logger.LogWarning("Outbox message {Id} to encrypted room {RoomId} not sent: {Error}", message.Id, message.RoomId, ex.Message);
            await store.MarkAttemptFailedAsync(message.Id, $"encryption unavailable: {ex.Message}", permanent: true, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            return true;
        }
        catch (MatrixException ex) when (IsPermanent(ex))
        {
            logger.LogWarning("Outbox message {Id} to {RoomId} rejected: {Error}", message.Id, message.RoomId, ex.Message);
            await store.MarkAttemptFailedAsync(message.Id, ex.Message, permanent: true, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            return true;
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("Outbox message {Id} to {RoomId} will be retried: {Error}", message.Id, message.RoomId, ex.Message);
            await store.MarkAttemptFailedAsync(message.Id, ex.Message, permanent: false, cancellationToken);
            return false;
        }
    }

    /// <summary>Return the event to send: encrypted for encrypted rooms, after loading the full member list so every device gets the key; unchanged otherwise.</summary>
    /// <remarks>Throws when the room is encrypted but encryption is unavailable, which fails the message instead of sending it in the clear.</remarks>
    private async Task<(string EventType, object Content)> EncryptIfNeededAsync(MatrixClient client, OutboxMessage message, JsonElement content, CancellationToken cancellationToken)
    {
        // Plain rooms send as is
        bool encrypted = processor?.Read(r => r?.TryGetRoom(message.RoomId, out Room? room) == true && room.IsEncrypted) == true;
        if (!encrypted) return (message.EventType, content);
        if (crypto is null || !crypto.Available) throw new EncryptionUnavailableException(crypto?.UnavailableReason ?? "Encryption is not configured.");

        // All members must be known, lazy loading omits some
        bool partial = processor!.Read(r => r?.TryGetRoom(message.RoomId, out Room? room) == true && room.Members.Values.Count(m => m.IsActive) < room.JoinedMemberCount + room.InvitedMemberCount);
        if (partial) await processor.LoadMembersAsync(client, message.RoomId, cancellationToken);
        (List<string> members, EncryptionContent? settings) = processor.Read(r => r?.TryGetRoom(message.RoomId, out Room? room) == true
            ? ([.. room.Members.Values.Where(m => m.IsActive).Select(m => m.UserId)], room.GetStateContent<EncryptionContent>(EventTypes.RoomEncryption))
            : (new List<string>(), (EncryptionContent?)null));

        // Encrypt
        EncryptedContent encryptedContent = await crypto.EncryptAsync(message.RoomId, message.EventType, content, members, settings, cancellationToken);
        // Return encrypted event
        return (EventTypes.RoomEncrypted, encryptedContent);
    }

    /// <summary>Whether retrying cannot help: client errors other than rate limiting, e.g. not joined or no permission.</summary>
    private static bool IsPermanent(MatrixException exception) => exception.StatusCode < HttpStatusCode.InternalServerError && exception.Errcode != ErrorCodes.LimitExceeded;
}

/// <summary>A message for an encrypted room cannot be encrypted, so it is not sent.</summary>
public sealed class EncryptionUnavailableException(string message) : Exception(message);
