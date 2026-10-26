namespace Uniform.Matrix.Server.Encryption;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;
using Uniform.Matrix.Server.Configuration;
using Uniform.Matrix.Server.Storage;
using Uniform.Matrix.Server.Sync;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#send-to-device-messaging">To-device messages</see> between local devices, and the <c>device_lists</c> of sync.</summary>
/// <remarks>A message waits until a sync from a token after it proves its device received it, so a lost response does not lose the message.</remarks>
public sealed class ToDeviceService
{
    private const string TransactionPrefix = "sendToDevice:";
    private readonly IDbContextFactory<ServerDbContext> contexts;
    private readonly ServerStream stream;
    private readonly IOptions<ServerOptions> options;

    /// <summary>Create the service.</summary>
    public ToDeviceService(IDbContextFactory<ServerDbContext> contexts, ServerStream stream, IOptions<ServerOptions> options)
    {
        // Store dependencies
        this.contexts = contexts;
        this.stream = stream;
        this.options = options;
    }

    /// <summary>Queue messages by user ID, then device ID or <c>*</c> for all devices; a retried transaction is not sent twice.</summary>
    public async Task SendAsync(Requester requester, string type, string transactionId, JsonObject messages, CancellationToken cancellationToken)
    {
        // Retried transaction
        string transaction = TransactionPrefix + transactionId;
        await using (ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Transactions.AnyAsync(t => t.UserId == requester.UserId && t.DeviceId == requester.DeviceId && t.TransactionId == transaction, cancellationToken)) return;
        }

        // All messages at one position, with the transaction
        await stream.WriteAsync(async position =>
        {
            await using ServerDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
            foreach ((string userId, JsonNode? devicesNode) in messages)
            {
                if (!userId.EndsWith(":" + options.Value.ServerName, StringComparison.Ordinal)) continue;
                List<string> userDevices = await db.Devices.AsNoTracking().Where(d => d.UserId == userId).Select(d => d.DeviceId).ToListAsync(cancellationToken);
                foreach ((string deviceId, JsonNode? content) in devicesNode as JsonObject ?? [])
                {
                    if (content is not JsonObject) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Message content must be an object.");
                    foreach (string target in deviceId == "*" ? userDevices : userDevices.Where(d => d == deviceId))
                    {
                        db.ToDevice.Add(new ToDeviceRow { UserId = userId, DeviceId = target, Sender = requester.UserId, Type = type, Json = content.ToJsonString(), Stream = position });
                    }
                }
            }
            db.Transactions.Add(new TransactionRow { UserId = requester.UserId, DeviceId = requester.DeviceId, TransactionId = transaction, EventId = "" });
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Return the device's messages up to <paramref name="until"/>, first deleting those delivered before <paramref name="since"/>.</summary>
    public static async Task<IReadOnlyList<JsonObject>> TakeAsync(ServerDbContext dbContext, string userId, string deviceId, long? since, long until, CancellationToken cancellationToken)
    {
        // A sync from a token proves the earlier messages arrived
        if (since is long delivered) await dbContext.ToDevice.Where(m => m.UserId == userId && m.DeviceId == deviceId && m.Stream <= delivered).ExecuteDeleteAsync(cancellationToken);
        List<ToDeviceRow> rows = await dbContext.ToDevice.AsNoTracking().Where(m => m.UserId == userId && m.DeviceId == deviceId && m.Stream <= until).OrderBy(m => m.Id).ToListAsync(cancellationToken);
        // Return events
        return [.. rows.Select(r => new JsonObject { ["type"] = r.Type, ["sender"] = r.Sender, ["content"] = JsonNode.Parse(r.Json) })];
    }

    /// <summary>Return the users whose keys a client should fetch again (<c>changed</c>) or may forget (<c>left</c>) after <paramref name="since"/>.</summary>
    /// <remarks>
    /// Changed: users sharing a room whose keys changed, users who joined a shared room, members of rooms the user joined, and the user.
    /// Left: users who left a shared room or whose room the user left, and who share no room any more.
    /// </remarks>
    public static async Task<(IReadOnlyList<string> Changed, IReadOnlyList<string> Left)> GetDeviceListsAsync(ServerDbContext dbContext, string userId, long since, long until, CancellationToken cancellationToken)
    {
        // Rooms now and the users in them
        List<string> rooms = await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
        HashSet<string> sharing = [.. await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && rooms.Contains(s.RoomId)).Select(s => s.StateKey).Distinct().ToListAsync(cancellationToken), userId];

        // Key changes of those users
        HashSet<string> changed = [.. await dbContext.DeviceListChanges.AsNoTracking().Where(c => c.Stream > since && c.Stream <= until && sharing.Contains(c.UserId)).Select(c => c.UserId).ToListAsync(cancellationToken)];

        // Membership changes in the range, of rooms the user is or was in
        List<string> touched = await dbContext.Events.AsNoTracking().Where(e => e.Type == EventTypes.RoomMember && e.StateKey == userId && e.Stream > since && e.Stream <= until).Select(e => e.RoomId).Distinct().ToListAsync(cancellationToken);
        List<string> relevant = [.. rooms.Union(touched)];
        List<CurrentStateRow> rangeMembers = await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && relevant.Contains(s.RoomId) && s.Stream > since && s.Stream <= until).ToListAsync(cancellationToken);
        HashSet<string> left = [];
        foreach (CurrentStateRow member in rangeMembers)
        {
            if (member.StateKey == userId) continue;
            if (member.Membership == Memberships.Join && rooms.Contains(member.RoomId)) changed.Add(member.StateKey);
            else if (!sharing.Contains(member.StateKey)) left.Add(member.StateKey);
        }

        // Everyone in rooms the user joined, or no longer shares after the user left
        foreach (string roomId in touched)
        {
            List<string> members = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join && s.StateKey != userId).Select(s => s.StateKey).ToListAsync(cancellationToken);
            foreach (string member in members)
            {
                if (rooms.Contains(roomId)) changed.Add(member);
                else if (!sharing.Contains(member)) left.Add(member);
            }
        }
        // Return lists
        return ([.. changed.Order(StringComparer.Ordinal)], [.. left.Except(changed).Order(StringComparer.Ordinal)]);
    }
}
