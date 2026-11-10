namespace Uniform.Chat.Server.Management;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Rooms;

/// <summary>The dashboard's view of the server it runs in: its services, called directly.</summary>
/// <param name="admin">Accounts and rooms.</param>
/// <param name="reports">Users' reports.</param>
/// <param name="status">Live counters.</param>
/// <param name="server">The web server, for its addresses.</param>
/// <param name="options">The server's name.</param>
/// <param name="storage">The data and log folders.</param>
/// <param name="environment">The application name, which names the folders.</param>
/// <param name="fileLogger">This session's log.</param>
/// <param name="settings">The settings file.</param>
public sealed class LocalServerAdmin(AdminService admin, ReportService reports, ServerStatus status, IServer server, IOptions<ServerOptions> options, IOptions<ServerStorageOptions> storage, IHostEnvironment environment, FileLoggerProvider fileLogger, SettingsStore settings) : IServerAdmin
{
    /// <summary>Return the counters, addresses and files.</summary>
    public Task<ServerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ServerSnapshot
    {
        ServerName = options.Value.ServerName,
        Version = typeof(ServerStatus).Assembly.GetName().Version?.ToString(3),
        Addresses = [.. server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []],
        Started = status.Started,
        Requests = status.Requests,
        ActiveRequests = status.ActiveRequests,
        FailedRequests = status.FailedRequests,
        DataDirectory = storage.Value.ResolveDataDirectory(environment.ApplicationName),
        LogDirectory = storage.Value.ResolveLogDirectory(environment.ApplicationName),
        LogFile = fileLogger.FilePath,
        SettingsFile = settings.Path,
    });

    /// <inheritdoc />
    public Task<IReadOnlyList<AdminUser>> GetUsersAsync(CancellationToken cancellationToken) => admin.GetUsersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<string> CreateUserAsync(string name, string password, bool isAdmin, CancellationToken cancellationToken) => admin.CreateUserAsync(name, password, isAdmin, cancellationToken);

    /// <inheritdoc />
    public Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken) => admin.ResetPasswordAsync(userId, password, cancellationToken);

    /// <inheritdoc />
    public Task SetAdminAsync(string userId, bool isAdmin, CancellationToken cancellationToken) => admin.SetAdminAsync(userId, isAdmin, cancellationToken);

    /// <inheritdoc />
    public Task SetSuspendedAsync(string userId, bool suspended, CancellationToken cancellationToken) => admin.SetSuspendedAsync(userId, suspended, cancellationToken);

    /// <inheritdoc />
    public Task SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken) => admin.SetLockedAsync(userId, locked, cancellationToken);

    /// <inheritdoc />
    public Task SetDeactivatedAsync(string userId, bool deactivated, CancellationToken cancellationToken) => admin.SetDeactivatedAsync(userId, deactivated, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdminRoom>> GetRoomsAsync(CancellationToken cancellationToken) => admin.GetRoomsAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetPublishedAsync(string roomId, bool published, CancellationToken cancellationToken) => admin.SetPublishedAsync(roomId, published, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Report>> GetReportsAsync(bool includeResolved, CancellationToken cancellationToken) => reports.GetAsync(includeResolved, cancellationToken);

    /// <inheritdoc />
    public Task SetResolvedAsync(long reportId, bool resolved, CancellationToken cancellationToken) => reports.SetResolvedAsync(reportId, resolved, cancellationToken);

    /// <inheritdoc />
    public Task DeleteReportAsync(long reportId, CancellationToken cancellationToken) => reports.DeleteAsync(reportId, cancellationToken);
}
