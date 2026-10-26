namespace Uniform.Matrix.Client.State;

using Uniform.Matrix.Protocol.Core;

/// <summary>What one <see cref="RoomStore.Apply"/> changed, for updating a UI.</summary>
public sealed record SyncChanges
{
    /// <summary>Rooms that became joined.</summary>
    public IReadOnlyList<Room> Joined { get; init; } = [];
    /// <summary>Rooms with a new invite.</summary>
    public IReadOnlyList<Room> Invited { get; init; } = [];
    /// <summary>Rooms that became left, including kicks and bans.</summary>
    public IReadOnlyList<Room> Left { get; init; } = [];
    /// <summary>New timeline events per room, in sync order.</summary>
    public IReadOnlyList<TimelineBatch> Timeline { get; init; } = [];
    /// <summary>Rooms whose typing users changed.</summary>
    public IReadOnlyList<Room> TypingChanged { get; init; } = [];
    /// <summary>Rooms whose unread counts changed.</summary>
    public IReadOnlyList<Room> UnreadChanged { get; init; } = [];
}

/// <summary>New timeline events of one room in a sync.</summary>
public sealed record TimelineBatch
{
    /// <summary>Room the events belong to.</summary>
    public required Room Room { get; init; }
    /// <summary>Events in chronological order.</summary>
    public required IReadOnlyList<ClientEvent> Events { get; init; }
    /// <summary>True when the server skipped events before <see cref="Events"/>; older history was cleared and can be loaded with <see cref="RoomStore.BackfillAsync"/>.</summary>
    public bool Limited { get; init; }
}
