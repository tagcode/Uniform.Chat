namespace Uniform.Chat.Server.Management;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;

/// <summary>The dashboard's view of a server over HTTP: its admin API, through the local admin socket or over the network as a logged-in admin. Only the socket also tells the server's counters and files.</summary>
public sealed class HttpServerAdmin : IServerAdmin, IAsyncDisposable
{
    /// <summary>Most items asked for in one page.</summary>
    private const int PageSize = 500;
    private readonly HttpClient http;
    private readonly MatrixClient client;
    private readonly bool socket;
    private readonly string serverName;

    /// <summary>Use <paramref name="client"/> over <paramref name="http"/>.</summary>
    private HttpServerAdmin(HttpClient http, MatrixClient client, bool socket, string serverName)
    {
        // Connection and identity
        this.http = http;
        this.client = client;
        this.socket = socket;
        this.serverName = serverName;
    }

    /// <summary>Whether this goes through the local admin socket, which sees the server's files, rather than the network.</summary>
    public bool IsSocket => socket;

    /// <summary>Connect to the admin socket at <paramref name="path"/> and read the server's name.</summary>
    /// <exception cref="HttpRequestException">When nothing listens there, or this user may not open it.</exception>
    public static async Task<HttpServerAdmin> ConnectSocketAsync(string path, CancellationToken cancellationToken)
    {
        // Every connection to the socket; the server ignores the token there
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, token) =>
            {
                Socket unix = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(unix, ownsSocket: true);
                }
                catch
                {
                    unix.Dispose();
                    throw;
                }
            },
        };
        Uri address = new("http://localhost/");
        HttpClient http = new(handler) { BaseAddress = address };
        MatrixClient client = new(http, new MatrixSession { Homeserver = address, UserId = AdminSocket.Requester.UserId, DeviceId = AdminSocket.Requester.DeviceId, AccessToken = "admin-socket" });

        // The server's name from its status
        try
        {
            JsonObject status = await http.GetFromJsonAsync<JsonObject>(AdminSocket.StatusPath.TrimStart('/'), cancellationToken) ?? [];
            return new HttpServerAdmin(http, client, socket: true, status["server_name"]?.GetValue<string>() ?? "");
        }
        catch
        {
            // On failure
            http.Dispose();
            throw;
        }
    }

    /// <summary>Log in at <paramref name="homeserver"/> as <paramref name="user"/>, who must be a server admin.</summary>
    /// <exception cref="MatrixException">When the login fails, or the user is not a server admin; the session is then logged out.</exception>
    public static async Task<HttpServerAdmin> LoginAsync(Uri homeserver, string user, string password, CancellationToken cancellationToken)
    {
        // Log in, then check the rights
        HttpClient http = new() { BaseAddress = homeserver };
        MatrixClient client = new(http, homeserver);
        try
        {
            LoginResponse login = await client.LoginWithPasswordAsync(user, password, "uniform-chat-server manage", cancellationToken);
            if (await client.IsServerAdminAsync(cancellationToken)) return new HttpServerAdmin(http, client, socket: false, login.UserId[(login.UserId.IndexOf(':') + 1)..]);
            await client.LogoutAsync(cancellationToken);
            throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, $"{login.UserId} is not a server admin.");
        }
        catch
        {
            // On failure
            http.Dispose();
            throw;
        }
    }

    /// <summary>Log out of a network session, so no access token is left behind; then close the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        // The socket has no session
        try
        {
            if (!socket && client.Session is not null) await client.LogoutAsync();
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException)
        {
            // The server may be gone; the token then dies with its device later
        }
        // Done
        http.Dispose();
    }

    /// <summary>Return the counters, addresses and files from the socket; over the network only the name and address.</summary>
    public async Task<ServerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        // Network: what the connection knows
        if (!socket) return new ServerSnapshot { ServerName = serverName, Addresses = [client.Homeserver.ToString()] };

        // Socket: the server's own status
        JsonObject status = await http.GetFromJsonAsync<JsonObject>(AdminSocket.StatusPath.TrimStart('/'), cancellationToken) ?? [];
        // Return snapshot
        return new ServerSnapshot
        {
            ServerName = status["server_name"]?.GetValue<string>() ?? serverName,
            Version = status["version"]?.GetValue<string>(),
            Addresses = [.. (status["addresses"] as JsonArray ?? []).Select(a => a!.GetValue<string>())],
            Started = status["started_ts"]?.GetValue<long>() is long started ? DateTimeOffset.FromUnixTimeMilliseconds(started) : null,
            Requests = status["requests"]?.GetValue<long>(),
            ActiveRequests = status["active_requests"]?.GetValue<int>(),
            FailedRequests = status["failed_requests"]?.GetValue<long>(),
            DataDirectory = status["data_directory"]?.GetValue<string>(),
            LogDirectory = status["log_directory"]?.GetValue<string>(),
            LogFile = status["log_file"]?.GetValue<string>(),
            SettingsFile = status["settings_file"]?.GetValue<string>(),
        };
    }

    /// <summary>Return every local account, page by page.</summary>
    public async Task<IReadOnlyList<AdminUser>> GetUsersAsync(CancellationToken cancellationToken)
    {
        // Until no next page
        List<AdminUser> users = [];
        int from = 0;
        while (true)
        {
            AdminAccountsResponse page = await client.GetAdminAccountsAsync(includeDeactivated: true, from: from, limit: PageSize, cancellationToken: cancellationToken);
            users.AddRange(page.Users.Select(ToUser));
            if (page.NextToken is null || !int.TryParse(page.NextToken, out from)) break;
        }
        // Return users
        return users;
    }

    /// <summary>Create the account, refusing one that exists, as the admin API would change it instead.</summary>
    public async Task<string> CreateUserAsync(string name, string password, bool admin, CancellationToken cancellationToken)
    {
        // Assert a new name
        string userId = $"@{name}:{serverName}";
        try
        {
            await client.GetAdminAccountAsync(userId, cancellationToken);
            throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.UserInUse, $"{userId} exists.");
        }
        catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
        {
            // Free
        }
        // Return created ID
        return (await client.CreateAdminAccountAsync(userId, password, admin: admin, cancellationToken: cancellationToken)).Name;
    }

    /// <inheritdoc />
    public Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken) => client.ResetPasswordAsync(userId, password, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task SetAdminAsync(string userId, bool admin, CancellationToken cancellationToken) => client.SetServerAdminAsync(userId, admin, cancellationToken);

    /// <inheritdoc />
    public Task SetSuspendedAsync(string userId, bool suspended, CancellationToken cancellationToken) => client.SetSuspendedAsync(userId, suspended, cancellationToken);

    /// <inheritdoc />
    public Task SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken) => client.SetLockedAsync(userId, locked, cancellationToken);

    /// <inheritdoc />
    public Task SetDeactivatedAsync(string userId, bool deactivated, CancellationToken cancellationToken) => deactivated ? client.DeactivateUserAsync(userId, cancellationToken) : client.ReactivateUserAsync(userId, cancellationToken);

    /// <summary>Return every room, page by page.</summary>
    public async Task<IReadOnlyList<AdminRoom>> GetRoomsAsync(CancellationToken cancellationToken)
    {
        // Until no next batch
        List<AdminRoom> rooms = [];
        int? from = 0;
        while (from is int start)
        {
            AdminRoomsResponse page = await client.GetAdminRoomsAsync(from: start, limit: PageSize, cancellationToken: cancellationToken);
            rooms.AddRange(page.Rooms.Select(r => new AdminRoom { RoomId = r.RoomId, Name = r.Name, Alias = r.CanonicalAlias, Members = r.JoinedMembers, Published = r.Public, Encrypted = r.Encryption is not null, Version = r.Version ?? "", Creator = r.Creator ?? "" }));
            from = page.NextBatch;
        }
        // Return rooms
        return rooms;
    }

    /// <inheritdoc />
    public Task SetPublishedAsync(string roomId, bool published, CancellationToken cancellationToken) => client.SetRoomDirectoryVisibilityAsync(roomId, published, cancellationToken);

    /// <summary>Return the reports, page by page, without resolved ones unless asked.</summary>
    public async Task<IReadOnlyList<Report>> GetReportsAsync(bool includeResolved, CancellationToken cancellationToken)
    {
        // Until no next page
        List<Report> reports = [];
        int from = 0;
        while (true)
        {
            AdminReportsResponse page = await client.GetReportsAsync(from, PageSize, cancellationToken);
            reports.AddRange(page.EventReports.Where(r => includeResolved || !r.Resolved).Select(ToReport));
            if (page.NextToken is null || !int.TryParse(page.NextToken, out from)) break;
        }
        // Return reports
        return reports;
    }

    /// <inheritdoc />
    public Task SetResolvedAsync(long reportId, bool resolved, CancellationToken cancellationToken) => client.SetReportResolvedAsync(reportId, resolved, cancellationToken);

    /// <inheritdoc />
    public Task DeleteReportAsync(long reportId, CancellationToken cancellationToken) => client.DeleteReportAsync(reportId, cancellationToken);

    /// <summary>Return an account of the admin API as the dashboard lists it.</summary>
    private static AdminUser ToUser(AdminAccount account) => new()
    {
        UserId = account.Name,
        DisplayName = account.DisplayName,
        AvatarUrl = account.AvatarUrl,
        Admin = account.Admin,
        Deactivated = account.Deactivated,
        Suspended = account.Suspended,
        Locked = account.Locked,
        Devices = account.Devices ?? 0,
        Rooms = account.JoinedRooms ?? 0,
        Created = DateTimeOffset.FromUnixTimeMilliseconds(account.CreationTs ?? 0),
    };

    /// <summary>Return a report of the admin API as the dashboard lists it.</summary>
    private static Report ToReport(AdminReport report) => new()
    {
        Id = report.Id,
        Reporter = report.UserId,
        RoomId = report.RoomId,
        EventId = report.EventId,
        UserId = report.TargetUserId,
        Reason = report.Reason ?? "",
        Received = DateTimeOffset.FromUnixTimeMilliseconds(report.ReceivedTs),
        Resolved = report.Resolved,
        EventSummary = report.EventSummary,
    };
}
