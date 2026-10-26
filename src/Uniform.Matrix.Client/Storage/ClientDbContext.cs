namespace Uniform.Matrix.Client.Storage;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>EF Core context of the local SQLite database.</summary>
public sealed class ClientDbContext : DbContext
{
    /// <summary>Create a context with <paramref name="options"/>.</summary>
    public ClientDbContext(DbContextOptions<ClientDbContext> options) : base(options)
    {
    }

    /// <summary>Session and syncs position.</summary>
    public DbSet<StoredAccount> Accounts => Set<StoredAccount>();
    /// <summary>Received room events.</summary>
    public DbSet<StoredEvent> Events => Set<StoredEvent>();
    /// <summary>Locally written events awaiting delivery.</summary>
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    /// <summary>Downloaded media.</summary>
    public DbSet<StoredMedia> Media => Set<StoredMedia>();
    /// <summary>Encryption account of this device.</summary>
    public DbSet<CryptoAccountRow> CryptoAccount => Set<CryptoAccountRow>();
    /// <summary>Olm sessions.</summary>
    public DbSet<CryptoOlmSessionRow> CryptoOlmSessions => Set<CryptoOlmSessionRow>();
    /// <summary>Megolm sessions for decrypting.</summary>
    public DbSet<CryptoInboundGroupSessionRow> CryptoInboundGroupSessions => Set<CryptoInboundGroupSessionRow>();
    /// <summary>Megolm sessions for sending.</summary>
    public DbSet<CryptoOutboundGroupSessionRow> CryptoOutboundGroupSessions => Set<CryptoOutboundGroupSessionRow>();
    /// <summary>Device lists of tracked users.</summary>
    public DbSet<CryptoDeviceListRow> CryptoDeviceLists => Set<CryptoDeviceListRow>();
    /// <summary>Named encryption values.</summary>
    public DbSet<CryptoValueRow> CryptoValues => Set<CryptoValueRow>();

    /// <summary>Configure keys, indexes and conversions.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Keys
        modelBuilder.Entity<StoredAccount>().HasKey(a => a.UserId);
        modelBuilder.Entity<CryptoAccountRow>().HasKey(a => a.Id);
        modelBuilder.Entity<CryptoOlmSessionRow>().HasKey(s => new { s.SenderKey, s.SessionId });
        modelBuilder.Entity<CryptoInboundGroupSessionRow>().HasKey(s => new { s.RoomId, s.SessionId });
        modelBuilder.Entity<CryptoOutboundGroupSessionRow>().HasKey(s => s.RoomId);
        modelBuilder.Entity<CryptoDeviceListRow>().HasKey(d => d.UserId);
        modelBuilder.Entity<CryptoValueRow>().HasKey(v => v.Name);
        modelBuilder.Entity<StoredEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<StoredMedia>().HasKey(m => m.MxcUri);

        // History is read per room in time order
        modelBuilder.Entity<StoredEvent>().HasIndex(e => new { e.RoomId, e.OriginServerTs });

        // Outbox lookups by state and by echoed transaction ID
        modelBuilder.Entity<OutboxMessage>().HasIndex(m => m.State);
        modelBuilder.Entity<OutboxMessage>().HasIndex(m => m.TransactionId).IsUnique();
        modelBuilder.Entity<OutboxMessage>().Property(m => m.State).HasConversion<string>();

        // SQLite cannot order by DateTimeOffset; store as ticks
        modelBuilder.Entity<StoredAccount>().Property(a => a.UpdatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        modelBuilder.Entity<OutboxMessage>().Property(m => m.CreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        modelBuilder.Entity<OutboxMessage>().Property(m => m.SentAt).HasConversion(v => v.HasValue ? v.Value.UtcTicks : (long?)null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<StoredMedia>().Property(m => m.DownloadedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    }
}

/// <summary>Creates a context for <c>dotnet ef</c> without starting the application.</summary>
public sealed class DesignTimeClientDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    /// <summary>Create a context on a throwaway database; migrations only need the model.</summary>
    public ClientDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
