namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

// Whois, suspension, locks, account details, room shutdown and media, for the remote admin API

/// <summary>A device of a user, as <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3adminwhoisuserid">whois</see> shows it.</summary>
/// <param name="DeviceId">Device ID.</param>
/// <param name="DisplayName">Device name.</param>
/// <param name="LastSeen">Last request.</param>
/// <param name="LastSeenIp">Address of the last request.</param>
public sealed record AdminDevice(string DeviceId, string? DisplayName, DateTimeOffset? LastSeen, string? LastSeenIp);

/// <summary>What the remote admin API changes of an account; a null field stays as it is.</summary>
public sealed record AdminUserChange
{
    /// <summary>New password.</summary>
    public string? Password { get; init; }
    /// <summary>Whether a new password logs out every device; true by default.</summary>
    public bool LogoutDevices { get; init; } = true;
    /// <summary>New display name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>New avatar content URI.</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>Give or take admin rights.</summary>
    public bool? Admin { get; init; }
    /// <summary>Deactivate, or reactivate.</summary>
    public bool? Deactivated { get; init; }
    /// <summary>Lock, or unlock.</summary>
    public bool? Locked { get; init; }
}

/// <summary>What shutting down a room did.</summary>
/// <param name="KickedUsers">Local users who were made to leave.</param>
/// <param name="FailedToKickUsers">Local users who could not be made to leave.</param>
/// <param name="LocalAliases">Local aliases that were deleted.</param>
public sealed record RoomShutdown(IReadOnlyList<string> KickedUsers, IReadOnlyList<string> FailedToKickUsers, IReadOnlyList<string> LocalAliases);

public sealed partial class AdminService
{
    /// <summary>Return one local account; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    public async Task<AdminUser> GetUserAsync(string userId, CancellationToken cancellationToken)
    {
        // From the list, which is small enough on this server's scale
        await AssertUserAsync(userId, cancellationToken);
        // Return account
        return (await GetUsersAsync(cancellationToken)).Single(u => u.UserId == userId);
    }

    /// <summary>Return the devices of <paramref name="userId"/>, with when and from where each was last used; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    public async Task<IReadOnlyList<AdminDevice>> WhoisAsync(string userId, CancellationToken cancellationToken)
    {
        // Devices by last use
        await AssertUserAsync(userId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<DeviceRow> devices = await db.Devices.AsNoTracking().Where(d => d.UserId == userId).ToListAsync(cancellationToken);
        // Return devices
        return [.. devices.OrderByDescending(d => d.LastSeenTs).Select(d => new AdminDevice(d.DeviceId, d.DisplayName, d.LastSeenTs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, d.LastSeenIp))];
    }

    /// <summary>Return whether the account is suspended; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    public Task<bool> IsSuspendedAsync(string userId, CancellationToken cancellationToken) => GetFlagAsync(userId, u => u.Suspended, cancellationToken);

    /// <summary>Return whether the account is locked; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    public Task<bool> IsLockedAsync(string userId, CancellationToken cancellationToken) => GetFlagAsync(userId, u => u.Locked, cancellationToken);

    /// <summary>Suspend an account, so it can read and leave but not send, join, invite or upload; or end the suspension.</summary>
    public async Task SetSuspendedAsync(string userId, bool suspended, CancellationToken cancellationToken)
    {
        // Flag of the account
        await AssertUserAsync(userId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Suspended, suspended), cancellationToken);
        // Done
        logger.LogInformation(suspended ? "Admin suspended {UserId}" : "Admin ended the suspension of {UserId}", userId);
    }

    /// <summary>Lock an account, so its sessions are refused with <c>M_USER_LOCKED</c> but kept for when it is unlocked; or unlock it.</summary>
    public async Task SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken)
    {
        // Flag of the account
        await AssertUserAsync(userId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Locked, locked), cancellationToken);
        // Done
        logger.LogInformation(locked ? "Admin locked {UserId}" : "Admin unlocked {UserId}", userId);
    }

    /// <summary>Deactivate an account the way its owner would: leave its rooms, clear its profile and end its sessions.</summary>
    public async Task DeactivateAsync(string userId, CancellationToken cancellationToken)
    {
        // Full deactivation
        await AssertUserAsync(userId, cancellationToken);
        await deactivation.DeactivateAsync(userId, cancellationToken);
        // Done
        logger.LogInformation("Admin deactivated {UserId}", userId);
    }

    /// <summary>Create the account when it does not exist, with <see cref="AdminUserChange.Password"/>, then apply the rest of <paramref name="change"/>; return whether it was created.</summary>
    public async Task<bool> CreateOrUpdateUserAsync(string userId, AdminUserChange change, CancellationToken cancellationToken)
    {
        // Assert a local user ID
        string localpart = LocalUserIds.Normalize(userId, accounts.ServerName)[1..].Split(':')[0];
        bool exists;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            exists = await db.Users.AnyAsync(u => u.UserId == userId, cancellationToken);
        }

        // New account, or a new password
        if (!exists) await CreateUserAsync(localpart, change.Password ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "A new account needs a password."), change.Admin == true, cancellationToken);
        else if (change.Password is string password)
        {
            await accounts.ChangePasswordAsync(userId, password, keepDeviceId: null, cancellationToken);
            if (change.LogoutDevices) await accounts.LogoutAllAsync(userId, cancellationToken);
            logger.LogInformation("Admin set a new password for {UserId}", userId);
        }

        // Profile, rights and flags
        if (change.DisplayName is not null || change.AvatarUrl is not null)
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            ProfileRow? profile = await db.Profiles.SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (profile is null) db.Profiles.Add(profile = new ProfileRow { UserId = userId });
            if (change.DisplayName is not null) profile.DisplayName = change.DisplayName;
            if (change.AvatarUrl is not null) profile.AvatarUrl = change.AvatarUrl;
            await db.SaveChangesAsync(cancellationToken);
            await rooms.UpdateMemberProfilesAsync(userId, cancellationToken);
        }
        if (exists && change.Admin is bool admin) await SetAdminAsync(userId, admin, cancellationToken);
        if (change.Locked is bool locked) await SetLockedAsync(userId, locked, cancellationToken);
        if (change.Deactivated == true) await DeactivateAsync(userId, cancellationToken);
        else if (change.Deactivated == false) await SetDeactivatedAsync(userId, false, cancellationToken);
        // Return whether created
        return !exists;
    }

    /// <summary>Return the local users joined to a room; throw <c>M_NOT_FOUND</c> for unknown rooms.</summary>
    public async Task<IReadOnlyList<string>> GetRoomMembersAsync(string roomId, CancellationToken cancellationToken)
    {
        // Joined members of the current state
        await AssertRoomAsync(roomId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return members
        return await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).Select(s => s.StateKey).OrderBy(u => u).ToListAsync(cancellationToken);
    }

    /// <summary>Shut a room down on this server: make every local member leave and forget it, take it out of the directory, delete its local aliases, and, with <paramref name="block"/>, keep local users from joining it again.</summary>
    /// <remarks>The room's events stay in the database; other servers' users stay in the room.</remarks>
    public async Task<RoomShutdown> ShutdownRoomAsync(string roomId, bool block, string? message, CancellationToken cancellationToken)
    {
        // Local members, joined or invited
        await AssertRoomAsync(roomId, cancellationToken);
        string suffix = $":{accounts.ServerName}";
        List<string> members;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            members = await db.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && (s.Membership == Memberships.Join || s.Membership == Memberships.Invite)).Select(s => s.StateKey).ToListAsync(cancellationToken);
        }
        members = [.. members.Where(m => m.EndsWith(suffix, StringComparison.Ordinal))];

        // Each leaves and forgets it; one that fails does not stop the rest
        List<string> kicked = [];
        List<string> failed = [];
        foreach (string member in members)
        {
            try
            {
                await rooms.LeaveAsync(member, roomId, message ?? "The room was shut down by the server admin", cancellationToken);
                await rooms.ForgetAsync(member, roomId, cancellationToken);
                kicked.Add(member);
            }
            catch (MatrixException ex)
            {
                logger.LogWarning("Shutting down {RoomId} could not remove {UserId}: {Error}", roomId, member, ex.Message);
                failed.Add(member);
            }
        }

        // Directory, aliases and the block
        List<string> aliases;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            aliases = await db.RoomAliases.Where(a => a.RoomId == roomId).Select(a => a.Alias).ToListAsync(cancellationToken);
            await db.RoomAliases.Where(a => a.RoomId == roomId).ExecuteDeleteAsync(cancellationToken);
            await db.Rooms.Where(r => r.RoomId == roomId).ExecuteUpdateAsync(r => r.SetProperty(x => x.Published, false).SetProperty(x => x.Blocked, x => block || x.Blocked), cancellationToken);
        }
        logger.LogInformation("Admin shut down room {RoomId}{Blocked}: {Count} local members removed", roomId, block ? " and blocked it" : "", kicked.Count);
        // Return what was done
        return new RoomShutdown(kicked, failed, aliases);
    }

    /// <summary>Return whether local users are kept from joining a room; throw <c>M_NOT_FOUND</c> for unknown rooms.</summary>
    public async Task<bool> IsRoomBlockedAsync(string roomId, CancellationToken cancellationToken)
    {
        // Flag of the room
        await AssertRoomAsync(roomId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return flag
        return await db.Rooms.Where(r => r.RoomId == roomId).Select(r => r.Blocked).SingleAsync(cancellationToken);
    }

    /// <summary>Block or unblock a room, so local users cannot or can join it.</summary>
    public async Task SetRoomBlockedAsync(string roomId, bool blocked, CancellationToken cancellationToken)
    {
        // Flag of the room
        await AssertRoomAsync(roomId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Rooms.Where(r => r.RoomId == roomId).ExecuteUpdateAsync(r => r.SetProperty(x => x.Blocked, blocked), cancellationToken);
        // Done
        logger.LogInformation(blocked ? "Admin blocked room {RoomId}" : "Admin unblocked room {RoomId}", roomId);
    }

    /// <summary>Return the media uploaded by <paramref name="userId"/>, newest first.</summary>
    public async Task<IReadOnlyList<MediaRow>> GetUserMediaAsync(string userId, CancellationToken cancellationToken)
    {
        // Uploads of an existing account
        await AssertUserAsync(userId, cancellationToken);
        // Return uploads
        return await media.GetUploadsAsync(userId, cancellationToken);
    }

    /// <summary>Delete local media; throw <c>M_NOT_FOUND</c> for media of other servers and unknown media.</summary>
    public async Task DeleteMediaAsync(string serverName, string mediaId, CancellationToken cancellationToken)
    {
        // Local media only
        if (serverName != accounts.ServerName || !await media.DeleteLocalAsync(mediaId, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown local media.");
    }

    /// <summary>Return a flag of an account; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    private async Task<bool> GetFlagAsync(string userId, System.Linq.Expressions.Expression<Func<UserRow, bool>> flag, CancellationToken cancellationToken)
    {
        // One column of the account
        await AssertUserAsync(userId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return flag
        return await db.Users.Where(u => u.UserId == userId).Select(flag).SingleAsync(cancellationToken);
    }

    /// <summary>Throw <c>M_NOT_FOUND</c> unless the room is known here.</summary>
    private async Task AssertRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        // Existing room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Rooms.AnyAsync(r => r.RoomId == roomId, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
    }
}
