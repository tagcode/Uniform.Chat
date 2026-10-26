namespace Uniform.Matrix.Server.Rooms;

using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Server.Storage;

/// <summary>Loaded state groups of one server; groups never change, so they are cached until the cache grows too large.</summary>
public sealed class StateCache
{
    private const int MaxEntries = 4096;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, IReadOnlyDictionary<(string Type, string StateKey), string>> groups = new();

    /// <summary>Return a cached group's state.</summary>
    internal bool TryGet(long groupId, out IReadOnlyDictionary<(string Type, string StateKey), string> state) => groups.TryGetValue(groupId, out state!);

    /// <summary>Cache a group's state, starting over when full.</summary>
    internal void Add(long groupId, IReadOnlyDictionary<(string Type, string StateKey), string> state)
    {
        // Simple bound: forget everything at the limit
        if (groups.Count >= MaxEntries) groups.Clear();
        groups[groupId] = state;
    }
}

/// <summary>Room state snapshots stored as changes against a parent, with a full snapshot every <see cref="MaxChainLength"/> changes so reads stay short.</summary>
internal static class StateGroups
{
    /// <summary>Longest parent chain before a full snapshot is stored.</summary>
    public const int MaxChainLength = 50;

    /// <summary>Return the state of a group through <paramref name="cache"/>; the result must not be changed.</summary>
    public static async Task<IReadOnlyDictionary<(string Type, string StateKey), string>> LoadAsync(ServerDbContext dbContext, long groupId, StateCache cache, CancellationToken cancellationToken)
    {
        // Cached, else loaded and remembered
        if (cache.TryGet(groupId, out IReadOnlyDictionary<(string, string), string> cached)) return cached;
        Dictionary<(string, string), string> state = await LoadAsync(dbContext, groupId, cancellationToken);
        cache.Add(groupId, state);
        // Return state
        return state;
    }

    /// <summary>Return the state of a group: event IDs by type and state key; empty for group 0, a room without state yet.</summary>
    public static async Task<Dictionary<(string Type, string StateKey), string>> LoadAsync(ServerDbContext dbContext, long groupId, CancellationToken cancellationToken)
    {
        // The chain from the group to its snapshot
        Dictionary<(string, string), string> state = [];
        if (groupId == 0) return state;
        List<long> chain = [];
        for (long? id = groupId; id is long current; id = (await dbContext.StateGroups.AsNoTracking().SingleAsync(g => g.Id == current, cancellationToken)).ParentId) chain.Add(current);

        // Apply from the snapshot to the group
        List<StateGroupEntryRow> entries = await dbContext.StateGroupEntries.AsNoTracking().Where(e => chain.Contains(e.GroupId)).ToListAsync(cancellationToken);
        foreach (long id in Enumerable.Reverse(chain))
            foreach (StateGroupEntryRow entry in entries.Where(e => e.GroupId == id)) state[(entry.Type, entry.StateKey)] = entry.EventId;
        // Return state
        return state;
    }

    /// <summary>Store the state of <paramref name="parentId"/> with <paramref name="changes"/> as a new group; return its ID.</summary>
    public static async Task<long> CreateAsync(ServerDbContext dbContext, string roomId, long parentId, IReadOnlyCollection<(string Type, string StateKey, string EventId)> changes, CancellationToken cancellationToken)
    {
        // Delta against the parent, or a full snapshot when the chain is long
        StateGroupRow? parent = parentId == 0 ? null : await dbContext.StateGroups.AsNoTracking().SingleAsync(g => g.Id == parentId, cancellationToken);
        bool snapshot = parent is null || parent.ChainLength + 1 >= MaxChainLength;
        Dictionary<(string, string), string> entries = snapshot ? await LoadAsync(dbContext, parentId, cancellationToken) : [];
        foreach ((string type, string stateKey, string eventId) in changes) entries[(type, stateKey)] = eventId;

        // Group, then its entries
        StateGroupRow group = new() { RoomId = roomId, ParentId = snapshot ? null : parentId, ChainLength = snapshot ? 0 : parent!.ChainLength + 1 };
        dbContext.StateGroups.Add(group);
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (((string type, string stateKey), string eventId) in entries) dbContext.StateGroupEntries.Add(new StateGroupEntryRow { GroupId = group.Id, Type = type, StateKey = stateKey, EventId = eventId });
        // Return new group
        return group.Id;
    }
}
