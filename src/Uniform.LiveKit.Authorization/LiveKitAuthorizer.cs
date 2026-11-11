namespace Uniform.LiveKit.Authorization;

using System.Net;
using Microsoft.Extensions.Logging;
using Uniform.LiveKit.Protocol;

/// <summary>Exchanges Matrix OpenID tokens for LiveKit access tokens to a call's room, as lk-jwt-service does: the user is verified with their homeserver; users of full-access homeservers may publish and get the room created, others only join rooms that exist.</summary>
/// <see href="https://github.com/element-hq/lk-jwt-service"/>
/// <param name="options">The current settings, read at each request, so changes apply at once.</param>
/// <param name="verifier">Verifies OpenID tokens.</param>
/// <param name="http">The client of LiveKit's room service.</param>
/// <param name="time">Clock of the tokens.</param>
/// <param name="logger">Logger of the decisions.</param>
/// <param name="membership">When given, only users joined to the Matrix room get tokens; lk-jwt-service does not check this, as it has no room state.</param>
public sealed class LiveKitAuthorizer(Func<LiveKitAuthorizerOptions> options, IOpenIdVerifier verifier, HttpClient http, TimeProvider time, ILogger<LiveKitAuthorizer> logger, IRoomMembership? membership = null)
{
    /// <summary>How long a created room waits for its first participant.</summary>
    public static readonly TimeSpan EmptyTimeout = TimeSpan.FromMinutes(5);
    /// <summary>How long a room stays after its last participant left.</summary>
    public static readonly TimeSpan DepartureTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Return a token for <paramref name="request"/>'s member in its slot.</summary>
    public async Task<SfuResponse> AuthorizeAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        // Assert the fields
        if (string.IsNullOrEmpty(request.RoomId)) throw BadJson("The room_id is missing.");
        if (string.IsNullOrEmpty(request.SlotId)) throw BadJson("The slot_id is missing.");
        if (request.Member is not { Id.Length: > 0, ClaimedUserId.Length: > 0, ClaimedDeviceId.Length: > 0 } member) throw BadJson("The member is missing its id, claimed_user_id or claimed_device_id.");
        OpenIdToken token = AssertToken(request.OpenIdToken);

        // The member is the verified user
        string userId = await VerifyAsync(token, cancellationToken).ConfigureAwait(false);
        if (userId != member.ClaimedUserId)
        {
            logger.LogWarning("{User} claimed to be {Claimed}", userId, member.ClaimedUserId);
            throw Unauthorized();
        }
        await AssertJoinedAsync(request.RoomId, userId, cancellationToken).ConfigureAwait(false);
        // Return token
        return await IssueAsync(userId, token, request, MatrixRtcNames.Identity(userId, member.ClaimedDeviceId!, member.Id!), MatrixRtcNames.RoomAlias(request.RoomId, request.SlotId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Return a token for the legacy <c>/sfu/get</c> <paramref name="request"/>, to the room's call.</summary>
    public async Task<SfuResponse> AuthorizeLegacyAsync(LegacySfuRequest request, CancellationToken cancellationToken)
    {
        // Assert the fields
        if (string.IsNullOrEmpty(request.Room)) throw BadJson("The room is missing.");
        if (string.IsNullOrEmpty(request.DeviceId)) throw BadJson("The device_id is missing.");
        OpenIdToken token = AssertToken(request.OpenIdToken);

        // Done
        string userId = await VerifyAsync(token, cancellationToken).ConfigureAwait(false);
        await AssertJoinedAsync(request.Room, userId, cancellationToken).ConfigureAwait(false);
        return await IssueAsync(userId, token, request, MatrixRtcNames.LegacyIdentity(userId, request.DeviceId), MatrixRtcNames.RoomAlias(request.Room, MatrixRtcNames.RoomCallSlot), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Return the OpenID token when it has what verifying needs; throw M_BAD_JSON otherwise.</summary>
    private static OpenIdToken AssertToken(OpenIdToken? token) => token is { AccessToken.Length: > 0, MatrixServerName.Length: > 0 } ? token : throw BadJson("The openid_token is missing its access_token or matrix_server_name.");

    /// <summary>Return M_BAD_JSON with <paramref name="message"/>.</summary>
    private static AuthorizationException BadJson(string message) => new(HttpStatusCode.BadRequest, AuthorizationError.BadJson, message);

    /// <summary>Return M_UNAUTHORIZED, which every failed verification gives, so that it tells nothing of the reason.</summary>
    private static AuthorizationException Unauthorized() => new(HttpStatusCode.Unauthorized, AuthorizationError.Unauthorized, "The request could not be authorised.");

    /// <summary>Return the user of <paramref name="token"/>, a user of the server that issued it; throw M_UNAUTHORIZED otherwise.</summary>
    private async Task<string> VerifyAsync(OpenIdToken token, CancellationToken cancellationToken)
    {
        // Asked from the homeserver
        string? userId;
        try
        {
            userId = await verifier.VerifyAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not verify an OpenID token with {Server}", token.MatrixServerName);
            throw Unauthorized();
        }

        // A user of that very server, as another could vouch for anyone
        if (userId is null)
        {
            logger.LogWarning("{Server} did not accept an OpenID token", token.MatrixServerName);
            throw Unauthorized();
        }
        if (ServerName(userId) is not string server || !string.Equals(server, token.MatrixServerName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Server {Server} vouched for {User} of another server", token.MatrixServerName, userId);
            throw Unauthorized();
        }
        // Return user
        return userId;
    }

    /// <summary>Throw M_UNAUTHORIZED when <paramref name="userId"/> is not joined to <paramref name="roomId"/>, if membership is checked at all.</summary>
    private async Task AssertJoinedAsync(string roomId, string userId, CancellationToken cancellationToken)
    {
        // Assert checking
        if (membership is null) return;
        if (await membership.IsJoinedAsync(roomId, userId, cancellationToken).ConfigureAwait(false)) return;
        // On not joined
        logger.LogWarning("{User} asked for a call in {Room} without being joined to it", userId, roomId);
        throw Unauthorized();
    }

    /// <summary>Return the server name of a user ID <c>@local:server</c>, or null when it is not one.</summary>
    public static string? ServerName(string userId)
    {
        // After the first colon
        int colon = userId.IndexOf(':');
        // Return server
        return userId.StartsWith('@') && colon > 1 && colon < userId.Length - 1 ? userId[(colon + 1)..] : null;
    }

    /// <summary>Return a token of <paramref name="identity"/> to <paramref name="room"/>; for a full-access user, create the room first.</summary>
    /// <param name="userId">The verified user.</param>
    /// <param name="token">The user's OpenID token, whose homeserver decides the access.</param>
    /// <param name="request">The request, whose delayed leave only full access may hand over.</param>
    /// <param name="identity">The LiveKit identity.</param>
    /// <param name="room">The LiveKit room.</param>
    /// <param name="cancellationToken">Cancels creating the room.</param>
    private async Task<SfuResponse> IssueAsync(string userId, OpenIdToken token, DelayedLeave request, string identity, string room, CancellationToken cancellationToken)
    {
        // Assert a configured LiveKit
        LiveKitAuthorizerOptions settings = options();
        if (settings.Url.Length == 0 || settings.ApiKey.Length == 0 || settings.ApiSecret.Length == 0) throw new AuthorizationException(HttpStatusCode.InternalServerError, AuthorizationError.Unknown, "LiveKit is not configured.");

        // Full access creates the room, so that others may join it; delayed leaves are its alone, though this service does not send them yet
        bool full = settings.HasFullAccess(token.MatrixServerName!);
        if (!full && !string.IsNullOrEmpty(request.DelayId)) throw BadJson("Delegation of delayed events is only supported for full access users");
        if (full)
        {
            try
            {
                await new LiveKitRoomService(http, settings.Url, settings.ApiKey, settings.ApiSecret, time).CreateRoomAsync(room, EmptyTimeout, DepartureTimeout, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "Could not create LiveKit room {Room}", room);
                throw new AuthorizationException(HttpStatusCode.InternalServerError, AuthorizationError.Unknown, "Unable to create the call's room.");
            }
        }

        // Join, publish only with full access
        VideoGrant grant = new() { RoomJoin = true, RoomCreate = false, Hidden = false, Room = room, CanPublish = full, CanPublishData = full, CanSubscribe = true, CanUpdateOwnMetadata = settings.CanUpdateOwnMetadata };
        string jwt = AccessToken.Create(settings.ApiKey, identity, grant, time.GetUtcNow(), settings.TokenLifetime).Sign(settings.ApiSecret);
        logger.LogInformation("Issued a LiveKit token for {User} to room {Room}, {Access} access", userId, room, full ? "full" : "restricted");
        // Return answer
        return new SfuResponse { Url = settings.Url, Jwt = jwt };
    }
}
