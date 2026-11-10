namespace Uniform.Matrix.Protocol.Client;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

// Server administration: the spec's whois, suspension and locks, and Synapse's admin API

public sealed partial class MatrixClient
{
    /// <summary>Return a user's devices and connections with <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3adminwhoisuserid">whois</see>; servers allow it for admins, and for oneself.</summary>
    public Task<WhoisResponse> WhoisAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<WhoisResponse>(HttpMethod.Get, $"_matrix/client/v3/admin/whois/{Escape(userId)}", null, authenticated: true, cancellationToken);

    /// <summary>Return whether the signed-in user is a server admin, by asking for an admin-only list; false on servers without Synapse's admin API.</summary>
    public async Task<bool> IsServerAdminAsync(CancellationToken cancellationToken = default)
    {
        // Forbidden for others, unrecognized where the API is missing
        try
        {
            await SendAsync(HttpMethod.Get, "_synapse/admin/v1/server_version", null, authenticated: true, cancellationToken);
            return true;
        }
        catch (MatrixException ex) when (ex.Errcode is ErrorCodes.Forbidden or ErrorCodes.Unrecognized or ErrorCodes.NotFound)
        {
            // On not an admin
            return false;
        }
    }

    /// <summary>Return whether an account is <see href="https://spec.matrix.org/latest/client-server-api/#account-suspension">suspended</see>; for admins.</summary>
    public async Task<bool> IsSuspendedAsync(string userId, CancellationToken cancellationToken = default) =>
        (await SendAsync<JsonObject>(HttpMethod.Get, $"_matrix/client/v1/admin/suspend/{Escape(userId)}", null, authenticated: true, cancellationToken))["suspended"]?.GetValue<bool>() == true;

    /// <summary>Suspend an account, so it can read but not send, join or invite; or end the suspension. For admins.</summary>
    public Task SetSuspendedAsync(string userId, bool suspended, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v1/admin/suspend/{Escape(userId)}", new { suspended }, authenticated: true, cancellationToken);

    /// <summary>Return whether an account is <see href="https://spec.matrix.org/latest/client-server-api/#account-locking">locked</see>; for admins.</summary>
    public async Task<bool> IsLockedAsync(string userId, CancellationToken cancellationToken = default) =>
        (await SendAsync<JsonObject>(HttpMethod.Get, $"_matrix/client/v1/admin/lock/{Escape(userId)}", null, authenticated: true, cancellationToken))["locked"]?.GetValue<bool>() == true;

    /// <summary>Lock an account, so its sessions are refused until it is unlocked; or unlock it. For admins.</summary>
    public Task SetLockedAsync(string userId, bool locked, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v1/admin/lock/{Escape(userId)}", new { locked }, authenticated: true, cancellationToken);

    /// <summary>Return a page of local accounts, optionally matching <paramref name="name"/> and including deactivated ones; for admins.</summary>
    public Task<AdminAccountsResponse> GetAdminAccountsAsync(string? name = null, bool includeDeactivated = false, int from = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        SendAsync<AdminAccountsResponse>(HttpMethod.Get, WithQuery("_synapse/admin/v2/users", [("from", from.ToString()), ("limit", limit.ToString()), ("name", name), ("deactivated", includeDeactivated ? "true" : null), ("locked", "true")]), null, authenticated: true, cancellationToken);

    /// <summary>Return a local account; for admins.</summary>
    public Task<AdminAccount> GetAdminAccountAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<AdminAccount>(HttpMethod.Get, $"_synapse/admin/v2/users/{Escape(userId)}", null, authenticated: true, cancellationToken);

    /// <summary>Create an account that logs in with <paramref name="password"/>; for admins.</summary>
    public Task<AdminAccount> CreateAdminAccountAsync(string userId, string password, string? displayName = null, bool admin = false, CancellationToken cancellationToken = default) =>
        SendAsync<AdminAccount>(HttpMethod.Put, $"_synapse/admin/v2/users/{Escape(userId)}", new { password, displayname = displayName, admin }, authenticated: true, cancellationToken);

    /// <summary>Set a new password for an account, logging out its devices unless <paramref name="logoutDevices"/> is false; for admins.</summary>
    public Task ResetPasswordAsync(string userId, string newPassword, bool logoutDevices = true, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"_synapse/admin/v1/reset_password/{Escape(userId)}", new { new_password = newPassword, logout_devices = logoutDevices }, authenticated: true, cancellationToken);

    /// <summary>Give or take server admin rights; for admins.</summary>
    public Task SetServerAdminAsync(string userId, bool admin, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_synapse/admin/v1/users/{Escape(userId)}/admin", new { admin }, authenticated: true, cancellationToken);

    /// <summary>Deactivate an account: it leaves its rooms and cannot log in again; for admins.</summary>
    public Task DeactivateUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"_synapse/admin/v1/deactivate/{Escape(userId)}", new { erase = false }, authenticated: true, cancellationToken);

    /// <summary>Reactivate a deactivated account, so it can log in again; for admins.</summary>
    public Task ReactivateUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_synapse/admin/v2/users/{Escape(userId)}", new { deactivated = false }, authenticated: true, cancellationToken);

    /// <summary>Return the registration tokens; with <paramref name="valid"/>, only those that may or may not register now. For admins.</summary>
    public async Task<IReadOnlyList<AdminRegistrationToken>> GetRegistrationTokensAsync(bool? valid = null, CancellationToken cancellationToken = default) =>
        (await SendAsync<AdminRegistrationTokensResponse>(HttpMethod.Get, WithQuery("_synapse/admin/v1/registration_tokens", [("valid", valid?.ToString().ToLowerInvariant())]), null, authenticated: true, cancellationToken)).RegistrationTokens;

    /// <summary>Create a registration token: <paramref name="token"/>, or a random one; usable <paramref name="usesAllowed"/> times, or any number, until <paramref name="expiry"/>, or forever. For admins.</summary>
    public Task<AdminRegistrationToken> CreateRegistrationTokenAsync(string? token = null, int? usesAllowed = null, DateTimeOffset? expiry = null, CancellationToken cancellationToken = default) =>
        SendAsync<AdminRegistrationToken>(HttpMethod.Post, "_synapse/admin/v1/registration_tokens/new", new { token, uses_allowed = usesAllowed, expiry_time = expiry?.ToUnixTimeMilliseconds() }, authenticated: true, cancellationToken);

    /// <summary>Change how many times a token may be used, null for any number, and when it expires, null for never; for admins.</summary>
    public Task<AdminRegistrationToken> UpdateRegistrationTokenAsync(string token, int? usesAllowed, DateTimeOffset? expiry, CancellationToken cancellationToken = default) =>
        SendAsync<AdminRegistrationToken>(HttpMethod.Put, $"_synapse/admin/v1/registration_tokens/{Escape(token)}", new JsonObject { ["uses_allowed"] = usesAllowed, ["expiry_time"] = expiry?.ToUnixTimeMilliseconds() }, authenticated: true, cancellationToken);

    /// <summary>Delete a registration token; for admins.</summary>
    public Task DeleteRegistrationTokenAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_synapse/admin/v1/registration_tokens/{Escape(token)}", null, authenticated: true, cancellationToken);

    /// <summary>Return a page of reports, newest first; for admins.</summary>
    public Task<AdminReportsResponse> GetReportsAsync(int from = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        SendAsync<AdminReportsResponse>(HttpMethod.Get, WithQuery("_synapse/admin/v1/event_reports", [("from", from.ToString()), ("limit", limit.ToString())]), null, authenticated: true, cancellationToken);

    /// <summary>Mark a report resolved, or open it again; for admins of Uniform servers, as Synapse has no such call.</summary>
    public Task SetReportResolvedAsync(long reportId, bool resolved, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_synapse/admin/v1/event_reports/{reportId}", new { resolved }, authenticated: true, cancellationToken);

    /// <summary>Delete a report; for admins.</summary>
    public Task DeleteReportAsync(long reportId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_synapse/admin/v1/event_reports/{reportId}", null, authenticated: true, cancellationToken);

    /// <summary>Return a page of rooms, most members first, optionally matching <paramref name="searchTerm"/>; for admins.</summary>
    public Task<AdminRoomsResponse> GetAdminRoomsAsync(string? searchTerm = null, int from = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        SendAsync<AdminRoomsResponse>(HttpMethod.Get, WithQuery("_synapse/admin/v1/rooms", [("from", from.ToString()), ("limit", limit.ToString()), ("search_term", searchTerm)]), null, authenticated: true, cancellationToken);

    /// <summary>Return the joined members of a room; for admins.</summary>
    public async Task<IReadOnlyList<string>> GetAdminRoomMembersAsync(string roomId, CancellationToken cancellationToken = default) =>
        (await SendAsync<JsonObject>(HttpMethod.Get, $"_synapse/admin/v1/rooms/{Escape(roomId)}/members", null, authenticated: true, cancellationToken))["members"]?.Deserialize<List<string>>(MatrixJson.Options) ?? [];

    /// <summary>Shut a room down on the server: its local members leave, its aliases go, and with <paramref name="block"/> no local user can join it again. For admins.</summary>
    public Task<AdminRoomShutdown> ShutdownRoomAsync(string roomId, bool block, string? message = null, CancellationToken cancellationToken = default) =>
        SendAsync<AdminRoomShutdown>(HttpMethod.Delete, $"_synapse/admin/v1/rooms/{Escape(roomId)}", new { block, purge = false, message }, authenticated: true, cancellationToken);

    /// <summary>Delete media of the server; for admins.</summary>
    public Task DeleteMediaAsync(string serverName, string mediaId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"_synapse/admin/v1/media/{Escape(serverName)}/{Escape(mediaId)}", null, authenticated: true, cancellationToken);

    /// <summary>Send a <see href="https://spec.matrix.org/latest/client-server-api/#server-notices">server notice</see> to a local user; return the event ID. For admins.</summary>
    public async Task<string> SendServerNoticeAsync(string userId, string text, CancellationToken cancellationToken = default) =>
        (await SendAsync<JsonObject>(HttpMethod.Post, "_synapse/admin/v1/send_server_notice", new { user_id = userId, content = new { msgtype = MessageTypes.Text, body = text } }, authenticated: true, cancellationToken))["event_id"]!.GetValue<string>();
}
