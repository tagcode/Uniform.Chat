namespace Uniform.Matrix.Server.Tests;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of sync, filters, room history and event context, through our own client.</summary>
public class SyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string?> Open = new() { ["Server:Registration"] = "Open" };

    /// <summary>Register a user and return its client.</summary>
    private static async Task<MatrixClient> UserAsync(TestHomeserver server, string name)
    {
        // Register logs in
        MatrixClient client = server.CreateClient();
        await client.RegisterAsync(name, "password", cancellationToken: Ct);
        // Return client
        return client;
    }

    /// <summary>Send a text message.</summary>
    private static Task<string> SayAsync(MatrixClient client, string roomId, string body) =>
        client.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = body }, cancellationToken: Ct);

    /// <summary>Return the bodies of the messages among <paramref name="events"/>.</summary>
    private static List<string?> Bodies(IEnumerable<ClientEvent> events) => [.. events.Where(e => e.Type == EventTypes.RoomMessage).Select(e => e.TryGetContent(out MessageContent? m) ? m.Body : null)];

    /// <summary>An initial sync has the joined room with its full state, the timeline, transaction IDs and member counts.</summary>
    [Fact]
    public async Task InitialSync()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" }, Ct);
        await alice.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "hello" }, "txn1", Ct);

        // Act
        SyncResponse sync = await alice.SyncAsync(cancellationToken: Ct);

        // Assert
        JoinedRoom room = sync.Rooms.Join[roomId];
        IEnumerable<ClientEvent> all = room.State.Events.Concat(room.Timeline.Events);
        Assert.Contains(all, e => e.Type == EventTypes.RoomCreate);
        Assert.Contains(all, e => e.Type == EventTypes.RoomName);
        Assert.Equal(["hello"], Bodies(room.Timeline.Events));
        Assert.Equal("txn1", room.Timeline.Events[^1].Unsigned?.TransactionId);
        Assert.Equal(1, room.Summary?.JoinedMemberCount);
        Assert.NotNull(room.Timeline.PrevBatch);
        Assert.StartsWith("s", sync.NextBatch);
    }

    /// <summary>An incremental sync returns only what is new, and an empty response with the same token when nothing is.</summary>
    [Fact]
    public async Task IncrementalSync()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        await SayAsync(alice, roomId, "first");
        SyncResponse initial = await alice.SyncAsync(cancellationToken: Ct);

        // Act
        SyncResponse quiet = await alice.SyncAsync(initial.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        await SayAsync(alice, roomId, "second");
        SyncResponse next = await alice.SyncAsync(initial.NextBatch, TimeSpan.Zero, cancellationToken: Ct);

        // Assert
        Assert.Empty(quiet.Rooms.Join);
        Assert.Equal(initial.NextBatch, quiet.NextBatch);
        JoinedRoom room = next.Rooms.Join[roomId];
        Assert.Equal(["second"], Bodies(room.Timeline.Events));
        Assert.False(room.Timeline.Limited);
        Assert.Empty(room.State.Events);
        Assert.NotEqual(initial.NextBatch, next.NextBatch);
    }

    /// <summary>A long-polling sync returns as soon as another user sends a message.</summary>
    [Fact]
    public async Task LongPoll()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Invite = [bob.UserId!] }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        SyncResponse initial = await alice.SyncAsync(cancellationToken: Ct);

        // Act
        Stopwatch watch = Stopwatch.StartNew();
        Task<SyncResponse> waiting = alice.SyncAsync(initial.NextBatch, TimeSpan.FromSeconds(30), cancellationToken: Ct);
        await Task.Delay(200, Ct);
        await SayAsync(bob, roomId, "ping");
        SyncResponse woken = await waiting;

        // Assert
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Sync took {watch.Elapsed}.");
        Assert.Equal(["ping"], Bodies(woken.Rooms.Join[roomId].Timeline.Events));
    }

    /// <summary>A room moves from invite, with stripped state, to join, to leave.</summary>
    [Fact]
    public async Task MembershipSections()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" }, Ct);
        SyncResponse before = await bob.SyncAsync(cancellationToken: Ct);

        // Act and assert: invite
        await alice.InviteAsync(roomId, bob.UserId!, cancellationToken: Ct);
        SyncResponse invited = await bob.SyncAsync(before.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        IReadOnlyList<StrippedStateEvent> stripped = invited.Rooms.Invite[roomId].InviteState.Events;
        Assert.Contains(stripped, e => e.Type == EventTypes.RoomName);
        Assert.Contains(stripped, e => e.Type == EventTypes.RoomMember && e.StateKey == bob.UserId);

        // Act and assert: join gives full state
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        SyncResponse joined = await bob.SyncAsync(invited.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        JoinedRoom room = joined.Rooms.Join[roomId];
        Assert.Contains(room.State.Events.Concat(room.Timeline.Events), e => e.Type == EventTypes.RoomCreate);
        Assert.Empty(joined.Rooms.Invite);

        // Act and assert: leave
        await bob.LeaveRoomAsync(roomId, cancellationToken: Ct);
        SyncResponse left = await bob.SyncAsync(joined.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        Assert.Contains(left.Rooms.Leave[roomId].Timeline.Events, e => e.Type == EventTypes.RoomMember && e.StateKey == bob.UserId);
        Assert.Empty(left.Rooms.Join);
    }

    /// <summary>A timeline limit marks the timeline limited; <c>prev_batch</c> pages back through the rest with <c>/messages</c>.</summary>
    [Fact]
    public async Task PaginateHistory()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        for (int i = 1; i <= 5; i++) await SayAsync(alice, roomId, $"m{i}");
        string filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 2 } } }.ToJson();

        // Act
        SyncResponse sync = await alice.SyncAsync(filter: filter, cancellationToken: Ct);
        Timeline timeline = sync.Rooms.Join[roomId].Timeline;
        MessagesResponse page = await alice.GetMessagesAsync(roomId, timeline.PrevBatch, Direction.Backward, limit: 2, cancellationToken: Ct);
        MessagesResponse rest = await alice.GetMessagesAsync(roomId, page.End, Direction.Backward, limit: 100, cancellationToken: Ct);
        MessagesResponse forward = await alice.GetMessagesAsync(roomId, page.End, Direction.Forward, limit: 100, cancellationToken: Ct);

        // Assert
        Assert.True(timeline.Limited);
        Assert.Equal(["m4", "m5"], Bodies(timeline.Events));
        Assert.Equal(["m3", "m2"], Bodies(page.Chunk));
        Assert.NotNull(page.End);
        Assert.Equal(["m1"], Bodies(rest.Chunk));
        Assert.Contains(rest.Chunk, e => e.Type == EventTypes.RoomCreate);
        Assert.Null(rest.End);
        Assert.Equal(["m2", "m3", "m4", "m5"], Bodies(forward.Chunk));
    }

    /// <summary>With lazy loading, state has only the members of the timeline's senders and the user.</summary>
    [Fact]
    public async Task LazyLoadMembers()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        MatrixClient carol = await UserAsync(server, "carol");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await carol.JoinRoomAsync(roomId, cancellationToken: Ct);
        for (int i = 0; i < 3; i++) await SayAsync(carol, roomId, $"c{i}");
        string filter = new Filter { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 3 }, State = new RoomEventFilter { LazyLoadMembers = true } } }.ToJson();

        // Act
        SyncResponse sync = await alice.SyncAsync(filter: filter, cancellationToken: Ct);

        // Assert
        JoinedRoom room = sync.Rooms.Join[roomId];
        List<string?> members = [.. room.State.Events.Where(e => e.Type == EventTypes.RoomMember).Select(e => e.StateKey).Order()];
        Assert.Contains(carol.UserId, members);
        Assert.Contains(alice.UserId, members);
        Assert.Equal(3, room.Summary?.JoinedMemberCount);
        Assert.Contains(bob.UserId!, room.Summary?.Heroes ?? []);
        Assert.Contains(room.State.Events, e => e.Type == EventTypes.RoomCreate);
    }

    /// <summary>Uploaded filters apply to sync by ID; only their owner reads them.</summary>
    [Fact]
    public async Task StoredFilters()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string kept = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        string hidden = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);

        // Act
        string filterId = await alice.CreateFilterAsync(new Filter { Room = new RoomFilter { NotRooms = [hidden] } }, Ct);
        SyncResponse sync = await alice.SyncAsync(filter: filterId, cancellationToken: Ct);
        using HttpClient http = server.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", bob.Session!.AccessToken);
        HttpResponseMessage other = await http.GetAsync($"_matrix/client/v3/user/{Uri.EscapeDataString(alice.UserId!)}/filter/{filterId}", Ct);

        // Assert
        Assert.Contains(kept, sync.Rooms.Join.Keys);
        Assert.DoesNotContain(hidden, sync.Rooms.Join.Keys);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        MatrixException unknown = await Assert.ThrowsAsync<MatrixException>(() => alice.SyncAsync(filter: "999", cancellationToken: Ct));
        Assert.Equal(ErrorCodes.NotFound, unknown.Errcode);
    }

    /// <summary>Context returns events on both sides of an event and the state at it.</summary>
    [Fact]
    public async Task EventContext()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        await SayAsync(alice, roomId, "before");
        string middle = await SayAsync(alice, roomId, "middle");
        await SayAsync(alice, roomId, "after");

        // Act
        ContextResponse context = await alice.GetContextAsync(roomId, middle, limit: 2, cancellationToken: Ct);

        // Assert
        Assert.Equal(middle, context.Event?.EventId);
        Assert.Equal(["before"], Bodies(context.EventsBefore));
        Assert.Equal(["after"], Bodies(context.EventsAfter));
        Assert.Contains(context.State, e => e.Type == EventTypes.RoomCreate);
        MatrixException missing = await Assert.ThrowsAsync<MatrixException>(() => alice.GetContextAsync(roomId, "$nope", cancellationToken: Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Errcode);
    }

    /// <summary>History sent under <c>joined</c> visibility before joining is hidden; state events carry the content they replaced.</summary>
    [Fact]
    public async Task HistoryVisibility()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat, Name = "Old" }, Ct);
        await alice.SendStateEventAsync(roomId, "m.room.history_visibility", new Dictionary<string, string> { ["history_visibility"] = "joined" }, cancellationToken: Ct);
        await SayAsync(alice, roomId, "secret");
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        await alice.SendStateEventAsync(roomId, EventTypes.RoomName, new Dictionary<string, string> { ["name"] = "New" }, cancellationToken: Ct);
        await SayAsync(alice, roomId, "welcome");

        // Act
        MessagesResponse history = await bob.GetMessagesAsync(roomId, limit: 100, cancellationToken: Ct);

        // Assert
        Assert.Equal(["welcome"], Bodies(history.Chunk));
        ClientEvent rename = history.Chunk.First(e => e.Type == EventTypes.RoomName);
        Assert.Equal("Old", rename.Unsigned?.PrevContent?.GetProperty("name").GetString());
        Assert.Contains(Bodies((await alice.GetMessagesAsync(roomId, limit: 100, cancellationToken: Ct)).Chunk), b => b == "secret");
    }
}
