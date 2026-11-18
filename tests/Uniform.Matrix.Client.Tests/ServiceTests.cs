namespace Uniform.Matrix.Client.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Tests;

/// <summary>Unit tests for <see cref="OutboxSender"/>, <see cref="MediaCache"/> and <see cref="AccountPaths"/>.</summary>
public class ServiceTests
{
    private const string RoomId = "!r:hs.example";
    private static readonly MatrixSession Session = new() { Homeserver = new Uri("https://hs.example/"), UserId = "@me:hs.example", DeviceId = "D", AccessToken = "tok" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Create a sender over <paramref name="store"/>.</summary>
    private static OutboxSender CreateSender(ClientStore store) => new(store, Options.Create(new MatrixOptions()), NullLogger<OutboxSender>.Instance);

    /// <summary>Pending messages are sent in order with their stored transaction IDs.</summary>
    [Fact]
    public async Task CanSendPendingWithStoredTransactionIds()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxSender sender = CreateSender(store);
        OutboxMessage first = await sender.EnqueueTextAsync(RoomId, "a", Ct);
        OutboxMessage second = await sender.EnqueueTextAsync(RoomId, "b", Ct);
        int counter = 0;
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, $$"""{"event_id":"$e{{++counter}}"}"""));

        // Act
        int sent = await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct);

        // Assert
        Assert.Equal(2, sent);
        Assert.EndsWith($"/send/m.room.message/{first.TransactionId}", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.EndsWith($"/send/m.room.message/{second.TransactionId}", handler.Requests[1].Request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"msgtype":"m.text","body":"a"}""", handler.Requests[0].Body);
        Assert.Empty(await store.GetPendingAsync(Ct));
    }

    /// <summary>Unsent messages can be cancelled and edited without network; the edited one is sent as edited, the cancelled one never, and a sent one can no longer be changed.</summary>
    [Fact]
    public async Task CancelAndEditUnsent()
    {
        // Three queued while offline
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxSender sender = CreateSender(store);
        OutboxMessage kept = await sender.EnqueueTextAsync(RoomId, "typo", Ct);
        OutboxMessage cancelled = await sender.EnqueueTextAsync(RoomId, "never mind", Ct);
        OutboxMessage plain = await sender.EnqueueTextAsync(RoomId, "plain", Ct);

        // Act: edit one, cancel one, then the network comes back
        Assert.True(await sender.EditAsync(kept.Id, new MessageContent { MessageType = MessageTypes.Text, Body = "fixed" }, Ct));
        Assert.True(await sender.CancelAsync(cancelled.Id, Ct));
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.OK, """{"event_id":"$sent"}"""));
        int sent = await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct);

        // Assert: the edit sent, the cancelled one not; once sent, nothing changes them
        Assert.Equal(2, sent);
        Assert.Equal(["""{"msgtype":"m.text","body":"fixed"}""", """{"msgtype":"m.text","body":"plain"}"""], handler.Requests.Select(r => r.Body));
        Assert.False(await sender.CancelAsync(plain.Id, Ct));
        // Done
        Assert.False(await sender.EditAsync(kept.Id, new MessageContent { MessageType = MessageTypes.Text, Body = "late" }, Ct));
    }

    /// <summary>A file queued without network is copied at once, uploaded before its message when the network is back, and its copy deleted; the message refers to the upload.</summary>
    [Fact]
    public async Task SendFileQueuedOffline()
    {
        // Queued offline, from a stream that is gone afterwards
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxSender sender = CreateSender(store);
        OutboxMessage queued;
        using (MemoryStream picture = new([1, 2, 3, 4])) queued = await sender.EnqueueFileAsync(RoomId, picture, "photo.png", "image/png", Ct);
        string copy = (await store.GetOutboxAsync(queued.Id, Ct))!.FilePath!;
        Assert.True(File.Exists(copy));

        // Act: back online, the upload then the message
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath.Contains("/upload")
            ? StubHandler.Json(HttpStatusCode.OK, """{"content_uri":"mxc://hs.example/abc"}""")
            : StubHandler.Json(HttpStatusCode.OK, """{"event_id":"$file"}"""));
        Assert.Equal(1, await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct));

        // Done: uploaded, sent with its URI and size, and the copy gone
        Assert.Contains("/upload", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"url\":\"mxc://hs.example/abc\"", handler.Requests[1].Body);
        Assert.Contains("\"msgtype\":\"m.image\"", handler.Requests[1].Body);
        Assert.Contains("\"size\":4", handler.Requests[1].Body);
        Assert.False(File.Exists(copy));
    }

    /// <summary>Transient failure keeps the message and stops the pass so later messages do not overtake it.</summary>
    [Fact]
    public async Task CanKeepOrderOnTransientFailure()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxSender sender = CreateSender(store);
        await sender.EnqueueTextAsync(RoomId, "a", Ct);
        await sender.EnqueueTextAsync(RoomId, "b", Ct);
        StubHandler handler = new(_ => StubHandler.Json(HttpStatusCode.BadGateway, """{"errcode":"M_UNKNOWN"}"""));

        // Act
        int sent = await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct);

        // Assert
        Assert.Equal(0, sent);
        Assert.Single(handler.Requests);
        IReadOnlyList<OutboxMessage> pending = await store.GetPendingAsync(Ct);
        Assert.Equal(2, pending.Count);
        Assert.Equal(1, pending[0].Attempts);
    }

    /// <summary>Permanent rejection fails the message and continues with the next.</summary>
    [Fact]
    public async Task CanSkipPermanentlyRejectedMessage()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        OutboxSender sender = CreateSender(store);
        await sender.EnqueueTextAsync(RoomId, "a", Ct);
        await sender.EnqueueTextAsync(RoomId, "b", Ct);
        int calls = 0;
        StubHandler handler = new(_ => ++calls == 1
            ? StubHandler.Json(HttpStatusCode.Forbidden, """{"errcode":"M_FORBIDDEN","error":"not in room"}""")
            : StubHandler.Json(HttpStatusCode.OK, """{"event_id":"$ok"}"""));

        // Act
        int sent = await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct);

        // Assert
        Assert.Equal(2, sent);
        OutboxMessage failed = Assert.Single(await store.GetUnsentAsync(RoomId, Ct));
        Assert.Equal(OutboxState.Failed, failed.State);
        Assert.Contains("not in room", failed.LastError);
    }

    /// <summary>Media is downloaded once, then served from disk.</summary>
    [Fact]
    public async Task CanCacheMedia()
    {
        // Arrange
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"matrix-terminal-tests-{Guid.NewGuid():N}");
        AccountPaths paths = new(Options.Create(new MatrixOptions { UserId = "@me:hs.example" }), Options.Create(new StorageOptions { DataDirectory = dataDirectory }));
        MediaCache cache = new(store, paths, TimeProvider.System);
        StubHandler handler = new(_ =>
        {
            ByteArrayContent content = new([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = "\"cat.png\"" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        MatrixClient client = new(new HttpClient(handler), Session);
        try
        {
            // Act
            StoredMedia first = await cache.GetAsync(client, MxcUri.Parse("mxc://hs.example/abc"), Ct);
            StoredMedia second = await cache.GetAsync(client, MxcUri.Parse("mxc://hs.example/abc"), Ct);

            // Assert
            Assert.Single(handler.Requests);
            Assert.Equal("/_matrix/client/v1/media/download/hs.example/abc", handler.Requests[0].Request.RequestUri!.AbsolutePath);
            Assert.Equal(("image/png", "cat.png", 3L), (second.ContentType, second.FileName, second.Size));
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(cache.GetFullPath(first), Ct));
        }
        finally
        {
            // Remove files
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>User IDs map to safe per-account directory names.</summary>
    [Theory]
    [InlineData("@alice:matrix.example.org", "alice_matrix.example.org")]
    [InlineData("@a/b:example.org:8448", "a_b_example.org_8448")]
    public void CanNameAccountDirectory(string userId, string expected)
    {
        // Act and assert
        Assert.Equal(expected, AccountPaths.ToDirectoryName(UserId.Parse(userId)));
    }

    /// <summary>A message to an encrypted room is refused, not sent in the clear, when encryption is unavailable.</summary>
    [Fact]
    public async Task CanRefusePlaintextInEncryptedRoom()
    {
        // Arrange: room state says encrypted; encryption never started
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        SyncProcessor processor = new(store);
        ClientEvent encryption = new() { Type = EventTypes.RoomEncryption, Sender = Session.UserId, StateKey = "", EventId = "$enc", OriginServerTs = 1, Content = JsonSerializer.SerializeToElement(new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm }, MatrixJson.Options) };
        await processor.ProcessAsync(new SyncResponse { NextBatch = "b", Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { [RoomId] = new() { State = new EventList<ClientEvent> { Events = [encryption] } } } } }, Session.UserId, Ct);
        CryptoService crypto = new(new EfCryptoStore(db), new AccountPaths(Options.Create(new MatrixOptions { UserId = Session.UserId }), Options.Create(new StorageOptions { DataDirectory = Path.GetTempPath() })), NullLogger<CryptoService>.Instance);
        OutboxSender sender = new(store, Options.Create(new MatrixOptions()), NullLogger<OutboxSender>.Instance, crypto, processor);
        await sender.EnqueueTextAsync(RoomId, "secret", Ct);
        StubHandler handler = new(_ => throw new InvalidOperationException("nothing may be sent"));

        // Act
        await sender.SendPendingAsync(new MatrixClient(new HttpClient(handler), Session), Ct);

        // Assert
        Assert.Empty(handler.Requests);
        OutboxMessage failed = Assert.Single(await store.GetUnsentAsync(RoomId, Ct));
        Assert.Equal(OutboxState.Failed, failed.State);
        Assert.Contains("encryption unavailable", failed.LastError);
    }
}
