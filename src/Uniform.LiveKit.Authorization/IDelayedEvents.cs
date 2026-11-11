namespace Uniform.LiveKit.Authorization;

/// <summary>Acts on members' delayed events (MSC4140) at their homeservers: restarts them, or sends them at once.</summary>
/// <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4140"/>
public interface IDelayedEvents
{
    /// <summary>Restart or send delayed event <paramref name="delayId"/> of a user of <paramref name="serverName"/>: <paramref name="action"/> is <c>restart</c> or <c>send</c>; return false when the homeserver has no such delayed event any more, e.g. as it was sent or cancelled.</summary>
    /// <exception cref="HttpRequestException">When the homeserver cannot be reached, or fails otherwise.</exception>
    Task<bool> UpdateAsync(string serverName, string delayId, string action, CancellationToken cancellationToken);
}
