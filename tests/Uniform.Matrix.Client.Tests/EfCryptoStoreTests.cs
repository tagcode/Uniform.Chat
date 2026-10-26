namespace Uniform.Matrix.Client.Tests;

using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>Tests of <see cref="EfCryptoStore"/> on a migrated SQLite database.</summary>
public class EfCryptoStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Named values are stored, replaced and removed.</summary>
    [Fact]
    public async Task CanStoreValues()
    {
        // Arrange
        await using TestDatabase db = new();
        await db.CreateStoreAsync();
        EfCryptoStore store = new(db);

        // Act and assert
        Assert.Null(await store.LoadValueAsync("backup_version", Ct));
        await store.SaveValueAsync("backup_version", "1", Ct);
        await store.SaveValueAsync("backup_version", "2", Ct);
        Assert.Equal("2", await store.LoadValueAsync("backup_version", Ct));
        await store.SaveValueAsync("backup_version", null, Ct);
        Assert.Null(await store.LoadValueAsync("backup_version", Ct));
    }

    /// <summary>Backup and imports flags of inbound sessions persist, and a reset marks all for upload again.</summary>
    [Fact]
    public async Task CanTrackBackedUpSessions()
    {
        // Arrange
        await using TestDatabase db = new();
        await db.CreateStoreAsync();
        EfCryptoStore store = new(db);
        InboundGroupSessionRecord session = new() { RoomId = "!r:hs", SessionId = "s1", SenderKey = "c", SigningKey = "e", Pickle = "p" };
        await store.SaveInboundGroupSessionAsync(session, Ct);
        await store.SaveInboundGroupSessionAsync(session with { SessionId = "s2", BackedUp = true, Imported = true }, Ct);

        // Act and assert
        Assert.Equal(["s1"], (await store.LoadSessionsToBackUpAsync(10, Ct)).Select(s => s.SessionId));
        Assert.True((await store.LoadInboundGroupSessionAsync("!r:hs", "s2", Ct))!.Imported);
        await store.ResetBackedUpAsync(Ct);
        Assert.Equal(2, (await store.LoadSessionsToBackUpAsync(10, Ct)).Count);
    }

    /// <summary>Device lists keep cross-signing identities and device trust.</summary>
    [Fact]
    public async Task CanStoreIdentity()
    {
        // Arrange
        await using TestDatabase db = new();
        await db.CreateStoreAsync();
        EfCryptoStore store = new(db);
        DeviceInfo device = new() { UserId = "@a:hs", DeviceId = "D", Curve25519Key = "c", Ed25519Key = "e", SignedBySelfSigningKey = "ssk", Verified = true };
        UserIdentity identity = new() { UserId = "@a:hs", MasterKey = "msk", SelfSigningKey = "ssk", Trusted = true };

        // Act
        await store.SaveDeviceListAsync(new DeviceListRecord { UserId = "@a:hs", Devices = [device], Identity = identity }, Ct);
        DeviceListRecord loaded = (await store.LoadDeviceListsAsync(Ct)).Single();

        // Assert
        Assert.Equal(identity, loaded.Identity);
        Assert.Equal(device, loaded.Devices.Single());
    }
}
