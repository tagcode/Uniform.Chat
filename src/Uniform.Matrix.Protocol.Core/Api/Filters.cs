namespace Uniform.Matrix.Protocol.Core;

using System.Text.Json;

/// <summary>Sync <see href="https://spec.matrix.org/latest/client-server-api/#filtering">filter</see>; upload with <c>MatrixClient.CreateFilterAsync</c> or pass <see cref="ToJson"/> inline.</summary>
/// <example><code><![CDATA[
/// Filter filter = new() { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 50 } } };
/// SyncResponse sync = await client.SyncAsync(filter: filter.ToJson());
/// ]]></code></example>
public sealed record Filter
{
    /// <summary>Fields to include in events, e.g. <c>content.body</c>.</summary>
    public IReadOnlyList<string>? EventFields { get; init; }
    /// <summary><c>client</c> or <c>federation</c>.</summary>
    public string? EventFormat { get; init; }
    /// <summary>Presence events to include.</summary>
    public EventFilter? Presence { get; init; }
    /// <summary>Global account data to include.</summary>
    public EventFilter? AccountData { get; init; }
    /// <summary>Room data to include.</summary>
    public RoomFilter? Room { get; init; }

    /// <summary>Serialize the filter for inline use.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MatrixJson.Options);
}

/// <summary>Filter for non-room events.</summary>
public sealed record EventFilter
{
    /// <summary>Maximum number of events.</summary>
    public int? Limit { get; init; }
    /// <summary>Event types to include; <c>*</c> wildcards allowed.</summary>
    public IReadOnlyList<string>? Types { get; init; }
    /// <summary>Event types to exclude.</summary>
    public IReadOnlyList<string>? NotTypes { get; init; }
    /// <summary>Senders to include.</summary>
    public IReadOnlyList<string>? Senders { get; init; }
    /// <summary>Senders to exclude.</summary>
    public IReadOnlyList<string>? NotSenders { get; init; }
}

/// <summary>Filter for room data.</summary>
public sealed record RoomFilter
{
    /// <summary>Room IDs to include.</summary>
    public IReadOnlyList<string>? Rooms { get; init; }
    /// <summary>Room IDs to exclude.</summary>
    public IReadOnlyList<string>? NotRooms { get; init; }
    /// <summary>Whether to include left rooms.</summary>
    public bool? IncludeLeave { get; init; }
    /// <summary>Timeline events to include.</summary>
    public RoomEventFilter? Timeline { get; init; }
    /// <summary>State events to include.</summary>
    public RoomEventFilter? State { get; init; }
    /// <summary>Ephemeral events to include.</summary>
    public RoomEventFilter? Ephemeral { get; init; }
    /// <summary>Room account data to include.</summary>
    public RoomEventFilter? AccountData { get; init; }
}

/// <summary>Filter for room events.</summary>
public sealed record RoomEventFilter
{
    /// <summary>Maximum number of events.</summary>
    public int? Limit { get; init; }
    /// <summary>Event types to include; <c>*</c> wildcards allowed.</summary>
    public IReadOnlyList<string>? Types { get; init; }
    /// <summary>Event types to exclude.</summary>
    public IReadOnlyList<string>? NotTypes { get; init; }
    /// <summary>Senders to include.</summary>
    public IReadOnlyList<string>? Senders { get; init; }
    /// <summary>Senders to exclude.</summary>
    public IReadOnlyList<string>? NotSenders { get; init; }
    /// <summary>Room IDs to include.</summary>
    public IReadOnlyList<string>? Rooms { get; init; }
    /// <summary>Room IDs to exclude.</summary>
    public IReadOnlyList<string>? NotRooms { get; init; }
    /// <summary>Send only member events of timeline senders, see <see href="https://spec.matrix.org/latest/client-server-api/#lazy-loading-room-members">lazy loading</see>.</summary>
    public bool? LazyLoadMembers { get; init; }
    /// <summary>Whether to include events with a <c>url</c> in content.</summary>
    public bool? ContainsUrl { get; init; }
}

/// <summary>Response of <see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3useruseridfilter">POST /_matrix/client/v3/user/{userId}/filter</see>.</summary>
public sealed record FilterIdResponse
{
    /// <summary>ID to pass as <c>filter</c> in sync.</summary>
    public required string FilterId { get; init; }
}
