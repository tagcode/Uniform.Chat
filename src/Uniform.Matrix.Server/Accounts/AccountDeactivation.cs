namespace Uniform.Matrix.Server.Accounts;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3accountdeactivate">Deactivates</see> an account at its owner's request: leaves its rooms, clears its profile and ends its sessions; the user ID is never given out again.</summary>
public sealed class AccountDeactivation
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly AccountService accounts;
    private readonly RoomService rooms;
    private readonly ILogger<AccountDeactivation> logger;

    /// <summary>Create the service.</summary>
    public AccountDeactivation(IDbContextFactory<ServerDbContext> contexts, AccountService accounts, RoomService rooms, ILogger<AccountDeactivation> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.accounts = accounts;
        this.rooms = rooms;
        this.logger = logger;
    }

    /// <summary>Deactivate <paramref name="userId"/>: leave joined rooms, reject invites, clear the display name and avatar, delete pushers, then log out every device.</summary>
    public async Task DeactivateAsync(string userId, CancellationToken cancellationToken)
    {
        // Rooms joined, invited to or knocked on, here and on other servers
        List<string> roomIds;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            roomIds = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && (s.Membership == Memberships.Join || s.Membership == Memberships.Invite || s.Membership == "knock")).Select(s => s.RoomId).ToListAsync(cancellationToken);
            roomIds.AddRange(await db.RemoteInvites.AsNoTracking().Where(i => i.UserId == userId && i.Membership == Memberships.Invite).Select(i => i.RoomId).ToListAsync(cancellationToken));
        }

        // Leave each; one that fails does not stop the rest
        foreach (string roomId in roomIds.Distinct())
        {
            try
            {
                await rooms.LeaveAsync(userId, roomId, "Account deactivated", cancellationToken);
            }
            catch (MatrixException ex)
            {
                logger.LogWarning("Deactivating {UserId} could not leave {RoomId}: {Error}", userId, roomId, ex.Message);
            }
        }

        // Profile, pushers and the flag, then the sessions
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await db.Profiles.Where(p => p.UserId == userId).ExecuteUpdateAsync(p => p.SetProperty(x => x.DisplayName, (string?)null).SetProperty(x => x.AvatarUrl, (string?)null), cancellationToken);
            await db.Pushers.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Deactivated, true), cancellationToken);
        }
        await accounts.LogoutAllAsync(userId, cancellationToken);
        // Done
        logger.LogInformation("{UserId} deactivated their account and left {Count} rooms", userId, roomIds.Count);
    }
}
