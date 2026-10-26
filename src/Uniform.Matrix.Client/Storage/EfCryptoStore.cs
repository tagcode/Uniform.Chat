namespace Uniform.Matrix.Client.Storage;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Crypto;

/// <summary><see cref="ICryptoStore"/> in the client's SQLite database; pickles are encrypted with the key in <c>crypto.key</c>.</summary>
public sealed class EfCryptoStore : ICryptoStore
{
    private readonly IDbContextFactory<ClientDbContext> contexts;

    /// <summary>Create a store over <paramref name="contexts"/>.</summary>
    public EfCryptoStore(IDbContextFactory<ClientDbContext> contexts)
    {
        // Store dependency
        this.contexts = contexts;
    }

    /// <summary>Device the stored account belongs to; sets by <see cref="ResetForDeviceAsync"/>.</summary>
    public string? DeviceId { get; private set; }

    /// <summary>Clear all encryption state when it belongs to another device than <paramref name="deviceId"/>, e.g. after a new login.</summary>
    /// <returns>Whether state was cleared.</returns>
    public async Task<bool> ResetForDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Keep state of the same device
        DeviceId = deviceId;
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoAccountRow? account = await db.CryptoAccount.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (account is null || account.DeviceId == deviceId) return false;

        // Clear everything; sessions of another device are useless
        await db.CryptoAccount.ExecuteDeleteAsync(cancellationToken);
        await db.CryptoOlmSessions.ExecuteDeleteAsync(cancellationToken);
        await db.CryptoInboundGroupSessions.ExecuteDeleteAsync(cancellationToken);
        await db.CryptoOutboundGroupSessions.ExecuteDeleteAsync(cancellationToken);
        await db.CryptoDeviceLists.ExecuteDeleteAsync(cancellationToken);
        await db.CryptoValues.ExecuteDeleteAsync(cancellationToken);
        // Done
        return true;
    }

    /// <summary>Return the account.</summary>
    public async Task<AccountRecord?> LoadAccountAsync(CancellationToken cancellationToken)
    {
        // Read the single row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoAccountRow? row = await db.CryptoAccount.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        // Return record
        return row is null ? null : new AccountRecord { Pickle = row.Pickle, DeviceKeysUploaded = row.DeviceKeysUploaded };
    }

    /// <summary>Save the account.</summary>
    public async Task SaveAccountAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        // Upsert the single row
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoAccountRow? row = await db.CryptoAccount.SingleOrDefaultAsync(cancellationToken);
        if (row is null) db.CryptoAccount.Add(row = new CryptoAccountRow { DeviceId = DeviceId ?? "", Pickle = account.Pickle });
        row.Pickle = account.Pickle;
        row.DeviceKeysUploaded = account.DeviceKeysUploaded;
        row.DeviceId = DeviceId ?? row.DeviceId;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return sessions with a device.</summary>
    public async Task<IReadOnlyList<OlmSessionRecord>> LoadOlmSessionsAsync(string senderKey, CancellationToken cancellationToken)
    {
        // Query by key
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<CryptoOlmSessionRow> rows = await db.CryptoOlmSessions.AsNoTracking().Where(s => s.SenderKey == senderKey).ToListAsync(cancellationToken);
        // Return records
        return [.. rows.Select(r => new OlmSessionRecord { SenderKey = r.SenderKey, SessionId = r.SessionId, Pickle = r.Pickle, LastUsed = new DateTimeOffset(r.LastUsedTicks, TimeSpan.Zero) })];
    }

    /// <summary>Save a session.</summary>
    public async Task SaveOlmSessionAsync(OlmSessionRecord session, CancellationToken cancellationToken)
    {
        // Upsert by key and ID
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoOlmSessionRow? row = await db.CryptoOlmSessions.FindAsync([session.SenderKey, session.SessionId], cancellationToken);
        if (row is null) db.CryptoOlmSessions.Add(row = new CryptoOlmSessionRow { SenderKey = session.SenderKey, SessionId = session.SessionId, Pickle = session.Pickle });
        row.Pickle = session.Pickle;
        row.LastUsedTicks = session.LastUsed.UtcTicks;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return an inbound session.</summary>
    public async Task<InboundGroupSessionRecord?> LoadInboundGroupSessionAsync(string roomId, string sessionId, CancellationToken cancellationToken)
    {
        // Find by room and ID
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoInboundGroupSessionRow? row = await db.CryptoInboundGroupSessions.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId && s.SessionId == sessionId, cancellationToken);
        // Return record
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Convert an inbound session row.</summary>
    private static InboundGroupSessionRecord ToRecord(CryptoInboundGroupSessionRow row) =>
        new() { RoomId = row.RoomId, SessionId = row.SessionId, SenderKey = row.SenderKey, SigningKey = row.SigningKey, Pickle = row.Pickle, BackedUp = row.BackedUp, Imported = row.Imported };

    /// <summary>Save an inbound session.</summary>
    public async Task SaveInboundGroupSessionAsync(InboundGroupSessionRecord session, CancellationToken cancellationToken)
    {
        // Upsert by room and ID
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoInboundGroupSessionRow? row = await db.CryptoInboundGroupSessions.FindAsync([session.RoomId, session.SessionId], cancellationToken);
        if (row is null) db.CryptoInboundGroupSessions.Add(row = new CryptoInboundGroupSessionRow { RoomId = session.RoomId, SessionId = session.SessionId, SenderKey = session.SenderKey, SigningKey = session.SigningKey, Pickle = session.Pickle });
        row.Pickle = session.Pickle;
        row.SenderKey = session.SenderKey;
        row.SigningKey = session.SigningKey;
        row.BackedUp = session.BackedUp;
        row.Imported = session.Imported;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return a room's outbound session.</summary>
    public async Task<OutboundGroupSessionRecord?> LoadOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken)
    {
        // Find by room
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoOutboundGroupSessionRow? row = await db.CryptoOutboundGroupSessions.AsNoTracking().SingleOrDefaultAsync(s => s.RoomId == roomId, cancellationToken);
        // Return record
        return row is null ? null : new OutboundGroupSessionRecord
        {
            RoomId = row.RoomId,
            SessionId = row.SessionId,
            Pickle = row.Pickle,
            CreatedAt = new DateTimeOffset(row.CreatedAtTicks, TimeSpan.Zero),
            MessageCount = row.MessageCount,
            SharedWith = JsonSerializer.Deserialize<HashSet<string>>(row.SharedWithJson) ?? [],
        };
    }

    /// <summary>Save a room's outbound session.</summary>
    public async Task SaveOutboundGroupSessionAsync(OutboundGroupSessionRecord session, CancellationToken cancellationToken)
    {
        // Upsert by room
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoOutboundGroupSessionRow? row = await db.CryptoOutboundGroupSessions.FindAsync([session.RoomId], cancellationToken);
        if (row is null) db.CryptoOutboundGroupSessions.Add(row = new CryptoOutboundGroupSessionRow { RoomId = session.RoomId, SessionId = session.SessionId, Pickle = session.Pickle, SharedWithJson = "[]" });
        row.SessionId = session.SessionId;
        row.Pickle = session.Pickle;
        row.CreatedAtTicks = session.CreatedAt.UtcTicks;
        row.MessageCount = session.MessageCount;
        row.SharedWithJson = JsonSerializer.Serialize(session.SharedWith);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Remove a room's outbound session.</summary>
    public async Task DeleteOutboundGroupSessionAsync(string roomId, CancellationToken cancellationToken)
    {
        // Delete by room
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.CryptoOutboundGroupSessions.Where(s => s.RoomId == roomId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Return all device lists.</summary>
    public async Task<IReadOnlyList<DeviceListRecord>> LoadDeviceListsAsync(CancellationToken cancellationToken)
    {
        // Read all
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<CryptoDeviceListRow> rows = await db.CryptoDeviceLists.AsNoTracking().ToListAsync(cancellationToken);
        // Return records
        return [.. rows.Select(r => new DeviceListRecord
        {
            UserId = r.UserId,
            Devices = JsonSerializer.Deserialize<List<DeviceInfo>>(r.DevicesJson) ?? [],
            Outdated = r.Outdated,
            Identity = r.IdentityJson is null ? null : JsonSerializer.Deserialize<UserIdentity>(r.IdentityJson),
        })];
    }

    /// <summary>Save a device list.</summary>
    public async Task SaveDeviceListAsync(DeviceListRecord deviceList, CancellationToken cancellationToken)
    {
        // Upsert by user
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoDeviceListRow? row = await db.CryptoDeviceLists.FindAsync([deviceList.UserId], cancellationToken);
        if (row is null) db.CryptoDeviceLists.Add(row = new CryptoDeviceListRow { UserId = deviceList.UserId, DevicesJson = "[]" });
        row.DevicesJson = JsonSerializer.Serialize(deviceList.Devices);
        row.Outdated = deviceList.Outdated;
        row.IdentityJson = deviceList.Identity is null ? null : JsonSerializer.Serialize(deviceList.Identity);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Return sessions not backed up.</summary>
    public async Task<IReadOnlyList<InboundGroupSessionRecord>> LoadSessionsToBackUpAsync(int limit, CancellationToken cancellationToken)
    {
        // Query not backed up
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        List<CryptoInboundGroupSessionRow> rows = await db.CryptoInboundGroupSessions.AsNoTracking().Where(s => !s.BackedUp).OrderBy(s => s.RoomId).ThenBy(s => s.SessionId).Take(limit).ToListAsync(cancellationToken);
        // Return records
        return [.. rows.Select(ToRecord)];
    }

    /// <summary>Mark all sessions not backed up.</summary>
    public async Task ResetBackedUpAsync(CancellationToken cancellationToken)
    {
        // Bulk update
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.CryptoInboundGroupSessions.ExecuteUpdateAsync(s => s.SetProperty(r => r.BackedUp, false), cancellationToken);
    }

    /// <summary>Return a value.</summary>
    public async Task<string?> LoadValueAsync(string name, CancellationToken cancellationToken)
    {
        // Find by name
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        CryptoValueRow? row = await db.CryptoValues.AsNoTracking().SingleOrDefaultAsync(v => v.Name == name, cancellationToken);
        // Return value
        return row?.Value;
    }

    /// <summary>Set or remove a value.</summary>
    public async Task SaveValueAsync(string name, string? value, CancellationToken cancellationToken)
    {
        // Remove
        await using ClientDbContext db = await contexts.CreateDbContextAsync(cancellationToken);
        if (value is null)
        {
            await db.CryptoValues.Where(v => v.Name == name).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        // Upsert
        CryptoValueRow? row = await db.CryptoValues.FindAsync([name], cancellationToken);
        if (row is null) db.CryptoValues.Add(row = new CryptoValueRow { Name = name, Value = value });
        row.Value = value;
        await db.SaveChangesAsync(cancellationToken);
    }
}
