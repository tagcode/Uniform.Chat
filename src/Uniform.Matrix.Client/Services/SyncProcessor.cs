namespace Uniform.Matrix.Client.Services;

using System.Threading.Channels;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Applies sync responses to the in-memory <see cref="RoomStore"/> and the database, and publishes the changes to the frontend.</summary>
/// <remarks><see cref="RoomStore"/> is not thread-safe, so every access goes through <see cref="Read{T}"/> or a method of this class, all under one lock that is never held across network calls.</remarks>
public sealed class SyncProcessor
{
    private readonly ClientStore store;
    private readonly CryptoService? crypto;
    private readonly Channel<SyncChanges> changes = Channel.CreateUnbounded<SyncChanges>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock gate = new();
    private RoomStore? rooms;

    /// <summary>Create a processor persisting to <paramref name="store"/>, decrypting with <paramref name="crypto"/> when given.</summary>
    public SyncProcessor(ClientStore store, CryptoService? crypto = null)
    {
        // Store dependencies
        this.store = store;
        this.crypto = crypto;
    }

    /// <summary>Raised with a room ID after stored events of it were decrypted with a late room key, so a shown log can reload.</summary>
    public event Action<string>? EventsDecrypted;

    /// <summary>Raised with the changes of each processed sync after they are applied, for services beside the frontend, e.g. calls; on the sync loop, so handlers return quickly.</summary>
    public event Action<SyncChanges>? Applied;

    /// <summary>Changes of each processed sync, in order; reads by one frontend.</summary>
    public ChannelReader<SyncChanges> Changes => changes.Reader;

    /// <summary>Persist <paramref name="sync"/>, then apply it to the room state and publishes the changes.</summary>
    /// <remarks>Persisting first means a crash can at worst re-apply a sync, which the store tolerates as events are deduplicated by ID.</remarks>
    public async Task ProcessAsync(SyncResponse sync, string ownUserId, CancellationToken cancellationToken)
    {
        // Decrypt what can be decrypted, so storage and display see plaintext
        if (crypto is not null) sync = await crypto.DecryptSyncAsync(sync, cancellationToken);

        // Persist events and position
        await store.SaveSyncAsync(sync, ownUserId, cancellationToken);

        // Apply to room state
        SyncChanges applied;
        lock (gate) applied = (rooms ??= new RoomStore(ownUserId)).Apply(sync);
        // Publish, then catch up on events that waited for keys
        Applied?.Invoke(applied);
        await changes.Writer.WriteAsync(applied, cancellationToken);
        await DecryptWithReceivedKeysAsync(cancellationToken);
    }

    /// <summary>Decrypt stored events whose room keys arrived since the last call, e.g. in this sync or from a backup restore, and announces their rooms.</summary>
    public async Task DecryptWithReceivedKeysAsync(CancellationToken cancellationToken = default)
    {
        // Assert keys arrived
        if (crypto is null) return;
        HashSet<string> updatedRooms = [];
        foreach ((string roomId, string sessionId) in crypto.TakeReceivedKeys())
        {
            foreach (ClientEvent ev in await store.GetEncryptedEventsAsync(roomId, sessionId, cancellationToken))
            {
                ClientEvent decrypted = await crypto.DecryptAsync(roomId, ev, cancellationToken);
                if (decrypted.Type == EventTypes.RoomEncrypted) continue;
                await store.ReplaceEventAsync(decrypted, cancellationToken);
                updatedRooms.Add(roomId);
            }
        }
        // Announce rooms with newly readable events
        foreach (string roomId in updatedRooms) EventsDecrypted?.Invoke(roomId);
    }

    /// <summary>Try once more to decrypt every stored encrypted event, e.g. at start, for keys that arrived while an event could not be matched; return how many were decrypted.</summary>
    public async Task<int> RetryStoredEncryptedAsync(CancellationToken cancellationToken = default)
    {
        // Assert encryption
        if (crypto is null) return 0;

        // Decrypt and replace what now decrypts
        HashSet<string> updatedRooms = [];
        int count = 0;
        foreach ((string roomId, ClientEvent ev) in await store.GetAllEncryptedEventsAsync(cancellationToken))
        {
            ClientEvent decrypted = await crypto.DecryptAsync(roomId, ev, cancellationToken);
            if (decrypted.Type == EventTypes.RoomEncrypted) continue;
            await store.ReplaceEventAsync(decrypted, cancellationToken);
            updatedRooms.Add(roomId);
            count++;
        }
        // Announce, then return count
        foreach (string roomId in updatedRooms) EventsDecrypted?.Invoke(roomId);
        return count;
    }

    /// <summary>Run <paramref name="read"/> on the room state while no sync is applied.</summary>
    public T Read<T>(Func<RoomStore?, T> read)
    {
        // Read under lock
        lock (gate) return read(rooms);
    }

    /// <summary>Load up to <paramref name="limit"/> older events of <paramref name="roomId"/> from the server, stores them and prepend them to the room; return them oldest first.</summary>
    public async Task<IReadOnlyList<ClientEvent>> BackfillAsync(MatrixClient client, string roomId, int limit, CancellationToken cancellationToken = default)
    {
        // Read token under lock
        string? token = Read(r => r?.TryGetRoom(roomId, out Room? room) == true && RoomStore.CanBackfill(room) ? room.HistoryToken : null);
        if (token is null) return [];

        // Fetch without holding the lock
        MessagesResponse page = await client.GetMessagesAsync(roomId, token, Direction.Backward, limit, cancellationToken: cancellationToken);
        if (crypto is not null)
        {
            List<ClientEvent> decrypted = [];
            foreach (ClientEvent ev in page.Chunk) decrypted.Add(await crypto.DecryptAsync(roomId, ev, cancellationToken));
            page = page with { Chunk = decrypted };
        }

        // Apply if no other backfill moved the token meanwhile
        IReadOnlyList<ClientEvent> older;
        lock (gate)
        {
            if (rooms is null || !rooms.TryGetRoom(roomId, out Room? room) || room.HistoryToken != token) return [];
            older = rooms.ApplyHistory(room, page);
        }
        // Persist and return events
        await store.SaveHistoryAsync(roomId, older, cancellationToken);
        return older;
    }

    /// <summary>Load the full member list of <paramref name="roomId"/>, which sync omits when lazy-loading.</summary>
    public async Task LoadMembersAsync(MatrixClient client, string roomId, CancellationToken cancellationToken = default)
    {
        // Fetch without holding the lock
        IReadOnlyList<ClientEvent> members = await client.GetMembersAsync(roomId, cancellationToken: cancellationToken);
        // Apply under lock
        lock (gate)
        {
            if (rooms is null || !rooms.TryGetRoom(roomId, out Room? room)) return;
            rooms.ApplyMembers(room, members);
        }
    }
}
