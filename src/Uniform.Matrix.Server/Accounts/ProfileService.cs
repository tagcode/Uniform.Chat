namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#profiles">Profiles</see> of local users: display name and avatar.</summary>
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

    /// <summary>Create the service.</summary>
    public ProfileService(IDbContextFactory<ServerDbContext> contexts, RoomService rooms)
    {
        // Store dependencies
        this.contexts = contexts;
        this.rooms = rooms;
    }

    /// <summary>Return the profile of a local user, with unset fields left out; throw <c>M_NOT_FOUND</c> for unknown users.</summary>
    public async Task<JsonObject> GetProfileAsync(string userId, CancellationToken cancellationToken)
    {
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
