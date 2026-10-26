namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

// Profiles and presence.
public sealed partial class MatrixClient
{
    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3profileuserid">global profile</see> of <paramref name="userId"/>.</summary>
    public Task<ProfileResponse> GetProfileAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<ProfileResponse>(HttpMethod.Get, $"_matrix/client/v3/profile/{Escape(userId)}", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3profileuseriddisplayname">Sets the global display name</see> of the logged-in user.</summary>
    public Task SetDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/profile/{Escape(RequireSession().UserId)}/displayname", new { displayname = displayName }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3profileuseridavatar_url">Sets the global avatar</see> of the logged-in user to an <c>mxc://</c> URI.</summary>
    public Task SetAvatarUrlAsync(string avatarUrl, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/profile/{Escape(RequireSession().UserId)}/avatar_url", new { avatar_url = avatarUrl }, authenticated: true, cancellationToken);

    /// <summary>Return the <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3presenceuseridstatus">presence</see> of <paramref name="userId"/>.</summary>
    public Task<PresenceContent> GetPresenceAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<PresenceContent>(HttpMethod.Get, $"_matrix/client/v3/presence/{Escape(userId)}/status", body: null, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3presenceuseridstatus">Sets the presence</see> of the logged-in user, like IRC <c>/away</c>.</summary>
    /// <param name="presence">Presence state, see <see cref="PresenceStates"/>.</param>
    /// <param name="statusMessage">Status message; cleared when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetPresenceAsync(string presence, string? statusMessage = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/presence/{Escape(RequireSession().UserId)}/status", new { presence, status_msg = statusMessage }, authenticated: true, cancellationToken);
}
