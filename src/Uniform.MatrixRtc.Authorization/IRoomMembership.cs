namespace Uniform.LiveKit.Authorization;

/// <summary>Tells whether a user is joined to a Matrix room, for a service that knows the room's state, such as one inside a homeserver; <see cref="LiveKitAuthorizer"/> then gives tokens only to the room's joined members.</summary>
public interface IRoomMembership
{
    /// <summary>Return whether <paramref name="userId"/> is currently joined to <paramref name="roomId"/>; false also when the room is unknown.</summary>
    Task<bool> IsJoinedAsync(string roomId, string userId, CancellationToken cancellationToken);
}
