namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;

/// <summary>A registration token as the admin API shows it.</summary>
public sealed record RegistrationToken
{
    /// <summary>The token.</summary>
    public required string Token { get; init; }
    /// <summary>How many registrations may use it; null for any number.</summary>
    public int? UsesAllowed { get; init; }
    /// <summary>Registrations that used it.</summary>
    public int Completed { get; init; }
    /// <summary>Expiry in milliseconds since the Unix epoch; null for never.</summary>
    public long? ExpiryTime { get; init; }
}

/// <summary>Registration tokens: those in <see cref="ServerOptions.RegistrationTokens"/>, which never run out, and those admins create in the database, with a number of uses and an expiry.</summary>
/// <remarks>A token's use is counted when a registration passes the token stage, so a registration abandoned after it still uses it up.</remarks>
public sealed class RegistrationTokens
{
    /// <summary>Characters of generated tokens, as Synapse uses.</summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~-";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly IOptionsMonitor<ServerOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<RegistrationTokens> logger;

    /// <summary>Create the service.</summary>
    public RegistrationTokens(IDbContextFactory<ServerDbContext> contexts, IOptionsMonitor<ServerOptions> options, TimeProvider time, ILogger<RegistrationTokens> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>Return whether <paramref name="token"/> may register now: a configured one, or a stored one with uses left that has not expired.</summary>
    public async Task<bool> IsValidAsync(string? token, CancellationToken cancellationToken)
    {
        // Configured tokens, compared in constant time
        if (string.IsNullOrEmpty(token)) return false;
        byte[] given = Encoding.UTF8.GetBytes(token);
        if (options.CurrentValue.GetRegistrationTokens().Any(t => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t), given))) return true;

        // Stored tokens
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RegistrationTokenRow? row = await db.RegistrationTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Token == token, cancellationToken);
        // Return validity
        return row is not null && IsUsable(row);
    }

    /// <summary>Use <paramref name="token"/> for a registration; return false when it is not valid. Stored tokens count the use.</summary>
    public async Task<bool> UseAsync(string? token, CancellationToken cancellationToken)
    {
        // Configured tokens have no count
        if (!await IsValidAsync(token, cancellationToken)) return false;
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        int counted = await db.RegistrationTokens.Where(t => t.Token == token && (t.UsesAllowed == null || t.Completed < t.UsesAllowed) && (t.ExpiryTs == null || t.ExpiryTs > now)).ExecuteUpdateAsync(t => t.SetProperty(x => x.Completed, x => x.Completed + 1), cancellationToken);
        bool stored = await db.RegistrationTokens.AnyAsync(t => t.Token == token, cancellationToken);
        // Return whether used: configured, or stored and counted
        return !stored || counted > 0;
    }

    /// <summary>Return the stored tokens; with <paramref name="valid"/>, only those that may or may not register now.</summary>
    public async Task<IReadOnlyList<RegistrationToken>> GetAllAsync(bool? valid, CancellationToken cancellationToken)
    {
        // All, then filtered in memory, as validity depends on the time
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<RegistrationTokenRow> rows = await db.RegistrationTokens.AsNoTracking().OrderBy(t => t.Token).ToListAsync(cancellationToken);
        // Return tokens
        return [.. rows.Where(r => valid is null || IsUsable(r) == valid).Select(ToToken)];
    }

    /// <summary>Return a stored token; throw <c>M_NOT_FOUND</c> when unknown.</summary>
    public async Task<RegistrationToken> GetAsync(string token, CancellationToken cancellationToken)
    {
        // By primary key
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RegistrationTokenRow row = await db.RegistrationTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Token == token, cancellationToken) ?? throw NotFound();
        // Return token
        return ToToken(row);
    }

    /// <summary>Create a token: <paramref name="token"/>, or a random one of <paramref name="length"/> characters.</summary>
    public async Task<RegistrationToken> CreateAsync(string? token, int length, int? usesAllowed, long? expiryTime, CancellationToken cancellationToken)
    {
        // Assert arguments
        if (token is not null && (token.Length is 0 or > 64 || token.Any(c => !Alphabet.Contains(c)))) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "A token has 1 to 64 characters of A-Z, a-z, 0-9, '.', '_', '~' and '-'.");
        if (length is < 1 or > 64) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "length must be from 1 to 64.");
        if (usesAllowed < 0) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "uses_allowed cannot be negative.");
        if (expiryTime is long expiry && expiry < time.GetUtcNow().ToUnixTimeMilliseconds()) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "expiry_time is in the past.");

        // Stored, unless taken
        RegistrationTokenRow row = new() { Token = token ?? RandomNumberGenerator.GetString(Alphabet[..62], length), UsesAllowed = usesAllowed, ExpiryTs = expiryTime };
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.RegistrationTokens.AnyAsync(t => t.Token == row.Token, cancellationToken)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "The token already exists.");
        db.RegistrationTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin created a registration token with {Uses} uses, expiring {Expiry}", usesAllowed?.ToString() ?? "unlimited", expiryTime is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : "never");
        // Return token
        return ToToken(row);
    }

    /// <summary>Change the uses or expiry of a token; a field not given stays as it is.</summary>
    public async Task<RegistrationToken> UpdateAsync(string token, Optional<int?> usesAllowed, Optional<long?> expiryTime, CancellationToken cancellationToken)
    {
        // Existing token
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        RegistrationTokenRow row = await db.RegistrationTokens.SingleOrDefaultAsync(t => t.Token == token, cancellationToken) ?? throw NotFound();
        if (usesAllowed.IsSet) row.UsesAllowed = usesAllowed.Value < 0 ? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "uses_allowed cannot be negative.") : usesAllowed.Value;
        if (expiryTime.IsSet) row.ExpiryTs = expiryTime.Value;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin changed a registration token");
        // Return token
        return ToToken(row);
    }

    /// <summary>Delete a stored token.</summary>
    public async Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        // By primary key
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.RegistrationTokens.Where(t => t.Token == token).ExecuteDeleteAsync(cancellationToken) == 0) throw NotFound();
        // Done
        logger.LogInformation("Admin deleted a registration token");
    }

    /// <summary>Return whether a stored token has uses left and has not expired.</summary>
    private bool IsUsable(RegistrationTokenRow row) => (row.UsesAllowed is null || row.Completed < row.UsesAllowed) && (row.ExpiryTs is null || row.ExpiryTs > time.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>Return the API form of a row.</summary>
    private static RegistrationToken ToToken(RegistrationTokenRow row) => new() { Token = row.Token, UsesAllowed = row.UsesAllowed, Completed = row.Completed, ExpiryTime = row.ExpiryTs };

    /// <summary>Return the error of an unknown token.</summary>
    private static MatrixException NotFound() => new(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown registration token.");
}

/// <summary>A value that may be left out, as opposed to given as null.</summary>
/// <typeparam name="T">Type of the value.</typeparam>
/// <param name="IsSet">Whether the value was given.</param>
/// <param name="Value">The value, when given.</param>
public readonly record struct Optional<T>(bool IsSet, T Value)
{
    /// <summary>A given value.</summary>
    public static Optional<T> Of(T value) => new(true, value);
}
