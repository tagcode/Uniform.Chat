namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Federation;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#profiles">Profiles</see>: display name and avatar of local users, and of other servers' users through federation.</summary>
/// <remarks>Changing a profile also updates the user's member event in each joined room, as other members see names from there.</remarks>
public sealed class ProfileService
{
    /// <summary>Profile field of the display name.</summary>
    public const string DisplayName = "displayname";
    /// <summary>Profile field of the avatar.</summary>
    public const string AvatarUrl = "avatar_url";
    private const int MaxDisplayNameLength = 256;
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly RoomService rooms;
    private readonly FederationClient federation;
    private readonly IOptionsMonitor<ServerOptions> server;

    /// <summary>Create the service.</summary>
    public ProfileService(IDbContextFactory<ServerDbContext> contexts, RoomService rooms, FederationClient federation, IOptionsMonitor<ServerOptions> server)
    {
        // Store dependencies
        this.contexts = contexts;
        this.rooms = rooms;
        this.federation = federation;
        this.server = server;
    }

    /// <summary>Return the profile of a user, with unset fields left out; throw <c>M_NOT_FOUND</c> for unknown users. Users of other servers are asked from their server.</summary>
    public async Task<JsonObject> GetProfileAsync(string userId, CancellationToken cancellationToken)
    {
        // Other servers' users from their server
        if (UserId.TryParse(userId, out UserId parsed) && parsed.ServerName != server.CurrentValue.ServerName) return await GetRemoteProfileAsync(userId, parsed.ServerName, cancellationToken);

        // Existing user
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(u => u.UserId == userId && !u.Deactivated, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown user.");
        ProfileRow? row = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // Set fields only
        JsonObject profile = [];
        if (row?.DisplayName is string name) profile[DisplayName] = name;
        if (row?.AvatarUrl is string avatar) profile[AvatarUrl] = avatar;
        // Return profile
        return profile;
    }

    /// <summary>Return the profile of a user of <paramref name="serverName"/> through federation, with only the known fields.</summary>
    private async Task<JsonObject> GetRemoteProfileAsync(string userId, string serverName, CancellationToken cancellationToken)
    {
        // Query, keeping only string fields the client API has
        JsonNode? answer = await federation.SendAsync(HttpMethod.Get, serverName, $"/_matrix/federation/v1/query/profile?user_id={Uri.EscapeDataString(userId)}", null, cancellationToken);
        JsonObject profile = [];
        foreach (string field in new[] { DisplayName, AvatarUrl })
        {
            if (answer?[field] is JsonValue value && value.TryGetValue(out string? text)) profile[field] = text;
        }
        // Return profile
        return profile;
    }

    /// <summary>Return one field of a profile; throw <c>M_NOT_FOUND</c> when it is not set.</summary>
    public async Task<JsonObject> GetFieldAsync(string userId, string field, CancellationToken cancellationToken)
    {
        // Field of the profile
        JsonObject profile = await GetProfileAsync(userId, cancellationToken);
        if (profile[field] is not JsonNode value) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"No {field} set.");
        // Return field
        return new JsonObject { [field] = value.DeepClone() };
    }

    /// <summary>Set or clear (<paramref name="value"/> null or empty) the requester's display name or avatar, then update their rooms.</summary>
    public async Task SetFieldAsync(string userId, string field, string? value, CancellationToken cancellationToken)
    {
        // Assert known field and valid value
        if (field is not (DisplayName or AvatarUrl)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Unknown profile field {field}.");
        if (string.IsNullOrEmpty(value)) value = null;
        if (field == DisplayName && value?.Length > MaxDisplayNameLength) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"Display names are at most {MaxDisplayNameLength} characters.");
        if (field == AvatarUrl && value is not null && !value.StartsWith("mxc://", StringComparison.Ordinal)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "avatar_url must be an mxc:// URI.");

        // Store
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            ProfileRow? row = await db.Profiles.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (row is null) db.Profiles.Add(row = new ProfileRow { UserId = userId });
            if (field == DisplayName) row.DisplayName = value;
            else row.AvatarUrl = value;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Member events follow
        await rooms.UpdateMemberProfilesAsync(userId, cancellationToken);
    }
}
