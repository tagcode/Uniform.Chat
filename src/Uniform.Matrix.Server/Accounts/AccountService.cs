namespace Uniform.Matrix.Server.Accounts;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Encryption;
using Uniform.Matrix.Server.Storage;

/// <summary>Local accounts, devices and access tokens.</summary>
public sealed class AccountService
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly IOptions<ServerOptions> options;
    private readonly TimeProvider time;
    private readonly DeviceKeyService keys;
    private readonly PasswordHasher<string> hasher = new();
    private readonly ConcurrentDictionary<(string, string), DateTimeOffset> lastSeenWritten = new();

    /// <summary>Create the service over the database.</summary>
    public AccountService(IDbContextFactory<ServerDbContext> contexts, IOptions<ServerOptions> options, TimeProvider time, DeviceKeyService keys)
    {
        // Store dependencies
        this.contexts = contexts;
        this.options = options;
        this.time = time;
        this.keys = keys;
    }

    /// <summary>Server name of local user IDs.</summary>
    public string ServerName => options.Value.ServerName;

    /// <summary>Return whether <paramref name="localpart"/> is free; throw <c>M_INVALID_USERNAME</c> when it is not valid.</summary>
    public async Task<bool> IsAvailableAsync(string localpart, CancellationToken cancellationToken)
    {
        // Valid, then unused
        string userId = LocalUserIds.Normalize(localpart, ServerName);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        // Return availability
        return !await db.Users.AnyAsync(u => u.UserId == userId, cancellationToken);
    }

    /// <summary>Create an account, and unless <paramref name="inhibitLogin"/> a device with an access token; throw <c>M_USER_IN_USE</c> when taken.</summary>
    /// <param name="localpart">Wanted localpart, or null for a generated one.</param>
    /// <param name="password">Password, or null for an account that cannot log in with one.</param>
    /// <param name="deviceId">Device to create, or null for a generated one.</param>
    /// <param name="deviceName">Display name of the device.</param>
    /// <param name="inhibitLogin">Whether to create only the account.</param>
    /// <param name="admin">Whether the user administers the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<RegisterResponse> RegisterAsync(string? localpart, string? password, string? deviceId, string? deviceName, bool inhibitLogin, bool admin, CancellationToken cancellationToken)
    {
        // Account row
        string userId = LocalUserIds.Normalize(localpart ?? GenerateLocalpart(), ServerName);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Users.AnyAsync(u => u.UserId == userId, cancellationToken)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UserInUse, "The user ID is already taken.");
        db.Users.Add(new UserRow { UserId = userId, PasswordHash = password is null ? null : hasher.HashPassword(userId, password), CreatedTs = Now(), Admin = admin });
        db.Profiles.Add(new ProfileRow { UserId = userId, DisplayName = userId[1..userId.IndexOf(':')] });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UserInUse, "The user ID is already taken.");
        }

        // Device and token unless inhibited
        if (inhibitLogin) return new RegisterResponse { UserId = userId };
        (string device, string token) = await CreateSessionAsync(userId, deviceId, deviceName, cancellationToken);
        // Return session
        return new RegisterResponse { UserId = userId, DeviceId = device, AccessToken = token };
    }

    /// <summary>Log in with a password; throw <c>M_FORBIDDEN</c> for unknown users and wrong passwords alike.</summary>
    public async Task<LoginResponse> LoginAsync(string user, string password, string? deviceId, string? deviceName, CancellationToken cancellationToken)
    {
        // Same error for every failure, so user IDs cannot be probed
        string userId;
        try
        {
            userId = LocalUserIds.Normalize(user, ServerName);
        }
        catch (MatrixException)
        {
            throw WrongCredentials();
        }
        if (!await CheckPasswordAsync(userId, password, cancellationToken)) throw WrongCredentials();

        // New or reused device
        (string device, string token) = await CreateSessionAsync(userId, deviceId, deviceName, cancellationToken);
        // Return session
        return new LoginResponse { UserId = userId, DeviceId = device, AccessToken = token };
    }

    /// <summary>Return whether <paramref name="password"/> is the password of an active account.</summary>
    public async Task<bool> CheckPasswordAsync(string userId, string password, CancellationToken cancellationToken)
    {
        // Active account with a password
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        UserRow? user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken);
        if (user is null || user.Deactivated || user.PasswordHash is null) return false;
        // Return verification
        return hasher.VerifyHashedPassword(userId, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }

    /// <summary>Return the user and device of an access token, or null when unknown; record when the device was last seen.</summary>
    public async Task<Requester?> AuthenticateAsync(string token, string? address, CancellationToken cancellationToken)
    {
        // Look up by hash
        string hash = Hash(token);
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        AccessTokenRow? row = await db.AccessTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (row is null) return null;

        // Last seen, written at most once a minute per device
        DateTimeOffset now = time.GetUtcNow();
        if (!lastSeenWritten.TryGetValue((row.UserId, row.DeviceId), out DateTimeOffset written) || now - written > LastSeenInterval)
        {
            lastSeenWritten[(row.UserId, row.DeviceId)] = now;
            await db.Devices.Where(d => d.UserId == row.UserId && d.DeviceId == row.DeviceId).ExecuteUpdateAsync(d => d.SetProperty(x => x.LastSeenTs, now.ToUnixTimeMilliseconds()).SetProperty(x => x.LastSeenIp, address), cancellationToken);
        }
        // Return requester
        return new Requester(row.UserId, row.DeviceId);
    }

    /// <summary>End the session of <paramref name="requester"/>: its token and device.</summary>
    public Task LogoutAsync(Requester requester, CancellationToken cancellationToken) => DeleteDevicesAsync(requester.UserId, [requester.DeviceId], cancellationToken);

    /// <summary>End every session of <paramref name="userId"/>.</summary>
    public async Task LogoutAllAsync(string userId, CancellationToken cancellationToken)
    {
        // All devices
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<string> devices = await db.Devices.Where(d => d.UserId == userId).Select(d => d.DeviceId).ToListAsync(cancellationToken);
        await DeleteDevicesAsync(userId, devices, cancellationToken);
    }

    /// <summary>Return the devices of <paramref name="userId"/>.</summary>
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(string userId, CancellationToken cancellationToken)
    {
        // All rows of the user
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<DeviceRow> rows = await db.Devices.AsNoTracking().Where(d => d.UserId == userId).OrderBy(d => d.DeviceId).ToListAsync(cancellationToken);
        // Return devices
        return [.. rows.Select(ToDevice)];
    }

    /// <summary>Return one device; throw <c>M_NOT_FOUND</c> when the user has no such device.</summary>
    public async Task<Device> GetDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken)
    {
        // Find
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        DeviceRow row = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken) ?? throw NoDevice();
        // Return device
        return ToDevice(row);
    }

    /// <summary>Rename a device; throw <c>M_NOT_FOUND</c> when the user has no such device.</summary>
    public async Task RenameDeviceAsync(string userId, string deviceId, string? displayName, CancellationToken cancellationToken)
    {
        // Update in place
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        int updated = await db.Devices.Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteUpdateAsync(d => d.SetProperty(x => x.DisplayName, displayName), cancellationToken);
        if (updated == 0) throw NoDevice();
    }

    /// <summary>Delete devices of <paramref name="userId"/> with their tokens, pushers, keys and waiting messages; unknown device IDs are ignored.</summary>
    public async Task DeleteDevicesAsync(string userId, IReadOnlyCollection<string> deviceIds, CancellationToken cancellationToken)
    {
        // Tokens and pushers first, then devices, then their keys
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.AccessTokens.Where(t => t.UserId == userId && deviceIds.Contains(t.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await db.Pushers.Where(p => p.UserId == userId && p.DeviceId != null && deviceIds.Contains(p.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await db.Devices.Where(d => d.UserId == userId && deviceIds.Contains(d.DeviceId)).ExecuteDeleteAsync(cancellationToken);
        await keys.DeleteDevicesAsync(userId, deviceIds, cancellationToken);
    }

    /// <summary>Set a new password; with <paramref name="keepDeviceId"/>, logs out every other device.</summary>
    public async Task ChangePasswordAsync(string userId, string newPassword, string? keepDeviceId, CancellationToken cancellationToken)
    {
        // New hash
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string hash = hasher.HashPassword(userId, newPassword);
        await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, hash), cancellationToken);

        // Other sessions end
        if (keepDeviceId is null) return;
        List<string> others = await db.Devices.Where(d => d.UserId == userId && d.DeviceId != keepDeviceId).Select(d => d.DeviceId).ToListAsync(cancellationToken);
        await DeleteDevicesAsync(userId, others, cancellationToken);
    }

    /// <summary>Create or reuses a device and issues a new access token for it.</summary>
    private async Task<(string DeviceId, string Token)> CreateSessionAsync(string userId, string? deviceId, string? deviceName, CancellationToken cancellationToken)
    {
        // Reusing a device replaces its tokens
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        string device = deviceId ?? GenerateDeviceId();
        DeviceRow? existing = await db.Devices.SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == device, cancellationToken);
        if (existing is null) db.Devices.Add(new DeviceRow { UserId = userId, DeviceId = device, DisplayName = deviceName, LastSeenTs = Now() });
        else await db.AccessTokens.Where(t => t.UserId == userId && t.DeviceId == device).ExecuteDeleteAsync(cancellationToken);

        // Random token, stored hashed
        string token = "mxt_" + Base64.EncodeUrlSafe(RandomNumberGenerator.GetBytes(32));
        db.AccessTokens.Add(new AccessTokenRow { TokenHash = Hash(token), UserId = userId, DeviceId = device, CreatedTs = Now() });
        await db.SaveChangesAsync(cancellationToken);
        // Return device and token
        return (device, token);
    }

    /// <summary>Convert a device row.</summary>
    private static Device ToDevice(DeviceRow row) => new() { DeviceId = row.DeviceId, DisplayName = row.DisplayName, LastSeenTs = row.LastSeenTs, LastSeenIp = row.LastSeenIp };

    /// <summary>Hex SHA-256 of a token.</summary>
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Return ten random uppercase letters, like other servers' device IDs.</summary>
    private static string GenerateDeviceId() => string.Concat(RandomNumberGenerator.GetItems<char>("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 10));

    /// <summary>Return a random numeric localpart for registrations without a username.</summary>
    private static string GenerateLocalpart() => RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString();

    /// <summary>Milliseconds since the Unix epoch.</summary>
    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Error of a failed login.</summary>
    private static MatrixException WrongCredentials() => new(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Invalid username or password.");

    /// <summary>Error of an unknown device.</summary>
    private static MatrixException NoDevice() => new(HttpStatusCode.NotFound, ErrorCodes.NotFound, "No such device.");
}
