namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Keys;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/server-server-api/#inviting-to-a-room">Invites</see> of other servers' users: the invite is signed here, sent to the invitee's server, which signs it too, and then stored like any event.</summary>
/// <remarks>The invitee then joins through this server's <c>make_join</c> and <c>send_join</c>. Invites the other way, to this server's users, are signed here too and kept in <see cref="RemoteInviteRow"/>s until the user joins or rejects them; rejecting does not tell the other server yet.</remarks>
public sealed class RemoteInvites
{
    private readonly FederationClient federation;
    private readonly RemoteServerKeys remoteKeys;
    private readonly EventSender sender;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly ServerKeys keys;
    private readonly IOptionsMonitor<ServerOptions> server;

    /// <summary>Create the service.</summary>
    public RemoteInvites(FederationClient federation, RemoteServerKeys remoteKeys, EventSender sender, IDbContextFactory<ServerDbContext> contexts, ServerStream stream, ServerKeys keys, IOptionsMonitor<ServerOptions> server)
    {
        // Store dependencies
        this.federation = federation;
        this.remoteKeys = remoteKeys;
        this.sender = sender;
        this.contexts = contexts;
        this.stream = stream;
        this.keys = keys;
        this.server = server;
    }

    /// <summary>Accept an invite of a local user that <paramref name="origin"/> sent: check it, sign it, keep it for the user's sync, and return it signed, as <c>invite</c> v2 answers.</summary>
    /// <param name="origin">Sending server, which must be the inviter's.</param>
    /// <param name="roomId">Room from the URL.</param>
    /// <param name="eventId">Event ID from the URL.</param>
    /// <param name="body">The request: <c>event</c>, <c>room_version</c> and <c>invite_room_state</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonObject> ReceiveAsync(string origin, string roomId, string eventId, JsonObject body, CancellationToken cancellationToken)
    {
        // Assert a known version and an invite of a local user by a user of the origin
        string serverName = server.CurrentValue.ServerName;
        RoomVersion version = RoomVersion.Find(body["room_version"]?.GetValue<string>()) ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.IncompatibleRoomVersion, "The room version is not supported here.");
        if (body["event"] is not JsonObject pdu || pdu["room_id"]?.GetValue<string>() != roomId) throw Invalid("The invite is of another room.");
        string? target = pdu["state_key"]?.GetValue<string>();
        if (pdu["type"]?.GetValue<string>() != EventTypes.RoomMember || pdu["content"]?["membership"]?.GetValue<string>() != Memberships.Invite || target is null) throw Invalid("The event is not an invite.");
        if (!UserId.TryParse(target, out UserId invitee) || invitee.ServerName != serverName) throw Invalid("The invitee is not a user of this server.");
        if (!UserId.TryParse(pdu["sender"]?.GetValue<string>(), out UserId inviter) || inviter.ServerName != origin) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "The inviter is not of the sending server.");
        if (EventFormat.ComputeEventId(pdu, version) != eventId || !EventFormat.HasValidContentHash(pdu)) throw Invalid("The event ID or hash does not match the event.");
        if (!await IsSignedByAsync(pdu, version, origin, cancellationToken)) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "The invite is not signed by the sending server.");
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Users.AnyAsync(u => u.UserId == target && !u.Deactivated, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown user.");
        }

        // Signed here too, and kept until the user answers
        pdu.Remove("unsigned");
        EventFormat.Sign(pdu, version, serverName, keys.KeyId, keys.Secret);
        JsonArray stripped = body["invite_room_state"] as JsonArray ?? [];
        await stream.WriteAsync(async position =>
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            RemoteInviteRow? row = await db.RemoteInvites.SingleOrDefaultAsync(i => i.UserId == target && i.RoomId == roomId, cancellationToken);
            if (row is null) db.RemoteInvites.Add(row = new RemoteInviteRow { UserId = target, RoomId = roomId, EventId = eventId, Sender = inviter.ToString(), RoomVersion = version.Id, EventJson = "", StrippedStateJson = "", Membership = Memberships.Invite });
            (row.EventId, row.Sender, row.RoomVersion, row.EventJson, row.StrippedStateJson, row.Membership, row.Stream) = (eventId, inviter.ToString(), version.Id, pdu.ToJsonString(), stripped.ToJsonString(), Memberships.Invite, position);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
        // Return the event with both signatures
        return new JsonObject { ["event"] = pdu.DeepClone() };
    }

    /// <summary>Return the server that invited <paramref name="userId"/> to <paramref name="roomId"/>, a room not here, or null without such an invite.</summary>
    public async Task<string?> GetInvitingServerAsync(string userId, string roomId, CancellationToken cancellationToken)
    {
        // Open invite
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string? inviter = await db.RemoteInvites.AsNoTracking().Where(i => i.UserId == userId && i.RoomId == roomId && i.Membership == Memberships.Invite).Select(i => i.Sender).SingleOrDefaultAsync(cancellationToken);
        // Return its server
        return UserId.TryParse(inviter, out UserId parsed) ? parsed.ServerName : null;
    }

    /// <summary>End an invite: <c>leave</c> when rejected, which sync reports, or removed once the user has joined or the room is here; nothing without one.</summary>
    public async Task EndAsync(string userId, string roomId, bool joined, CancellationToken cancellationToken)
    {
        // Nothing to end, without taking a stream position
        await using (ServerDbContext check = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (!await check.RemoteInvites.AnyAsync(i => i.UserId == userId && i.RoomId == roomId, cancellationToken)) return;
        }
        // Done
        await stream.WriteAsync(position => EndAtAsync(userId, roomId, joined, position, cancellationToken), cancellationToken);
    }

    /// <summary>Mark or remove an invite at stream <paramref name="position"/>.</summary>
    private async Task<bool> EndAtAsync(string userId, string roomId, bool joined, long position, CancellationToken cancellationToken)
    {
        // Mark or remove
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RemoteInviteRow? row = await db.RemoteInvites.SingleOrDefaultAsync(i => i.UserId == userId && i.RoomId == roomId, cancellationToken);
        if (row is null) return false;
        if (joined) db.RemoteInvites.Remove(row);
        else (row.Membership, row.Stream) = (Memberships.Leave, position);
        await db.SaveChangesAsync(cancellationToken);
        // Done
        return true;
    }

    /// <summary>Return an <c>M_INVALID_PARAM</c> error about a received invite.</summary>
    private static MatrixException Invalid(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, message);

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
