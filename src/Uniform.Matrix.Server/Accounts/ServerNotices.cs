namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#server-notices">Server notices</see>: messages from the server to a local user, in a room of their own tagged <c>m.server_notice</c>, where only the notices account may write.</summary>
public sealed class ServerNotices
{
    /// <summary>Account data type of the notices account, mapping each user to their notices room.</summary>
    private const string RoomsType = "rip.uniform.server_notice_rooms";
    /// <summary>Device the notices account sends from; it never logs in.</summary>
    private const string DeviceId = "SERVER";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly AccountService accounts;
    private readonly AccountDataService accountData;
    private readonly RoomService rooms;
    private readonly IOptionsMonitor<ServerOptions> options;
    private readonly ILogger<ServerNotices> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Create the service.</summary>
    public ServerNotices(IDbContextFactory<ServerDbContext> contexts, AccountService accounts, AccountDataService accountData, RoomService rooms, IOptionsMonitor<ServerOptions> options, ILogger<ServerNotices> logger)
    {
        // Store dependencies
        this.contexts = contexts;
        this.accounts = accounts;
        this.accountData = accountData;
        this.rooms = rooms;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>User ID of the notices account.</summary>
    public string SenderId => $"@{options.CurrentValue.ServerNoticesLocalpart}:{accounts.ServerName}";

    /// <summary>Send a notice to a local user, in their notices room, made and tagged for them on the first notice; return the event ID.</summary>
    /// <param name="userId">Local user who gets the notice.</param>
    /// <param name="content">Message content; <c>msgtype</c> is <c>m.text</c> when not given.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> SendAsync(string userId, JsonObject content, CancellationToken cancellationToken)
    {
        // Assert an active local user and a body
        await AssertRecipientAsync(userId, cancellationToken);
        if (content["body"] is not JsonValue) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "The notice needs a body.");
        if (content["msgtype"] is null) content["msgtype"] = MessageTypes.Text;

        // One at a time, so a user gets one room
        await gate.WaitAsync(cancellationToken);
        try
        {
            string roomId = await GetRoomAsync(userId, cancellationToken);
            string eventId = await rooms.SendMessageAsync(new Requester(SenderId, DeviceId), roomId, EventTypes.RoomMessage, content, Guid.NewGuid().ToString("N"), cancellationToken);
            logger.LogInformation("Admin sent a server notice to {UserId}", userId);
            // Return event
            return eventId;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Return the user's notices room: the one kept for them while they are in it, else a new one they are invited to.</summary>
    private async Task<string> GetRoomAsync(string userId, CancellationToken cancellationToken)
    {
        // The kept room, while the user is joined or invited
        await EnsureSenderAsync(cancellationToken);
        JsonObject kept = await accountData.TryGetAsync(SenderId, null, RoomsType, cancellationToken) ?? [];
        if (kept[userId] is JsonValue value && value.TryGetValue(out string? existing) && await rooms.GetMembershipAsync(existing, userId, cancellationToken) is Memberships.Join or Memberships.Invite) return existing;

        // A new room where only the notices account writes
        JsonObject body = new()
        {
            ["preset"] = "private_chat",
            ["name"] = "Server notices",
            ["topic"] = $"Messages from the admins of {accounts.ServerName}",
            ["invite"] = new JsonArray(userId),
            ["power_level_content_override"] = new JsonObject { ["events_default"] = 100, ["invite"] = 100, ["state_default"] = 100 },
        };
        string roomId = await rooms.CreateRoomAsync(new Requester(SenderId, DeviceId), body, cancellationToken);
        await accountData.SetTagAsync(userId, roomId, "m.server_notice", [], cancellationToken);
        kept[userId] = roomId;
        await accountData.SetAsync(SenderId, null, RoomsType, kept, cancellationToken);
        // Return room
        return roomId;
    }

    /// <summary>Create the notices account when it does not exist; throw when the localpart belongs to an account that logs in.</summary>
    private async Task EnsureSenderAsync(CancellationToken cancellationToken)
    {
        // An existing account without a password is the notices account
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        UserRow? row = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == SenderId, cancellationToken);
        if (row is not null && row.PasswordHash is not null) throw new MatrixException(HttpStatusCode.Conflict, ErrorCodes.UserInUse, $"{SenderId} is a normal account; choose another Server:ServerNoticesLocalpart.");
        if (row is not null) return;
        await accounts.RegisterAsync(options.CurrentValue.ServerNoticesLocalpart, password: null, deviceId: null, deviceName: null, inhibitLogin: true, admin: false, cancellationToken);
        logger.LogInformation("Created the server notices account {UserId}", SenderId);
    }

    /// <summary>Throw <c>M_NOT_FOUND</c> unless <paramref name="userId"/> is an active local account other than the notices account.</summary>
    private async Task AssertRecipientAsync(string userId, CancellationToken cancellationToken)
    {
        // Active local user
        await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (userId == SenderId || !await db.Users.AnyAsync(u => u.UserId == userId && !u.Deactivated, cancellationToken)) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"Unknown local user {userId}.");
    }
}
