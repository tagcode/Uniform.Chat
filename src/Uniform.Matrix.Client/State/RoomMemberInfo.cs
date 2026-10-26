namespace Uniform.Matrix.Client.State;

using Uniform.Matrix.Protocol.Core;

/// <summary>Current membership of one user in a <see cref="Room"/>.</summary>
public sealed record RoomMemberInfo
{
    /// <summary>Full user ID.</summary>
    public required string UserId { get; init; }
    /// <summary>Membership state, see <see cref="Memberships"/>.</summary>
    public required string Membership { get; init; }
    /// <summary>Display name in the room; may be shared by others, see <see cref="Room.GetMemberDisplayName"/>.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>Whether the user is joined or invited, which counts for room names and name disambiguation.</summary>
    public bool IsActive => Membership is Memberships.Join or Memberships.Invite;
}
