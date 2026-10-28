namespace Uniform.Matrix.Server.Storage;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>The homeserver's database; SQLite now, PostgreSQL later through the same model.</summary>
public sealed class ServerDbContext : DbContext
{
    /// <summary>Create a context with <paramref name="options"/>.</summary>
    public ServerDbContext(DbContextOptions<ServerDbContext> options) : base(options)
    {
    }

    /// <summary>Local accounts.</summary>
    public DbSet<UserRow> Users => Set<UserRow>();
    /// <summary>Devices of local users.</summary>
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    /// <summary>Access tokens by hash.</summary>
    public DbSet<AccessTokenRow> AccessTokens => Set<AccessTokenRow>();
    /// <summary>Profiles of local users.</summary>
    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();
    /// <summary>Rooms.</summary>
    public DbSet<RoomRow> Rooms => Set<RoomRow>();
    /// <summary>Events of all rooms.</summary>
    public DbSet<EventRow> Events => Set<EventRow>();
    /// <summary>Edges of the room graphs.</summary>
    public DbSet<EventEdgeRow> EventEdges => Set<EventEdgeRow>();
    /// <summary>Relations between events.</summary>
    public DbSet<EventRelationRow> EventRelations => Set<EventRelationRow>();
    /// <summary>Latest events of each room.</summary>
    public DbSet<ForwardExtremityRow> ForwardExtremities => Set<ForwardExtremityRow>();
    /// <summary>State snapshots.</summary>
    public DbSet<StateGroupRow> StateGroups => Set<StateGroupRow>();
    /// <summary>Entries of state snapshots.</summary>
    public DbSet<StateGroupEntryRow> StateGroupEntries => Set<StateGroupEntryRow>();
    /// <summary>Current state of each room.</summary>
    public DbSet<CurrentStateRow> CurrentState => Set<CurrentStateRow>();
    /// <summary>Client transaction IDs.</summary>
    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();
    /// <summary>Local room aliases.</summary>
    public DbSet<RoomAliasRow> RoomAliases => Set<RoomAliasRow>();
    /// <summary>Rooms users have forgotten.</summary>
    public DbSet<ForgottenRoomRow> ForgottenRooms => Set<ForgottenRoomRow>();
    /// <summary>Stored sync filters.</summary>
    public DbSet<FilterRow> Filters => Set<FilterRow>();
    /// <summary>Global and per-room account data.</summary>
    public DbSet<AccountDataRow> AccountData => Set<AccountDataRow>();
    /// <summary>Latest receipts.</summary>
    public DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();
    /// <summary>Server counters.</summary>
    public DbSet<CounterRow> Counters => Set<CounterRow>();
    /// <summary>Uploaded media.</summary>
    public DbSet<MediaRow> Media => Set<MediaRow>();
    /// <summary>Generated thumbnails.</summary>
    public DbSet<ThumbnailRow> Thumbnails => Set<ThumbnailRow>();
    /// <summary>Device keys.</summary>
    public DbSet<DeviceKeysRow> DeviceKeys => Set<DeviceKeysRow>();
    /// <summary>Unclaimed one-time keys.</summary>
    public DbSet<OneTimeKeyRow> OneTimeKeys => Set<OneTimeKeyRow>();
    /// <summary>Fallback keys.</summary>
    public DbSet<FallbackKeyRow> FallbackKeys => Set<FallbackKeyRow>();
    /// <summary>Cross-signing keys.</summary>
    public DbSet<CrossSigningKeyRow> CrossSigningKeys => Set<CrossSigningKeyRow>();
    /// <summary>Signatures uploaded for keys.</summary>
    public DbSet<KeySignatureRow> KeySignatures => Set<KeySignatureRow>();
    /// <summary>Latest device list change per user.</summary>
    public DbSet<DeviceListChangeRow> DeviceListChanges => Set<DeviceListChangeRow>();
    /// <summary>Undelivered to-device messages.</summary>
    public DbSet<ToDeviceRow> ToDevice => Set<ToDeviceRow>();
    /// <summary>Key backup versions.</summary>
    public DbSet<KeyBackupVersionRow> KeyBackupVersions => Set<KeyBackupVersionRow>();
    /// <summary>Backed-up room keys.</summary>
    public DbSet<RoomKeyRow> RoomKeys => Set<RoomKeyRow>();
    /// <summary>Pushers of users.</summary>
    public DbSet<PusherRow> Pushers => Set<PusherRow>();
    /// <summary>Media of other servers kept here.</summary>
    public DbSet<RemoteMediaRow> RemoteMedia => Set<RemoteMediaRow>();
    /// <summary>Signed key answers of other servers.</summary>
    public DbSet<ServerKeyResponseRow> ServerKeyResponses => Set<ServerKeyResponseRow>();
    /// <summary>How far the federation sender got.</summary>
    public DbSet<FederationProgressRow> FederationProgress => Set<FederationProgressRow>();
    /// <summary>PDUs and EDUs waiting for other servers.</summary>
    public DbSet<FederationOutboxRow> FederationOutbox => Set<FederationOutboxRow>();
    /// <summary>Invites from other servers to rooms not here yet.</summary>
    public DbSet<RemoteInviteRow> RemoteInvites => Set<RemoteInviteRow>();
    /// <summary>Reports of content, rooms and users.</summary>
    public DbSet<ReportRow> Reports => Set<ReportRow>();

    /// <summary>Keys and indexes.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Accounts
        modelBuilder.Entity<UserRow>().HasKey(u => u.UserId);
        modelBuilder.Entity<DeviceRow>().HasKey(d => new { d.UserId, d.DeviceId });
        modelBuilder.Entity<AccessTokenRow>().HasKey(t => t.TokenHash);
        modelBuilder.Entity<AccessTokenRow>().HasIndex(t => new { t.UserId, t.DeviceId });
        modelBuilder.Entity<PusherRow>().HasKey(p => new { p.UserId, p.AppId, p.PushKey });
        modelBuilder.Entity<PusherRow>().HasIndex(p => new { p.AppId, p.PushKey });
        modelBuilder.Entity<ProfileRow>().HasKey(p => p.UserId);

        // Rooms and the event graph
        modelBuilder.Entity<RoomRow>().HasKey(r => r.RoomId);
        modelBuilder.Entity<EventRow>().HasKey(e => e.EventId);
        modelBuilder.Entity<EventRow>().HasIndex(e => e.Stream).IsUnique();
        modelBuilder.Entity<EventRow>().HasIndex(e => new { e.RoomId, e.Stream });
        modelBuilder.Entity<EventRow>().HasQueryFilter(e => !e.SoftFailed);
        modelBuilder.Entity<EventRelationRow>().HasKey(r => r.EventId);
        modelBuilder.Entity<EventRelationRow>().HasIndex(r => new { r.RelatesTo, r.RelType });
        modelBuilder.Entity<EventEdgeRow>().HasKey(e => new { e.EventId, e.PrevEventId });
        modelBuilder.Entity<ForwardExtremityRow>().HasKey(e => new { e.RoomId, e.EventId });

        // State
        modelBuilder.Entity<StateGroupRow>().HasKey(g => g.Id);
        modelBuilder.Entity<StateGroupEntryRow>().HasKey(e => new { e.GroupId, e.Type, e.StateKey });
        modelBuilder.Entity<CurrentStateRow>().HasKey(s => new { s.RoomId, s.Type, s.StateKey });
        modelBuilder.Entity<CurrentStateRow>().HasIndex(s => new { s.Type, s.StateKey, s.Membership });

        // Client bookkeeping
        modelBuilder.Entity<FederationOutboxRow>().HasIndex(o => new { o.Destination, o.Id });
        modelBuilder.Entity<FederationProgressRow>().Property(p => p.Id).ValueGeneratedNever();
        modelBuilder.Entity<RemoteMediaRow>().HasKey(m => new { m.ServerName, m.MediaId });
        modelBuilder.Entity<ServerKeyResponseRow>().HasKey(k => k.ServerName);
        modelBuilder.Entity<RemoteInviteRow>().HasKey(i => new { i.UserId, i.RoomId });
        modelBuilder.Entity<RemoteInviteRow>().HasIndex(i => new { i.UserId, i.Stream });
        modelBuilder.Entity<TransactionRow>().HasKey(t => new { t.UserId, t.DeviceId, t.TransactionId, t.EventId });
        modelBuilder.Entity<RoomAliasRow>().HasKey(a => a.Alias);
        modelBuilder.Entity<RoomAliasRow>().HasIndex(a => a.RoomId);
        modelBuilder.Entity<ForgottenRoomRow>().HasKey(f => new { f.UserId, f.RoomId });
        modelBuilder.Entity<FilterRow>().HasKey(f => new { f.UserId, f.FilterId });
        modelBuilder.Entity<AccountDataRow>().HasKey(a => new { a.UserId, a.RoomId, a.Type });
        modelBuilder.Entity<AccountDataRow>().HasIndex(a => new { a.UserId, a.Stream });

        // Ephemeral data and counters
        modelBuilder.Entity<ReceiptRow>().HasKey(r => new { r.RoomId, r.UserId, r.Type, r.ThreadId });
        modelBuilder.Entity<ReceiptRow>().HasIndex(r => new { r.RoomId, r.Stream });
        modelBuilder.Entity<CounterRow>().HasKey(c => c.Name);

        // Media
        modelBuilder.Entity<MediaRow>().HasKey(m => m.MediaId);
        modelBuilder.Entity<MediaRow>().HasIndex(m => m.UserId);
        modelBuilder.Entity<ThumbnailRow>().HasKey(t => new { t.MediaId, t.Width, t.Height, t.Method });

        // Encryption keys and messages
        modelBuilder.Entity<DeviceKeysRow>().HasKey(k => new { k.UserId, k.DeviceId });
        modelBuilder.Entity<OneTimeKeyRow>().HasKey(k => new { k.UserId, k.DeviceId, k.KeyId });
        modelBuilder.Entity<OneTimeKeyRow>().HasIndex(k => new { k.UserId, k.DeviceId, k.Algorithm });
        modelBuilder.Entity<FallbackKeyRow>().HasKey(k => new { k.UserId, k.DeviceId, k.Algorithm });
        modelBuilder.Entity<CrossSigningKeyRow>().HasKey(k => new { k.UserId, k.Usage });
        modelBuilder.Entity<KeySignatureRow>().HasKey(s => new { s.TargetUserId, s.TargetKeyId, s.SignerUserId, s.SignerKeyId });
        modelBuilder.Entity<DeviceListChangeRow>().HasKey(c => c.UserId);
        modelBuilder.Entity<DeviceListChangeRow>().HasIndex(c => c.Stream);
        modelBuilder.Entity<ToDeviceRow>().HasKey(m => m.Id);
        modelBuilder.Entity<ToDeviceRow>().HasIndex(m => new { m.UserId, m.DeviceId, m.Stream });
        modelBuilder.Entity<KeyBackupVersionRow>().HasKey(v => new { v.UserId, v.Version });
        modelBuilder.Entity<RoomKeyRow>().HasKey(k => new { k.UserId, k.Version, k.RoomId, k.SessionId });
    }
}

/// <summary>Context for <c>dotnet ef</c> at design time, on a throwaway SQLite file.</summary>
public sealed class DesignTimeServerDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    /// <summary>Create a context for migrations.</summary>
    public ServerDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite("Data Source=design.db").Options);
}
