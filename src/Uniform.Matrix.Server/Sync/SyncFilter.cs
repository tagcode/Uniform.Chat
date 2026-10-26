namespace Uniform.Matrix.Server.Sync;

using System.Text.Json.Nodes;

/// <summary>The parts of a <see href="https://spec.matrix.org/latest/client-server-api/#filtering">sync filter</see> the server applies.</summary>
public sealed record SyncFilter
{
    /// <summary>Filter of clients that set none.</summary>
    public static readonly SyncFilter Default = new();

    /// <summary>Timeline events per room; 10 by default.</summary>
    public int TimelineLimit { get; init; } = 10;
    /// <summary>Rooms to include; all when null.</summary>
    public IReadOnlySet<string>? Rooms { get; init; }
    /// <summary>Rooms to leave out.</summary>
    public IReadOnlySet<string> NotRooms { get; init; } = new HashSet<string>();
    /// <summary>Event types the timeline keeps; all when null.</summary>
    public IReadOnlySet<string>? TimelineTypes { get; init; }
    /// <summary>Event types the timeline leaves out.</summary>
    public IReadOnlySet<string> TimelineNotTypes { get; init; } = new HashSet<string>();
    /// <summary>Whether state holds only the members needed for the timeline.</summary>
    public bool LazyLoadMembers { get; init; }
    /// <summary>Whether initial syncs include rooms the user has left.</summary>
    public bool IncludeLeave { get; init; }

    /// <summary>Read a filter's JSON; missing parts keep their defaults.</summary>
    public static SyncFilter Parse(JsonObject? json)
    {
        // Room filter parts
        JsonNode? room = json?["room"];
        JsonNode? timeline = room?["timeline"];
        static HashSet<string>? Set(JsonNode? node) => node is JsonArray array ? [.. array.Select(n => n!.GetValue<string>())] : null;
        bool lazy = room?["state"]?["lazy_load_members"]?.GetValue<bool>() == true || timeline?["lazy_load_members"]?.GetValue<bool>() == true;
        // Return filter
        return new SyncFilter
        {
            TimelineLimit = Math.Clamp(timeline?["limit"]?.GetValue<int>() ?? 10, 1, 500),
            Rooms = Set(room?["rooms"]),
            NotRooms = Set(room?["not_rooms"]) ?? [],
            TimelineTypes = Set(timeline?["types"]),
            TimelineNotTypes = Set(timeline?["not_types"]) ?? [],
            LazyLoadMembers = lazy,
            IncludeLeave = room?["include_leave"]?.GetValue<bool>() == true,
        };
    }

    /// <summary>Return whether the filter includes <paramref name="roomId"/>.</summary>
    public bool IncludesRoom(string roomId) => (Rooms is null || Rooms.Contains(roomId)) && !NotRooms.Contains(roomId);

    /// <summary>Return whether the timeline keeps events of <paramref name="type"/>; a trailing <c>*</c> matches prefixes.</summary>
    public bool IncludesType(string type) => (TimelineTypes is null || TimelineTypes.Any(t => Matches(t, type))) && !TimelineNotTypes.Any(t => Matches(t, type));

    /// <summary>Match a type against a pattern with an optional trailing wildcard.</summary>
    private static bool Matches(string pattern, string type) => pattern.EndsWith('*') ? type.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == type;
}
