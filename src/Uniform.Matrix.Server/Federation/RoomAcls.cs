namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Storage;

/// <summary>The <see href="https://spec.matrix.org/latest/server-server-api/#server-access-control-lists-acls">server ACLs</see> of rooms here: requests and transactions of a server the room's <c>m.room.server_acl</c> denies are refused for that room.</summary>
public sealed class RoomAcls
{
    /// <summary>Event type of server ACLs.</summary>
    public const string EventType = "m.room.server_acl";
    private readonly IDbContextFactory<ServerDbContext> contexts;

    /// <summary>Create the service.</summary>
    public RoomAcls(IDbContextFactory<ServerDbContext> contexts)
    {
        // Store dependencies
        this.contexts = contexts;
    }

    /// <summary>Return whether <paramref name="serverName"/> may take part in <paramref name="roomId"/>; true for rooms without an ACL and rooms not here.</summary>
    public async Task<bool> IsAllowedAsync(string serverName, string? roomId, CancellationToken cancellationToken)
    {
        // The ACL in the current state, if any
        if (roomId is null) return true;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string? eventId = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventType && s.StateKey == "").Select(s => s.EventId).SingleOrDefaultAsync(cancellationToken);
        if (eventId is null) return true;
        string? json = await db.Events.AsNoTracking().Where(e => e.EventId == eventId).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        // Return the ACL's verdict; content that is not an object denies nothing
        return json is null || JsonNode.Parse(json)?["content"] is not JsonObject content || new ServerAcl(content).IsAllowed(serverName);
    }

    /// <summary>Throw <c>M_FORBIDDEN</c> when the ACL of <paramref name="roomId"/> denies <paramref name="serverName"/>.</summary>
    public async Task AssertAllowedAsync(string serverName, string? roomId, CancellationToken cancellationToken)
    {
        // Done when allowed
        if (await IsAllowedAsync(serverName, roomId, cancellationToken)) return;
        // On error refuse
        throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, $"The room's server ACL denies {serverName}.");
    }
}
