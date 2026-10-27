namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Storage;

/// <summary>A local account as the admin screens list it.</summary>
public sealed record AdminUser
{
    /// <summary>Full user ID.</summary>
    public required string UserId { get; init; }
    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Whether the user administers the server.</summary>
    public bool Admin { get; init; }
    /// <summary>Whether the account is deactivated.</summary>
    public bool Deactivated { get; init; }
    /// <summary>Logged-in devices.</summary>
    public int Devices { get; init; }
    /// <summary>Joined rooms.</summary>
    public int Rooms { get; init; }
    /// <summary>Creation time.</summary>
    public DateTimeOffset Created { get; init; }
}

/// <summary>A room as the admin screens list it.</summary>
public sealed record AdminRoom
{
    /// <summary>Room ID.</summary>
    public required string RoomId { get; init; }
    /// <summary>Name, when set.</summary>
    public string? Name { get; init; }
    /// <summary>Canonical alias, when set.</summary>
    public string? Alias { get; init; }
    /// <summary>Joined members.</summary>
    public int Members { get; init; }
    /// <summary>Whether the room is listed in the public room directory.</summary>
    public bool Published { get; init; }
    /// <summary>Whether messages are end-to-end encrypted.</summary>
    public bool Encrypted { get; init; }
    /// <summary>Room version.</summary>
    public required string Version { get; init; }
    /// <summary>Creator.</summary>
    public required string Creator { get; init; }
}

/// <summary>Server administration for the operator: accounts, passwords, admin rights and the room directory, without an access token.</summary>
/// <remarks>Each change is logged at Information level, so the session log shows what the operator did; passwords are never logged.</remarks>
public sealed class AdminService
{
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly AccountService accounts;
    private readonly ILogger<AdminService> logger;

    /// <summary>Create the service.</summary>
    public AdminService(IDbContextFactory<ServerDbContext> contexts, AccountService accounts, ILogger<AdminService> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.accounts = accounts;
        this.logger = logger;
    }

    /// <summary>Return every local account, by user ID.</summary>
    public async Task<IReadOnlyList<AdminUser>> GetUsersAsync(CancellationToken cancellationToken)
    {
        // Users with profile, device and room counts
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<UserRow> users = await db.Users.AsNoTracking().OrderBy(u => u.UserId).ToListAsync(cancellationToken);
        Dictionary<string, string?> names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.DisplayName, cancellationToken);
        Dictionary<string, int> devices = await db.Devices.AsNoTracking().GroupBy(d => d.UserId).ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
        Dictionary<string, int> rooms = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).GroupBy(s => s.StateKey).ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
        // Return summaries
        return [.. users.Select(u => new AdminUser
        {
            UserId = u.UserId,
            DisplayName = names.GetValueOrDefault(u.UserId),
            Admin = u.Admin,
            Deactivated = u.Deactivated,
            Devices = devices.GetValueOrDefault(u.UserId),
            Rooms = rooms.GetValueOrDefault(u.UserId),
            Created = DateTimeOffset.FromUnixTimeMilliseconds(u.CreatedTs),
        })];
    }

    /// <summary>Create an account that logs in with <paramref name="password"/>; return its user ID.</summary>
    public async Task<string> CreateUserAsync(string localpart, string password, bool admin, CancellationToken cancellationToken)
    {
        // Assert a password
        if (string.IsNullOrEmpty(password)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "A password is required.");
        string userId = (await accounts.RegisterAsync(localpart, password, deviceId: null, deviceName: null, inhibitLogin: true, admin, cancellationToken)).UserId;
        logger.LogInformation("Admin created user {UserId}{Admin}", userId, admin ? " with server admin rights" : "");
        // Return user ID
        return userId;
    }

    /// <summary>Set a new password and log out every device of the user.</summary>
    public async Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken)
    {
        // Assert account and password
        if (string.IsNullOrEmpty(password)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "A password is required.");
        await AssertUserAsync(userId, cancellationToken);
        await accounts.ChangePasswordAsync(userId, password, keepDeviceId: null, cancellationToken);
        await accounts.LogoutAllAsync(userId, cancellationToken);
        logger.LogInformation("Admin reset the password of {UserId} and logged out its devices", userId);
    }

    /// <summary>Give or take server admin rights.</summary>
    public async Task SetAdminAsync(string userId, bool admin, CancellationToken cancellationToken)
    {
        // Flag of the account
        await AssertUserAsync(userId, cancellationToken);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Admin, admin), cancellationToken);
        logger.LogInformation(admin ? "Admin gave server admin rights to {UserId}" : "Admin took server admin rights from {UserId}", userId);
    }

    /// <summary>Deactivate an account, logging out every device, or reactivates it.</summary>
    public async Task SetDeactivatedAsync(string userId, bool deactivated, CancellationToken cancellationToken)
    {
        // Flag of the account; sessions end on deactivation
        await AssertUserAsync(userId, cancellationToken);
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Deactivated, deactivated), cancellationToken);
        }
        if (deactivated) await accounts.LogoutAllAsync(userId, cancellationToken);
        logger.LogInformation(deactivated ? "Admin deactivated {UserId} and logged out its devices" : "Admin reactivated {UserId}", userId);
    }

    /// <summary>Return every room, most members first.</summary>
    public async Task<IReadOnlyList<AdminRoom>> GetRoomsAsync(CancellationToken cancellationToken)
    {
        // Rooms with member counts and a few state contents
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<RoomRow> rooms = await db.Rooms.AsNoTracking().ToListAsync(cancellationToken);
        Dictionary<string, int> members = await db.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join).GroupBy(s => s.RoomId).ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
        string[] types = [EventTypes.RoomName, EventTypes.RoomCanonicalAlias, EventTypes.RoomEncryption];
        List<CurrentStateRow> state = await db.CurrentState.AsNoTracking().Where(s => s.StateKey == "" && types.Contains(s.Type)).ToListAsync(cancellationToken);
        List<string> ids = [.. state.Select(s => s.EventId)];
        Dictionary<string, JsonObject?> contents = (await db.Events.AsNoTracking().Where(e => ids.Contains(e.EventId)).Select(e => new { e.EventId, e.Json }).ToListAsync(cancellationToken)).ToDictionary(e => e.EventId, e => JsonNode.Parse(e.Json)!["content"] as JsonObject);
        string? Field(string roomId, string type, string key) => state.FirstOrDefault(s => s.RoomId == roomId && s.Type == type) is CurrentStateRow row && contents.GetValueOrDefault(row.EventId)?[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        // Return summaries
        return [.. rooms.Select(r => new AdminRoom
        {
            RoomId = r.RoomId,
            Name = Field(r.RoomId, EventTypes.RoomName, "name"),
            Alias = Field(r.RoomId, EventTypes.RoomCanonicalAlias, "alias"),
            Members = members.GetValueOrDefault(r.RoomId),
            Published = r.Published,
            Encrypted = state.Any(s => s.RoomId == r.RoomId && s.Type == EventTypes.RoomEncryption),
            Version = r.Version,
            Creator = r.Creator,
        }).OrderByDescending(r => r.Members).ThenBy(r => r.RoomId, StringComparer.Ordinal)];
    }

    /// <summary>List or unlists a room in the public room directory.</summary>
    public async Task SetPublishedAsync(string roomId, bool published, CancellationToken cancellationToken)
    {
        // Flag of the room
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        int updated = await db.Rooms.Where(r => r.RoomId == roomId).ExecuteUpdateAsync(r => r.SetProperty(x => x.Published, published), cancellationToken);
        if (updated == 0) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room.");
        logger.LogInformation(published ? "Admin listed room {RoomId} in the public room directory" : "Admin removed room {RoomId} from the public room directory", roomId);
    }

    /// <summary>Throw <c>M_NOT_FOUND</c> unless the account exists.</summary>
    private async Task AssertUserAsync(string userId, CancellationToken cancellationToken)
    {
        // Existing account
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(u => u.UserId == userId, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"Unknown user {userId}.");
    }
}
