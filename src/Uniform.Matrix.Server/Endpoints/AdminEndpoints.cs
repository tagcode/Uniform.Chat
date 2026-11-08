namespace Uniform.Matrix.Server.Endpoints;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Http;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>Remote administration for server admins: the spec's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3adminwhoisuserid">whois</see>, <see href="https://github.com/matrix-org/matrix-spec-proposals/pull/4323">suspension and locks</see>, and a subset of <see href="https://element-hq.github.io/synapse/latest/usage/administration/admin_api/">Synapse's admin API</see> at its paths, so tools made for Synapse work too.</summary>
public static class AdminEndpoints
{
    /// <summary>Most items in one page of a list.</summary>
    private const int MaxLimit = 1000;

    /// <summary>Map the admin endpoints.</summary>
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder routes)
    {
        // Spec: whois, for admins and for oneself
        routes.MapGet("/_matrix/client/v3/admin/whois/{userId}", WhoisAsync).RequireAccessToken();

        // MSC4323: suspension and locks, at the stable and the unstable prefix
        foreach (string prefix in new[] { "/_matrix/client/v1/admin", "/_matrix/client/unstable/uk.timedout.msc4323/admin" })
        {
            RouteGroupBuilder spec = routes.MapGroup(prefix).RequireAdmin();
            spec.MapGet("/suspend/{userId}", async (string userId, AdminService admin, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["suspended"] = await admin.IsSuspendedAsync(userId, cancellationToken) }));
            spec.MapPut("/suspend/{userId}", async (string userId, JsonObject body, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
            {
                bool suspended = Flag(body, "suspended");
                if (suspended) AssertNotSelf(http, userId, "suspend");
                await admin.SetSuspendedAsync(userId, suspended, cancellationToken);
                return MatrixHttp.Json(new JsonObject { ["suspended"] = suspended });
            });
            spec.MapGet("/lock/{userId}", async (string userId, AdminService admin, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["locked"] = await admin.IsLockedAsync(userId, cancellationToken) }));
            spec.MapPut("/lock/{userId}", async (string userId, JsonObject body, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
            {
                bool locked = Flag(body, "locked");
                if (locked) AssertNotSelf(http, userId, "lock");
                await admin.SetLockedAsync(userId, locked, cancellationToken);
                return MatrixHttp.Json(new JsonObject { ["locked"] = locked });
            });
        }

        // Synapse's admin API
        RouteGroupBuilder synapse = routes.MapGroup("/_synapse/admin").RequireAdmin();
        synapse.MapGet("/v1/server_version", () => MatrixHttp.Json(new JsonObject { ["server_version"] = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" }));
        MapUsers(synapse);
        MapRegistrationTokens(synapse);
        MapReports(synapse);
        MapRooms(synapse);
        MapMediaAndNotices(synapse);
        // Return routes
        return routes;
    }

    /// <summary>Map account management: list, details, create or change, password, admin rights, deactivation, whois, suspension, rooms and media.</summary>
    private static void MapUsers(RouteGroupBuilder synapse)
    {
        // List and details
        synapse.MapGet("/v2/users", async (HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            IQueryCollection query = http.Request.Query;
            string? name = query["name"].FirstOrDefault() ?? query["user_id"].FirstOrDefault();
            bool deactivated = Bool(query, "deactivated") ?? false;
            bool locked = Bool(query, "locked") ?? false;
            bool? admins = Bool(query, "admins");
            IEnumerable<AdminUser> users = (await admin.GetUsersAsync(cancellationToken))
                .Where(u => (deactivated || !u.Deactivated) && (locked || !u.Locked) && (admins is null || u.Admin == admins))
                .Where(u => name is null || u.UserId.Contains(name, StringComparison.OrdinalIgnoreCase) || u.DisplayName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
            if (query["dir"] == "b") users = users.Reverse();
            return Page(http, "users", [.. users.Select(ToJson)]);
        });
        synapse.MapGet("/v2/users/{userId}", async (string userId, AdminService admin, CancellationToken cancellationToken) => MatrixHttp.Json(ToJson(await admin.GetUserAsync(userId, cancellationToken))));
        synapse.MapPut("/v2/users/{userId}", async (string userId, JsonObject body, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            AdminUserChange change = new()
            {
                Password = Text(body, "password"),
                LogoutDevices = body["logout_devices"] is JsonValue logout ? logout.GetValue<bool>() : true,
                DisplayName = Text(body, "displayname"),
                AvatarUrl = Text(body, "avatar_url"),
                Admin = body["admin"] is JsonValue a ? a.GetValue<bool>() : null,
                Deactivated = body["deactivated"] is JsonValue d ? d.GetValue<bool>() : null,
                Locked = body["locked"] is JsonValue l ? l.GetValue<bool>() : null,
            };
            if (change.Admin == false || change.Deactivated == true || change.Locked == true) AssertNotSelf(http, userId, "take admin rights from, deactivate or lock");
            bool created = await admin.CreateOrUpdateUserAsync(userId, change, cancellationToken);
            return MatrixHttp.Json(ToJson(await admin.GetUserAsync(userId, cancellationToken)), created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // Password, rights, deactivation
        synapse.MapPost("/v1/reset_password/{userId}", async (string userId, JsonObject body, AdminService admin, AccountService accounts, CancellationToken cancellationToken) =>
        {
            string password = Text(body, "new_password") ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing new_password.");
            bool logout = body["logout_devices"] is JsonValue value ? value.GetValue<bool>() : true;
            await admin.CreateOrUpdateUserAsync(await ExistingAsync(admin, userId, cancellationToken), new AdminUserChange { Password = password, LogoutDevices = logout }, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        synapse.MapGet("/v1/users/{userId}/admin", async (string userId, AdminService admin, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["admin"] = (await admin.GetUserAsync(userId, cancellationToken)).Admin }));
        synapse.MapPut("/v1/users/{userId}/admin", async (string userId, JsonObject body, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            bool isAdmin = Flag(body, "admin");
            if (!isAdmin) AssertNotSelf(http, userId, "take admin rights from");
            await admin.SetAdminAsync(userId, isAdmin, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
        synapse.MapPost("/v1/deactivate/{userId}", async (string userId, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            AssertNotSelf(http, userId, "deactivate");
            await admin.DeactivateAsync(userId, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["id_server_unbind_result"] = "success" });
        });

        // Sessions, suspension, rooms and uploads
        synapse.MapGet("/v1/whois/{userId}", WhoisAsync);
        synapse.MapPut("/v1/suspend/{userId}", async (string userId, JsonObject body, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            bool suspend = Flag(body, "suspend");
            if (suspend) AssertNotSelf(http, userId, "suspend");
            await admin.SetSuspendedAsync(userId, suspend, cancellationToken);
            return MatrixHttp.Json(new JsonObject { [$"user_{userId}_suspended"] = suspend });
        });
        synapse.MapGet("/v1/users/{userId}/joined_rooms", async (string userId, AdminService admin, RoomService rooms, CancellationToken cancellationToken) =>
        {
            await admin.GetUserAsync(userId, cancellationToken);
            IReadOnlyList<string> joined = await rooms.GetJoinedRoomsAsync(userId, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["joined_rooms"] = new JsonArray([.. joined.Select(r => (JsonNode)r)]), ["total"] = joined.Count });
        });
        synapse.MapGet("/v1/users/{userId}/media", async (string userId, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
            Page(http, "media", [.. (await admin.GetUserMediaAsync(userId, cancellationToken)).Select(ToJson)]));
    }

    /// <summary>Map registration tokens: list, details, create, change and delete.</summary>
    private static void MapRegistrationTokens(RouteGroupBuilder synapse)
    {
        // List and details
        synapse.MapGet("/v1/registration_tokens", async (HttpContext http, RegistrationTokens tokens, CancellationToken cancellationToken) =>
            MatrixHttp.Json(new JsonObject { ["registration_tokens"] = new JsonArray([.. (await tokens.GetAllAsync(Bool(http.Request.Query, "valid"), cancellationToken)).Select(ToJson)]) }));
        synapse.MapGet("/v1/registration_tokens/{token}", async (string token, RegistrationTokens tokens, CancellationToken cancellationToken) => MatrixHttp.Json(ToJson(await tokens.GetAsync(token, cancellationToken))));

        // Create, change, delete
        synapse.MapPost("/v1/registration_tokens/new", async (JsonObject body, RegistrationTokens tokens, CancellationToken cancellationToken) =>
            MatrixHttp.Json(ToJson(await tokens.CreateAsync(Text(body, "token"), body["length"] is JsonValue length ? length.GetValue<int>() : 16, NullableInt(body, "uses_allowed").Value, NullableLong(body, "expiry_time").Value, cancellationToken))));
        synapse.MapPut("/v1/registration_tokens/{token}", async (string token, JsonObject body, RegistrationTokens tokens, CancellationToken cancellationToken) =>
            MatrixHttp.Json(ToJson(await tokens.UpdateAsync(token, NullableInt(body, "uses_allowed"), NullableLong(body, "expiry_time"), cancellationToken))));
        synapse.MapDelete("/v1/registration_tokens/{token}", async (string token, RegistrationTokens tokens, CancellationToken cancellationToken) =>
        {
            await tokens.DeleteAsync(token, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
    }

    /// <summary>Map reports of events, rooms and users: list, details, delete.</summary>
    private static void MapReports(RouteGroupBuilder synapse)
    {
        // List, newest first unless asked otherwise; resolved ones too
        synapse.MapGet("/v1/event_reports", async (HttpContext http, ReportService reports, CancellationToken cancellationToken) =>
        {
            IEnumerable<Report> all = await reports.GetAsync(includeResolved: true, cancellationToken);
            if (http.Request.Query["dir"] == "f") all = all.Reverse();
            return Page(http, "event_reports", [.. all.Select(ToJson)]);
        });
        synapse.MapGet("/v1/event_reports/{reportId:long}", async (long reportId, ReportService reports, CancellationToken cancellationToken) =>
            (await reports.GetAsync(includeResolved: true, cancellationToken)).FirstOrDefault(r => r.Id == reportId) is Report report
                ? MatrixHttp.Json(ToJson(report))
                : MatrixHttp.Error(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown report."));
        synapse.MapDelete("/v1/event_reports/{reportId:long}", async (long reportId, ReportService reports, CancellationToken cancellationToken) =>
        {
            await reports.DeleteAsync(reportId, cancellationToken);
            return MatrixHttp.Json(new JsonObject());
        });
    }

    /// <summary>Map rooms: list, details, members, block and shutdown.</summary>
    private static void MapRooms(RouteGroupBuilder synapse)
    {
        // List, most members first, and details
        synapse.MapGet("/v1/rooms", async (HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            string? term = http.Request.Query["search_term"].FirstOrDefault();
            IEnumerable<AdminRoom> rooms = (await admin.GetRoomsAsync(cancellationToken)).Where(r => term is null || r.RoomId.Contains(term, StringComparison.OrdinalIgnoreCase) || r.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true || r.Alias?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
            if (http.Request.Query["dir"] == "b") rooms = rooms.Reverse();
            List<JsonNode> items = [.. rooms.Select(ToJson)];
            (int from, int limit) = Paging(http);
            JsonObject page = new() { ["rooms"] = new JsonArray([.. items.Skip(from).Take(limit).Select(i => i.DeepClone())]), ["offset"] = from, ["total_rooms"] = items.Count };
            if (from + limit < items.Count) page["next_batch"] = from + limit;
            if (from > 0) page["prev_batch"] = Math.Max(0, from - limit);
            return MatrixHttp.Json(page);
        });
        synapse.MapGet("/v1/rooms/{roomId}", async (string roomId, AdminService admin, CancellationToken cancellationToken) =>
            (await admin.GetRoomsAsync(cancellationToken)).FirstOrDefault(r => r.RoomId == roomId) is AdminRoom room
                ? MatrixHttp.Json(ToJson(room))
                : MatrixHttp.Error(HttpStatusCode.NotFound, ErrorCodes.NotFound, "Unknown room."));
        synapse.MapGet("/v1/rooms/{roomId}/members", async (string roomId, AdminService admin, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<string> members = await admin.GetRoomMembersAsync(roomId, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["members"] = new JsonArray([.. members.Select(m => (JsonNode)m)]), ["total"] = members.Count });
        });

        // Block and shutdown
        synapse.MapGet("/v1/rooms/{roomId}/block", async (string roomId, AdminService admin, CancellationToken cancellationToken) => MatrixHttp.Json(new JsonObject { ["block"] = await admin.IsRoomBlockedAsync(roomId, cancellationToken) }));
        synapse.MapPut("/v1/rooms/{roomId}/block", async (string roomId, JsonObject body, AdminService admin, CancellationToken cancellationToken) =>
        {
            bool block = Flag(body, "block");
            await admin.SetRoomBlockedAsync(roomId, block, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["block"] = block });
        });
        synapse.MapDelete("/v1/rooms/{roomId}", async (string roomId, HttpContext http, AdminService admin, CancellationToken cancellationToken) =>
        {
            JsonObject body = http.Request.ContentLength is not 0 && http.Request.HasJsonContentType() ? await http.Request.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [] : [];
            RoomShutdown done = await admin.ShutdownRoomAsync(roomId, body["block"] is JsonValue block && block.GetValue<bool>(), Text(body, "message"), cancellationToken);
            return MatrixHttp.Json(new JsonObject
            {
                ["kicked_users"] = new JsonArray([.. done.KickedUsers.Select(u => (JsonNode)u)]),
                ["failed_to_kick_users"] = new JsonArray([.. done.FailedToKickUsers.Select(u => (JsonNode)u)]),
                ["local_aliases"] = new JsonArray([.. done.LocalAliases.Select(a => (JsonNode)a)]),
                ["new_room_id"] = null,
            });
        });
    }

    /// <summary>Map media deletion and server notices.</summary>
    private static void MapMediaAndNotices(RouteGroupBuilder synapse)
    {
        // Local media
        synapse.MapDelete("/v1/media/{serverName}/{mediaId}", async (string serverName, string mediaId, AdminService admin, CancellationToken cancellationToken) =>
        {
            await admin.DeleteMediaAsync(serverName, mediaId, cancellationToken);
            return MatrixHttp.Json(new JsonObject { ["deleted_media"] = new JsonArray(mediaId), ["total"] = 1 });
        });

        // Notices, with or without a transaction ID
        foreach (string pattern in new[] { "/v1/send_server_notice", "/v1/send_server_notice/{txnId}" })
        {
            Delegate send = async (JsonObject body, ServerNotices notices, CancellationToken cancellationToken) =>
            {
                string userId = Text(body, "user_id") ?? throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing user_id.");
                if (body["content"] is not JsonObject content) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Missing content.");
                return MatrixHttp.Json(new JsonObject { ["event_id"] = await notices.SendAsync(userId, (JsonObject)content.DeepClone(), cancellationToken) });
            };
            if (pattern.EndsWith('}')) synapse.MapPut(pattern, send);
            else synapse.MapPost(pattern, send);
        }
    }

    /// <summary>Answer whois: the user's devices, each with its last connection; for admins about anyone local, for others about themselves.</summary>
    private static async Task<IResult> WhoisAsync(string userId, HttpContext http, AdminService admin, CancellationToken cancellationToken)
    {
        // Admins, or oneself
        Requester requester = http.GetRequester();
        if (!requester.Admin && requester.UserId != userId) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, "Only server admins may look up other users.");
        JsonObject devices = [];
        foreach (AdminDevice device in await admin.WhoisAsync(userId, cancellationToken))
        {
            JsonArray connections = [];
            if (device.LastSeen is DateTimeOffset seen) connections.Add(new JsonObject { ["ip"] = device.LastSeenIp, ["last_seen"] = seen.ToUnixTimeMilliseconds(), ["user_agent"] = null });
            devices[device.DeviceId] = new JsonObject { ["sessions"] = new JsonArray(new JsonObject { ["connections"] = connections }), ["display_name"] = device.DisplayName };
        }
        // Return devices
        return MatrixHttp.Json(new JsonObject { ["user_id"] = userId, ["devices"] = devices });
    }

    /// <summary>Return a page of <paramref name="items"/> under <paramref name="key"/>, with <c>total</c> and, when more follow, <c>next_token</c>.</summary>
    private static IResult Page(HttpContext http, string key, IReadOnlyList<JsonNode> items)
    {
        // Offset and size from the query
        (int from, int limit) = Paging(http);
        JsonObject page = new() { [key] = new JsonArray([.. items.Skip(from).Take(limit).Select(i => i.DeepClone())]), ["total"] = items.Count };
        if (from + limit < items.Count) page["next_token"] = (from + limit).ToString();
        // Return page
        return MatrixHttp.Json(page);
    }

    /// <summary>Return <c>from</c> and <c>limit</c> of the query: from 0, 100 items by default.</summary>
    private static (int From, int Limit) Paging(HttpContext http)
    {
        // Assert numbers in range
        IQueryCollection query = http.Request.Query;
        int from = int.TryParse(query["from"], out int f) ? f : 0;
        int limit = int.TryParse(query["limit"], out int l) ? l : 100;
        if (from < 0 || limit < 0) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "from and limit cannot be negative.");
        // Return range
        return (from, Math.Min(limit, MaxLimit));
    }

    /// <summary>Throw <c>M_FORBIDDEN</c> when an admin would <paramref name="action"/> their own account, which would shut them out.</summary>
    private static void AssertNotSelf(HttpContext http, string userId, string action)
    {
        // Other accounts only
        if (http.GetRequester().UserId == userId) throw new MatrixException(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, $"You cannot {action} your own account.");
    }

    /// <summary>Return the user ID when the account exists; throw <c>M_NOT_FOUND</c> otherwise.</summary>
    private static async Task<string> ExistingAsync(AdminService admin, string userId, CancellationToken cancellationToken) => (await admin.GetUserAsync(userId, cancellationToken)).UserId;

    /// <summary>Return a required boolean of the body; throw <c>M_MISSING_PARAM</c> when missing.</summary>
    private static bool Flag(JsonObject body, string key) => body[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, $"Missing boolean {key}.");

    /// <summary>Return a string of the body, or null.</summary>
    private static string? Text(JsonObject body, string key) => body[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>Return a boolean query parameter, or null when not given.</summary>
    private static bool? Bool(IQueryCollection query, string key) => bool.TryParse(query[key], out bool value) ? value : null;

    /// <summary>Return an integer of the body that may be null, and whether it was given.</summary>
    private static Optional<int?> NullableInt(JsonObject body, string key) => !body.ContainsKey(key) ? default : Optional<int?>.Of(body[key] is JsonValue value ? value.GetValue<int>() : null);

    /// <summary>Return a long of the body that may be null, and whether it was given.</summary>
    private static Optional<long?> NullableLong(JsonObject body, string key) => !body.ContainsKey(key) ? default : Optional<long?>.Of(body[key] is JsonValue value ? value.GetValue<long>() : null);

    /// <summary>Return an account as Synapse shows it.</summary>
    private static JsonNode ToJson(AdminUser user) => new JsonObject
    {
        ["name"] = user.UserId,
        ["displayname"] = user.DisplayName,
        ["avatar_url"] = user.AvatarUrl,
        ["admin"] = user.Admin,
        ["deactivated"] = user.Deactivated,
        ["locked"] = user.Locked,
        ["suspended"] = user.Suspended,
        ["shadow_banned"] = false,
        ["is_guest"] = false,
        ["user_type"] = null,
        ["creation_ts"] = user.Created.ToUnixTimeMilliseconds(),
        ["threepids"] = new JsonArray(),
        ["external_ids"] = new JsonArray(),
        ["devices"] = user.Devices,
        ["joined_rooms"] = user.Rooms,
    };

    /// <summary>Return a registration token as Synapse shows it.</summary>
    private static JsonNode ToJson(RegistrationToken token) => new JsonObject { ["token"] = token.Token, ["uses_allowed"] = token.UsesAllowed, ["pending"] = 0, ["completed"] = token.Completed, ["expiry_time"] = token.ExpiryTime };

    /// <summary>Return a report as Synapse shows event reports, with the reported user and whether it is resolved besides.</summary>
    private static JsonNode ToJson(Report report) => new JsonObject
    {
        ["id"] = report.Id,
        ["received_ts"] = report.Received.ToUnixTimeMilliseconds(),
        ["room_id"] = report.RoomId,
        ["event_id"] = report.EventId,
        ["user_id"] = report.Reporter,
        ["target_user_id"] = report.UserId,
        ["reason"] = report.Reason,
        ["score"] = null,
        ["event_summary"] = report.EventSummary,
        ["resolved"] = report.Resolved,
    };

    /// <summary>Return a room as Synapse lists rooms.</summary>
    private static JsonNode ToJson(AdminRoom room) => new JsonObject
    {
        ["room_id"] = room.RoomId,
        ["name"] = room.Name,
        ["canonical_alias"] = room.Alias,
        ["joined_members"] = room.Members,
        ["version"] = room.Version,
        ["creator"] = room.Creator,
        ["encryption"] = room.Encrypted ? "m.megolm.v1.aes-sha2" : null,
        ["federatable"] = true,
        ["public"] = room.Published,
    };

    /// <summary>Return an upload as Synapse lists a user's media.</summary>
    private static JsonNode ToJson(MediaRow row) => new JsonObject
    {
        ["media_id"] = row.MediaId,
        ["media_type"] = row.ContentType,
        ["media_length"] = row.Size,
        ["upload_name"] = row.FileName,
        ["created_ts"] = row.CreatedTs,
        ["quarantined_by"] = null,
        ["safe_from_quarantine"] = false,
    };
}
