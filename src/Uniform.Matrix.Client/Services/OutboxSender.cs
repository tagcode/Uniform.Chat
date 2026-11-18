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
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly string fileDirectory;

    /// <summary>Raised with the room ID when a message of that room is queued, sent or failed; fires on the sending thread.</summary>
    public event Action<string>? RoomChanged;

    /// <summary>Create a sender over <paramref name="store"/>.</summary>
    /// <param name="store">Queue storage.</param>
    /// <param name="options">Retry settings.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="crypto">Encryption for encrypted rooms; without it, messages to encrypted rooms are refused.</param>
    /// <param name="processor">Room state, to know which rooms are encrypted and who is in them.</param>
    /// <param name="paths">The account's folders, where files wait to be uploaded; a temporary folder without them.</param>
    public OutboxSender(ClientStore store, IOptions<MatrixOptions> options, ILogger<OutboxSender> logger, CryptoService? crypto = null, SyncProcessor? processor = null, AccountPaths? paths = null)
    {
        // Files wait in the account's folder
        fileDirectory = Path.Combine(paths?.Directory ?? Path.Combine(Path.GetTempPath(), "uniform-matrix-outbox"), "outbox");

        // Encryption dependencies; in-room verification answers through the outbox, which encrypts
        this.crypto = crypto;
        this.processor = processor;
        if (crypto is not null) crypto.RoomEventSender = async (roomId, type, content, token) => await EnqueueAsync(roomId, type, content, token);
        // Store dependencies
        this.store = store;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>Send an event now, not through the queue, encrypted where the room is, and return its event ID; for events whose ID is needed at once, such as a verification request. Failures are thrown, not retried.</summary>
    public async Task<string> SendNowAsync(MatrixClient client, string roomId, string eventType, object content, CancellationToken cancellationToken = default)
    {
        // Encrypt where needed, then send
        JsonElement json = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options);
        (string type, object body) = await EncryptIfNeededAsync(client, roomId, eventType, json, cancellationToken);
        // Return event ID
        return await client.SendEventAsync(roomId, type, body, cancellationToken: cancellationToken);
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

    /// <summary>Queue a file message to <paramref name="roomId"/>: the file is copied now, so it can be sent without network, and uploaded, encrypted where the room is, before the message is sent.</summary>
    /// <param name="roomId">The room.</param>
    /// <param name="content">The file's bytes, read to the end.</param>
    /// <param name="fileName">Its name, e.g. <c>photo.jpg</c>.</param>
    /// <param name="contentType">Its MIME type, e.g. <c>image/jpeg</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OutboxMessage> EnqueueFileAsync(string roomId, Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        // A copy of its own, then the message without its upload yet
        Directory.CreateDirectory(fileDirectory);
        string path = Path.Combine(fileDirectory, Guid.NewGuid().ToString("N"));
        long size;
        await using (FileStream copy = File.Create(path))
        {
            await content.CopyToAsync(copy, cancellationToken);
            size = copy.Length;
        }
        MessageContent message = MatrixClient.CreateFileContent(new MxcUri("localhost", "pending"), fileName, contentType, size) with { Url = null };
        // Return queued message
        OutboxMessage queued = await store.EnqueueAsync(roomId, EventTypes.RoomMessage, message, cancellationToken, path);
        RoomChanged?.Invoke(roomId);
        signal.Release();
        return queued;
    }

    /// <summary>Cancel a message not sent yet, so it is never sent, waiting for a send under way; return whether it was still unsent.</summary>
    public async Task<bool> CancelAsync(long id, CancellationToken cancellationToken = default)
    {
        // Not while it may be sending
        await sending.WaitAsync(cancellationToken);
        try
        {
            if (await store.DeleteUnsentAsync(id, cancellationToken) is not OutboxMessage removed) return false;
            if (removed.FilePath is string path) File.Delete(path);
            RoomChanged?.Invoke(removed.RoomId);
            // Done
            return true;
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>Replace the content of a message not sent yet, waiting for a send under way; a failed one is tried again. Return whether it was still unsent.</summary>
    public async Task<bool> EditAsync(long id, object content, CancellationToken cancellationToken = default)
    {
        // Not while it may be sending
        await sending.WaitAsync(cancellationToken);
        try
        {
            if (await store.GetOutboxAsync(id, cancellationToken) is not OutboxMessage message || !await store.UpdateUnsentAsync(id, content, cancellationToken)) return false;
            RoomChanged?.Invoke(message.RoomId);
            signal.Release();
            // Done
            return true;
        }
        finally
        {
            sending.Release();
        }
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
        foreach (OutboxMessage listed in await store.GetPendingAsync(cancellationToken))
        {
            // As stored now, as it may have been cancelled or edited meanwhile, and not while that happens
            await sending.WaitAsync(cancellationToken);
            try
            {
                if (await store.GetOutboxAsync(listed.Id, cancellationToken) is not { State: OutboxState.Pending } message) continue;
                if (!await TrySendAsync(client, message, cancellationToken)) break;
                sent++;
            }
            finally
            {
                sending.Release();
            }
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
            if (message.FilePath is not null) message = await UploadAsync(client, message, cancellationToken);
            JsonElement content = JsonDocument.Parse(message.ContentJson).RootElement;
            (string eventType, object body) = await EncryptIfNeededAsync(client, message.RoomId, message.EventType, content, cancellationToken);
            string eventId = await client.SendEventAsync(message.RoomId, eventType, body, message.TransactionId, cancellationToken);
            await store.MarkSentAsync(message.Id, eventId, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            logger.LogInformation("Sent outbox message {Id} to {RoomId} as {EventId}", message.Id, message.RoomId, eventId);
            return true;
        }
        catch (EncryptionUnavailableException ex)
        {
            logger.LogWarning("Outbox message {Id} to encrypted room {RoomId} not sent: {Error}", message.Id, message.RoomId, ex.Describe());
            await store.MarkAttemptFailedAsync(message.Id, $"encryption unavailable: {ex.Message}", permanent: true, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning("Outbox message {Id} to {RoomId} not sent: its file is gone", message.Id, message.RoomId);
            await store.MarkAttemptFailedAsync(message.Id, "the file is gone", permanent: true, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            return true;
        }
        catch (MatrixException ex) when (IsPermanent(ex))
        {
            logger.LogWarning("Outbox message {Id} to {RoomId} rejected: {Error}", message.Id, message.RoomId, ex.Describe());
            await store.MarkAttemptFailedAsync(message.Id, ex.Message, permanent: true, cancellationToken);
            RoomChanged?.Invoke(message.RoomId);
            return true;
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("Outbox message {Id} to {RoomId} will be retried: {Error}", message.Id, message.RoomId, ex.Describe());
            await store.MarkAttemptFailedAsync(message.Id, ex.Message, permanent: false, cancellationToken);
            return false;
        }
    }

    /// <summary>Upload a message's file, encrypted where the room is, record the content that refers to the upload, and delete the copy; return the message as it is now.</summary>
    /// <exception cref="FileNotFoundException">When the copy is gone, which fails the message.</exception>
    private async Task<OutboxMessage> UploadAsync(MatrixClient client, OutboxMessage message, CancellationToken cancellationToken)
    {
        // The copy and the content waiting for it
        string path = message.FilePath!;
        MessageContent content = JsonSerializer.Deserialize<MessageContent>(message.ContentJson, MatrixJson.Options)!;
        string contentType = content.Info?.Mimetype ?? "application/octet-stream";
        bool encrypted = processor?.Read(r => r?.TryGetRoom(message.RoomId, out Room? room) == true && room.IsEncrypted) == true;

        // Encrypted rooms get an encrypted upload, others the file as it is
        if (encrypted)
        {
            EncryptedFile file = await client.UploadEncryptedAsync(await File.ReadAllBytesAsync(path, cancellationToken), cancellationToken);
            content = content with { File = file, Url = null };
        }
        else
        {
            await using FileStream stream = File.OpenRead(path);
            MxcUri uri = await client.UploadMediaAsync(stream, contentType, content.Filename ?? content.Body, cancellationToken);
            content = content with { Url = uri.ToString() };
        }

        // Recorded before the copy goes, so a retry sends rather than uploads again
        string json = JsonSerializer.Serialize(content, MatrixJson.Options);
        await store.MarkUploadedAsync(message.Id, json, cancellationToken);
        File.Delete(path);
        logger.LogInformation("Uploaded the file of outbox message {Id}", message.Id);
        // Return the message as stored
        message.ContentJson = json;
        message.FilePath = null;
        return message;
    }

    /// <summary>Return the event to send: encrypted for encrypted rooms, after loading the full member list so every device gets the key; unchanged otherwise.</summary>
    /// <remarks>Throws when the room is encrypted but encryption is unavailable, which fails the message instead of sending it in the clear.</remarks>
    private async Task<(string EventType, object Content)> EncryptIfNeededAsync(MatrixClient client, string roomId, string eventType, JsonElement content, CancellationToken cancellationToken)
    {
        // Plain rooms send as is
        bool encrypted = processor?.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.IsEncrypted) == true;
        if (!encrypted) return (eventType, content);
        if (crypto is null || !crypto.Available) throw new EncryptionUnavailableException(crypto?.UnavailableReason ?? "Encryption is not configured.");

        // All members must be known, lazy loading omits some
        bool partial = processor!.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.Members.Values.Count(m => m.IsActive) < room.JoinedMemberCount + room.InvitedMemberCount);
        if (partial) await processor.LoadMembersAsync(client, roomId, cancellationToken);
        (List<string> members, EncryptionContent? settings) = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true
            ? ([.. room.Members.Values.Where(m => m.IsActive).Select(m => m.UserId)], room.GetStateContent<EncryptionContent>(EventTypes.RoomEncryption))
            : (new List<string>(), (EncryptionContent?)null));

        // Encrypt
        EncryptedContent encryptedContent = await crypto.EncryptAsync(roomId, eventType, content, members, settings, cancellationToken);
        // Return encrypted event
        return (EventTypes.RoomEncrypted, encryptedContent);
    }

    /// <summary>Whether retrying cannot help: client errors other than rate limiting, e.g. not joined or no permission.</summary>
    private static bool IsPermanent(MatrixException exception) => exception.StatusCode < HttpStatusCode.InternalServerError && exception.Errcode != ErrorCodes.LimitExceeded;
}

/// <summary>A message for an encrypted room cannot be encrypted, so it is not sent.</summary>
public sealed class EncryptionUnavailableException(string message) : Exception(message);
