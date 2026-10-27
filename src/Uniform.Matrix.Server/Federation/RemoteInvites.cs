namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Rooms;

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#inviting-to-a-room">Invites</see> of other servers' users: the invite is signed here, sent to the invitee's server, which signs it too, and then stored like any event.</summary>
/// <remarks>The invitee then joins through this server's <c>make_join</c> and <c>send_join</c>.</remarks>
public sealed class RemoteInvites
{
    private readonly FederationClient federation;
    private readonly RemoteServerKeys remoteKeys;
    private readonly EventSender sender;

    /// <summary>Create the service.</summary>
    public RemoteInvites(FederationClient federation, RemoteServerKeys remoteKeys, EventSender sender)
    {
        // Store dependencies
        this.federation = federation;
        this.remoteKeys = remoteKeys;
        this.sender = sender;
    }

    /// <summary>Invite <paramref name="target"/> of <paramref name="targetServer"/> to a room here, for the local user <paramref name="inviter"/>.</summary>
    public async Task InviteAsync(string inviter, string roomId, string target, string targetServer, JsonObject content, CancellationToken cancellationToken)
    {
        // Signed and allowed here first
        (JsonObject pdu, string eventId, RoomVersion version) = await sender.SignAsync(new EventDraft { RoomId = roomId, Sender = inviter, Type = EventTypes.RoomMember, StateKey = target, Content = content }, cancellationToken);
        JsonObject body = new() { ["event"] = pdu, ["room_version"] = version.Id, ["invite_room_state"] = await sender.InviteRoomStateAsync(roomId, inviter, cancellationToken) };

        // The invitee's server signs it too
        JsonNode? answer = await federation.SendAsync(HttpMethod.Put, targetServer, $"/_matrix/federation/v2/invite/{Uri.EscapeDataString(roomId)}/{Uri.EscapeDataString(eventId)}", body, cancellationToken);
        if (answer?["event"] is not JsonObject signed || EventFormat.ComputeEventId(signed, version) != eventId) throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, $"{targetServer} returned another event for the invite.");
        if (!await IsSignedByAsync(signed, version, targetServer, cancellationToken)) throw new MatrixException(HttpStatusCode.BadGateway, ErrorCodes.Unknown, $"{targetServer} did not sign the invite.");

        // Stored with both signatures, checked again against the state of now
        (_, string? error) = await sender.AcceptEventAsync(signed, sender.OwnKeys, cancellationToken);
        // On error refuse
        if (error is not null) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, error);
    }

    /// <summary>Return whether <paramref name="server"/> validly signed a PDU with any of its keys.</summary>
    private async Task<bool> IsSignedByAsync(JsonObject pdu, RoomVersion version, string server, CancellationToken cancellationToken)
    {
        // Any listed key that verifies
        foreach (string keyId in (pdu["signatures"]?[server] as JsonObject ?? []).Select(k => k.Key))
        {
            if (await remoteKeys.GetVerifyKeyAsync(server, keyId, cancellationToken) is string key && EventFormat.HasValidSignature(pdu, version, server, keyId, key)) return true;
        }
        // Return not signed
        return false;
    }
}
