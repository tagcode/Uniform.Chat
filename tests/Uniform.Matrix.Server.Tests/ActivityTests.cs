namespace Uniform.Matrix.Server.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary>Tests of account data, tags, read markers, receipts and typing, through our own client and sync.</summary>
public class ActivityTests
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

    /// <summary>Create a room of <paramref name="alice"/> that <paramref name="bob"/> joins.</summary>
    private static async Task<string> SharedRoomAsync(MatrixClient alice, MatrixClient bob)
    {
        // Public room, so joining needs no invite
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Preset = RoomPresets.PublicChat }, Ct);
        await bob.JoinRoomAsync(roomId, cancellationToken: Ct);
        // Return room
        return roomId;
    }

    /// <summary>Global and room account data are stored, read back, and synced once per change; server-controlled types are refused.</summary>
    [Fact]
    public async Task AccountData()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);
        SyncResponse initial = await alice.SyncAsync(cancellationToken: Ct);

        // Act
        await alice.SetAccountDataAsync("org.example.settings", new Dictionary<string, int> { ["volume"] = 3 }, Ct);
        await alice.SetRoomAccountDataAsync(roomId, "org.example.draft", new Dictionary<string, string> { ["text"] = "hi" }, Ct);
        SyncResponse next = await alice.SyncAsync(initial.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse quiet = await alice.SyncAsync(next.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse fresh = await alice.SyncAsync(cancellationToken: Ct);

        // Assert
        Assert.Equal(3, (await alice.GetAccountDataAsync<JsonElement>("org.example.settings", Ct)).GetProperty("volume").GetInt32());
        Assert.Equal("hi", (await alice.GetRoomAccountDataAsync<JsonElement>(roomId, "org.example.draft", Ct)).GetProperty("text").GetString());
        Assert.Contains(next.AccountData.Events, e => e.Type == "org.example.settings");
        Assert.Contains(next.Rooms.Join[roomId].AccountData.Events, e => e.Type == "org.example.draft");
        Assert.Empty(quiet.AccountData.Events);
        Assert.Empty(quiet.Rooms.Join);
        Assert.Contains(fresh.AccountData.Events, e => e.Type == "org.example.settings");
        MatrixException controlled = await Assert.ThrowsAsync<MatrixException>(() => alice.SetRoomAccountDataAsync(roomId, "m.fully_read", new Dictionary<string, string> { ["event_id"] = "$x" }, Ct));
        Assert.Equal("M_BAD_JSON", controlled.Errcode);
        MatrixException missing = await Assert.ThrowsAsync<MatrixException>(() => alice.GetAccountDataAsync<JsonElement>("org.example.none", Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Errcode);
    }

    /// <summary>Tags are added with an order, listed, removed, and synced as <c>m.tag</c> room account data.</summary>
    [Fact]
    public async Task Tags()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        string roomId = await alice.CreateRoomAsync(new CreateRoomRequest(), Ct);

        // Act
        await alice.SetTagAsync(roomId, "m.favourite", 0.25, Ct);
        await alice.SetTagAsync(roomId, "u.work", cancellationToken: Ct);
        await alice.DeleteTagAsync(roomId, "u.work", Ct);
        TagContent tags = await alice.GetTagsAsync(roomId, Ct);
        SyncResponse sync = await alice.SyncAsync(cancellationToken: Ct);

        // Assert
        Assert.Equal(["m.favourite"], tags.Tags.Keys);
        Assert.Equal(0.25, tags.Tags["m.favourite"].Order);
        MatrixEvent tagEvent = Assert.Single(sync.Rooms.Join[roomId].AccountData.Events, e => e.Type == "m.tag");
        Assert.True(tagEvent.Content.GetProperty("tags").TryGetProperty("m.favourite", out _));
    }

    /// <summary>Read markers set the fully read marker and a public receipt; others see the receipt but never a private one.</summary>
    [Fact]
    public async Task Receipts()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await SharedRoomAsync(alice, bob);
        string first = await alice.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "one" }, cancellationToken: Ct);
        string second = await alice.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "two" }, cancellationToken: Ct);
        SyncResponse before = await alice.SyncAsync(cancellationToken: Ct);

        // Act
        await bob.SetReadMarkersAsync(roomId, second, second, Ct);
        await bob.SendReceiptAsync(roomId, first, ReceiptTypes.Read, Ct);
        await bob.SendReceiptAsync(roomId, second, "m.read.private", Ct);
        SyncResponse aliceSync = await alice.SyncAsync(before.NextBatch, TimeSpan.Zero, cancellationToken: Ct);
        SyncResponse bobSync = await bob.SyncAsync(cancellationToken: Ct);

        // Assert: alice sees bob's public receipt on the second event, not moved back
        JsonElement receipts = Assert.Single(aliceSync.Rooms.Join[roomId].Ephemeral.Events, e => e.Type == "m.receipt").Content;
        Assert.True(receipts.GetProperty(second).GetProperty("m.read").TryGetProperty(bob.UserId!, out _));
        Assert.False(receipts.TryGetProperty(first, out _));
        Assert.False(receipts.GetProperty(second).TryGetProperty("m.read.private", out _));

        // Assert: bob sees his private receipt and his read marker
        JsonElement own = Assert.Single(bobSync.Rooms.Join[roomId].Ephemeral.Events, e => e.Type == "m.receipt").Content;
        Assert.True(own.GetProperty(second).TryGetProperty("m.read.private", out _));
        Assert.Equal(second, Assert.Single(bobSync.Rooms.Join[roomId].AccountData.Events, e => e.Type == "m.fully_read").Content.GetProperty("event_id").GetString());
        MatrixException unknown = await Assert.ThrowsAsync<MatrixException>(() => bob.SendReceiptAsync(roomId, "$missing", ReceiptTypes.Read, Ct));
        Assert.Equal(ErrorCodes.NotFound, unknown.Errcode);
    }

    /// <summary>Typing wakes a waiting sync, shows the typing users, and runs out after its timeout; only members may type.</summary>
    [Fact]
    public async Task Typing()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync(Open);
        MatrixClient alice = await UserAsync(server, "alice");
        MatrixClient bob = await UserAsync(server, "bob");
        string roomId = await SharedRoomAsync(alice, bob);
        SyncResponse initial = await alice.SyncAsync(cancellationToken: Ct);

        // Act: bob starts typing while alice waits
        Stopwatch watch = Stopwatch.StartNew();
        Task<SyncResponse> waiting = alice.SyncAsync(initial.NextBatch, TimeSpan.FromSeconds(30), cancellationToken: Ct);
        await Task.Delay(200, Ct);
        await bob.SetTypingAsync(roomId, true, TimeSpan.FromMilliseconds(500), Ct);
        SyncResponse started = await waiting;

        // Act: the timeout runs out
        SyncResponse stopped = await alice.SyncAsync(started.NextBatch, TimeSpan.FromSeconds(10), cancellationToken: Ct);

        // Assert
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Sync took {watch.Elapsed}.");
        Assert.Equal([bob.UserId], TypingUsers(started, roomId));
        Assert.Empty(TypingUsers(stopped, roomId));
        MatrixClient carol = await UserAsync(server, "carol");
        MatrixException outsider = await Assert.ThrowsAsync<MatrixException>(() => carol.SetTypingAsync(roomId, true, cancellationToken: Ct));
        Assert.Equal(ErrorCodes.Forbidden, outsider.Errcode);
    }

    /// <summary>Stream positions handed out for unstored changes, such as typing, are not reused by a restarted stream.</summary>
    [Fact]
    public async Task StreamSurvivesRestart()
    {
        // Arrange
        string path = Path.Combine(Directory.CreateTempSubdirectory("matrix-stream-").FullName, "server.db");
        IDbContextFactory<ServerDbContext> contexts = new TestContexts($"Data Source={path}");
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(Ct)) await db.Database.MigrateAsync(Ct);

        // Act
        ServerStream first = new(contexts);
        await first.AdvanceAsync(Ct);
        long last = await first.AdvanceAsync(Ct);
        long restarted = await new ServerStream(contexts).GetCurrentAsync(Ct);

        // Assert
        Assert.Equal(2, last);
        Assert.Equal(last, restarted);

        // Release the pooled connection, then the file
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    /// <summary>Return the typing users of a room in a sync response.</summary>
    private static List<string?> TypingUsers(SyncResponse sync, string roomId)
    {
        // Latest m.typing of the room
        MatrixEvent? typing = sync.Rooms.Join.GetValueOrDefault(roomId)?.Ephemeral.Events.LastOrDefault(e => e.Type == "m.typing");
        // Return users
        return typing is null ? [] : [.. typing.Content.GetProperty("user_ids").EnumerateArray().Select(u => u.GetString())];
    }

    /// <summary>Contexts over one SQLite file, as the server's factory makes them.</summary>
    private sealed class TestContexts(string connection) : IDbContextFactory<ServerDbContext>
    {
        /// <summary>Create a context.</summary>
        public ServerDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(connection).Options);
    }
}
