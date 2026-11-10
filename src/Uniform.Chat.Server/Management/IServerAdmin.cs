namespace Uniform.Chat.Server.Management;

using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Rooms;

/// <summary>What the dashboard shows of a server and changes on it: its state, accounts, rooms and reports. <see cref="LocalServerAdmin"/> works in the server's own process; <see cref="HttpServerAdmin"/> over the admin socket or the network, for <c>uniform-chat-server manage</c>.</summary>
public interface IServerAdmin
{
    /// <summary>Return the server's state; what the connection cannot see is null.</summary>
    Task<ServerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Return the local accounts, deactivated ones too.</summary>
    Task<IReadOnlyList<AdminUser>> GetUsersAsync(CancellationToken cancellationToken);

    /// <summary>Create an account named <paramref name="name"/>, the part before the colon; return its user ID; fail when it exists.</summary>
    Task<string> CreateUserAsync(string name, string password, bool admin, CancellationToken cancellationToken);

    /// <summary>Set a new password, logging out every device of the account.</summary>
    Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken);

    /// <summary>Give or take server admin rights.</summary>
    Task SetAdminAsync(string userId, bool admin, CancellationToken cancellationToken);

    /// <summary>Suspend an account, or end its suspension.</summary>
    Task SetSuspendedAsync(string userId, bool suspended, CancellationToken cancellationToken);

    /// <summary>Lock an account, or unlock it.</summary>
    Task SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken);

    /// <summary>Deactivate an account, or reactivate it.</summary>
    Task SetDeactivatedAsync(string userId, bool deactivated, CancellationToken cancellationToken);

    /// <summary>Return the server's rooms.</summary>
    Task<IReadOnlyList<AdminRoom>> GetRoomsAsync(CancellationToken cancellationToken);

    /// <summary>List a room in the public room directory, or take it out.</summary>
    Task SetPublishedAsync(string roomId, bool published, CancellationToken cancellationToken);

    /// <summary>Return the users' reports, newest first; resolved ones with <paramref name="includeResolved"/>.</summary>
    Task<IReadOnlyList<Report>> GetReportsAsync(bool includeResolved, CancellationToken cancellationToken);

    /// <summary>Resolve a report, or open it again.</summary>
    Task SetResolvedAsync(long reportId, bool resolved, CancellationToken cancellationToken);

    /// <summary>Delete a report.</summary>
    Task DeleteReportAsync(long reportId, CancellationToken cancellationToken);
}

/// <summary>A server's state as the dashboard shows it; what the connection cannot see is null.</summary>
public sealed record ServerSnapshot
{
    /// <summary>The server's name, the part after the colon in user IDs.</summary>
    public required string ServerName { get; init; }
    /// <summary>The server's version.</summary>
    public string? Version { get; init; }
    /// <summary>Where it listens, or the address it was reached at.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];
    /// <summary>When it started.</summary>
    public DateTimeOffset? Started { get; init; }
    /// <summary>Requests answered.</summary>
    public long? Requests { get; init; }
    /// <summary>Requests in progress, including waiting syncs.</summary>
    public int? ActiveRequests { get; init; }
    /// <summary>Requests answered with a server error.</summary>
    public long? FailedRequests { get; init; }
    /// <summary>The data folder.</summary>
    public string? DataDirectory { get; init; }
    /// <summary>The folder of session logs.</summary>
    public string? LogDirectory { get; init; }
    /// <summary>This session's log.</summary>
    public string? LogFile { get; init; }
    /// <summary>The settings file.</summary>
    public string? SettingsFile { get; init; }
}
