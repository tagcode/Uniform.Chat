namespace Uniform.Matrix.Protocol.Tests;

using System.Text.Json;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests against the real test homeserver using <see cref="TestAccount"/>; exclude with <c>dotnet test -- --filter-not-trait "Category=Live"</c>.</summary>
/// <remarks>Tests share one login via <see cref="LiveSession"/>, as the server rate-limits logins.</remarks>
[Trait("Category", "Live")]
public class LiveServerTests(LiveSession session) : IClassFixture<LiveSession>
{
    /// <summary>Server versions and identity of the shared session.</summary>
    [Fact]
    public async Task CanGetVersionsAndWhoAmI()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        MatrixClient client = session.Client;

        // Assert versions
        VersionsResponse versions = await client.GetVersionsAsync(ct);
        Assert.Contains("v1.1", versions.Versions);

        // Assert identity
        WhoAmIResponse me = await client.WhoAmIAsync(ct);
        Assert.Equal($"@{session.Account.Localpart}:{session.Account.ServerName}", me.UserId);
        Assert.Equal(client.DeviceId, me.DeviceId);
    }

    /// <summary>Create a private room, chats in it via send, messages and sync, then leave and forget it.</summary>
    [Fact]
    public async Task CanChatInNewRoom()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        MatrixClient client = session.Client;
        string? roomId = null;
        try
        {
            // Create room
            string marker = Guid.NewGuid().ToString("N");
            roomId = await client.CreateRoomAsync(new CreateRoomRequest { Name = "Uniform.Matrix.Client.Tests", Topic = marker, Preset = RoomPresets.PrivateChat }, ct);
            Assert.Contains(roomId, await client.GetJoinedRoomsAsync(ct));

            // Send messages and markers
            string textId = await client.SendTextAsync(roomId, $"hello {marker}", ct);
            await client.SendEmoteAsync(roomId, "waves", ct);
            await client.SetTypingAsync(roomId, true, TimeSpan.FromSeconds(5), ct);
            await client.SetTypingAsync(roomId, false, cancellationToken: ct);
            await client.SetReadMarkersAsync(roomId, textId, textId, ct);

            // Assert state
            TopicContent topic = await client.GetStateEventAsync<TopicContent>(roomId, EventTypes.RoomTopic, cancellationToken: ct);
            Assert.Equal(marker, topic.Topic);
            Assert.Contains(client.UserId!, (await client.GetJoinedMembersAsync(roomId, ct)).Keys);

            // Assert history, newest first
            MessagesResponse page = await client.GetMessagesAsync(roomId, limit: 10, cancellationToken: ct);
            List<MessageContent> messages = page.Chunk.Where(e => e.Type == EventTypes.RoomMessage).Select(e => e.TryGetContent(out MessageContent? m) ? m : null).OfType<MessageContent>().ToList();
            Assert.Equal(MessageTypes.Emote, messages[0].MessageType);
            Assert.Equal($"hello {marker}", messages[1].Body);
            Assert.Equal(textId, (await client.GetEventAsync(roomId, textId, ct)).EventId);

            // Assert sync sees the room
            Filter filter = new() { Room = new RoomFilter { Rooms = [roomId], Timeline = new RoomEventFilter { Limit = 10 } }, Presence = new EventFilter { NotTypes = ["*"] } };
            SyncResponse sync = await client.SyncAsync(filter: await client.CreateFilterAsync(filter, ct), cancellationToken: ct);
            JoinedRoom room = sync.Rooms.Join[roomId];
            Assert.Contains(room.Timeline.Events, e => e.EventId == textId);
        }
        finally
        {
            // Clean up room
            if (roomId is not null) await client.LeaveRoomAsync(roomId, cancellationToken: ct);
            if (roomId is not null) await client.ForgetRoomAsync(roomId, ct);
        }
    }

    /// <summary>Use replies, edits, reactions, tags, account data and directories, and track the room in a <see cref="RoomStore"/>.</summary>
    [Fact]
    public async Task CanTrackRichRoomInStore()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        MatrixClient client = session.Client;
        string? roomId = null;
        try
        {
            // Create room and rich messages
            roomId = await client.CreateRoomAsync(new CreateRoomRequest { Name = "Uniform.Matrix.Client.Tests store", Preset = RoomPresets.PrivateChat }, ct);
            string firstId = await client.SendTextAsync(roomId, "first", ct);
            ClientEvent first = await client.GetEventAsync(roomId, firstId, ct);
            string replyId = await client.SendReplyAsync(roomId, first, "reply", ct);
            string editId = await client.SendEditAsync(roomId, replyId, "reply edited", ct);
            string reactionId = await client.SendReactionAsync(roomId, firstId, "👍", ct);
            await client.SendHtmlAsync(roomId, "bold", "<b>bold</b>", cancellationToken: ct);

            // Tag and account data
            await client.SetTagAsync(roomId, RoomTags.LowPriority, cancellationToken: ct);
            Assert.Contains(RoomTags.LowPriority, (await client.GetTagsAsync(roomId, ct)).Tags.Keys);
            await client.SetAccountDataAsync("fi.dy.matrix.client.tests", new { run = roomId }, ct);
            JsonElement data = await client.GetAccountDataAsync<JsonElement>("fi.dy.matrix.client.tests", ct);
            Assert.Equal(roomId, data.GetProperty("run").GetString());

            // Directories answer
            await client.GetPublicRoomsAsync(limit: 5, cancellationToken: ct);
            UserDirectoryResponse users = await client.SearchUsersAsync(session.Account.Localpart, cancellationToken: ct);
            Assert.NotNull(users.Results);

            // Sync into a store, keeping the timeline short to exercise backfill; no room.rooms filter, as the server then omits room account data
            Filter filter = new() { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 3 } }, Presence = new EventFilter { NotTypes = ["*"] } };
            RoomStore store = new(client.UserId!);
            SyncChanges changes = store.Apply(await client.SyncAsync(filter: filter.ToJson(), cancellationToken: ct));
            Room room = store.Rooms[roomId];

            // Assert store state
            Assert.Contains(room, changes.Joined);
            Assert.Equal("Uniform.Matrix.Client.Tests store", room.DisplayName);
            Assert.Equal(Memberships.Join, room.Membership);
            Assert.Contains(RoomTags.LowPriority, room.Tags.Keys);
            Assert.NotNull(store.NextBatch);

            // Backfill until the first message
            for (int page = 0; page < 20 && !room.TryGetEvent(firstId, out _) && room.HasMoreHistory; page++) await store.BackfillAsync(client, room, 3, ct);
            Assert.True(room.TryGetEvent(replyId, out ClientEvent? reply));
            Assert.True(reply.TryGetContent(out MessageContent? replyContent));
            Assert.Equal(firstId, replyContent.RelatesTo?.InReplyTo?.EventId);
            Assert.True(room.TryGetEvent(editId, out ClientEvent? edit));
            Assert.True(edit.TryGetContent(out MessageContent? editContent));
            Assert.Equal("reply edited", editContent.NewContent?.Body);
            Assert.True(room.TryGetEvent(reactionId, out _));
        }
        finally
        {
            // Clean up room
            if (roomId is not null) await client.LeaveRoomAsync(roomId, cancellationToken: ct);
            if (roomId is not null) await client.ForgetRoomAsync(roomId, ct);
        }
    }

    /// <summary>List devices including this one, and round-trips a file through media upload and download.</summary>
    [Fact]
    public async Task CanListDevicesAndRoundTripMedia()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        MatrixClient client = session.Client;

        // Assert devices
        IReadOnlyList<Device> devices = await client.GetDevicesAsync(ct);
        Assert.Contains(devices, d => d.DeviceId == client.DeviceId);

        // Upload, then download the same bytes
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"Uniform.Matrix.Client.Tests {Guid.NewGuid()}");
        using MemoryStream upload = new(bytes);
        MxcUri uri = await client.UploadMediaAsync(upload, "text/plain", "test.txt", ct);
        using MediaDownload download = await client.DownloadMediaAsync(uri, ct);
        using MemoryStream received = new();
        await download.Content.CopyToAsync(received, ct);

        // Assert
        Assert.Equal(bytes, received.ToArray());
        Assert.StartsWith("text/plain", download.ContentType);
    }
}

/// <summary>One logged-in session shared by <see cref="LiveServerTests"/>; logs out when all tests are done.</summary>
public sealed class LiveSession : IAsyncLifetime
{
    private readonly HttpClient http = new();

    /// <summary>Test account from testclient.txt.</summary>
    public TestAccount Account { get; } = TestAccount.Load();
    /// <summary>Logged-in client.</summary>
    public MatrixClient Client { get; private set; } = null!;

    /// <summary>Discover the homeserver and log in.</summary>
    public async ValueTask InitializeAsync()
    {
        // Log in
        Client = new MatrixClient(http, await HomeserverDiscovery.DiscoverAsync(http, Account.ServerName));
        await Client.LoginWithPasswordAsync(Account.Localpart, Account.Password, "Uniform.Matrix.Client.Tests");
    }

    /// <summary>Log out, deleting the test device.</summary>
    public async ValueTask DisposeAsync()
    {
        // Log out
        if (Client?.IsLoggedIn == true) await Client.LogoutAsync();
        http.Dispose();
    }
}

/// <summary>Account parsed from testclient.txt: first line <c>@localpart:server</c>, then a <c>password: ...</c> line.</summary>
public sealed record TestAccount(string Localpart, string ServerName, string Password)
{
    /// <summary>Find and parse testclient.txt from the test binary directory upwards.</summary>
    public static TestAccount Load()
    {
        // Read non-empty lines
        List<string> lines = File.ReadAllLines(FindFile("testclient.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Parse user ID and password
        if (!UserId.TryParse(lines[0], out UserId userId)) throw new FormatException("First line of testclient.txt must be @localpart:server");
        string password = lines.First(l => l.StartsWith("password:", StringComparison.OrdinalIgnoreCase))["password:".Length..].Trim();
        // Return account
        return new TestAccount(userId.Localpart, userId.ServerName, password);
    }

    /// <summary>Return the path of <paramref name="name"/> in the binary directory or its nearest parent.</summary>
    private static string FindFile(string name)
    {
        // Walk up from binary directory
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, name);
            if (File.Exists(path)) return path;
        }
        // Not found
        throw new FileNotFoundException($"{name} not found in any parent of {AppContext.BaseDirectory}");
    }
}
