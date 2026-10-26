namespace Uniform.Matrix.Server.Tests;

using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>Tests of <see cref="AdminService"/>, which the admin screens of the server use.</summary>
public class AdminTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The admin creates an account while registration is closed; the user logs in, and the list shows them.</summary>
    [Fact]
    public async Task CreateUser()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();
        AdminService admin = server.Services.GetRequiredService<AdminService>();

        // Act
        string userId = await admin.CreateUserAsync("alice", "secret", admin: true, Ct);
        MatrixClient alice = server.CreateClient();
        await alice.LoginWithPasswordAsync("alice", "secret", cancellationToken: Ct);
        IReadOnlyList<AdminUser> users = await admin.GetUsersAsync(Ct);

        // Assert
        Assert.Equal("@alice:test.local", userId);
        AdminUser listed = Assert.Single(users);
        Assert.True(listed.Admin);
        Assert.Equal(1, listed.Devices);
        Assert.Equal("alice", listed.DisplayName);
        MatrixException taken = await Assert.ThrowsAsync<MatrixException>(() => admin.CreateUserAsync("alice", "other", admin: false, Ct));
        Assert.Equal(ErrorCodes.UserInUse, taken.Errcode);
    }

    /// <summary>A reset password replaces the old one and logs out every device; deactivation blocks login until reactivated.</summary>
    [Fact]
    public async Task ResetPasswordAndDeactivate()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();
        AdminService admin = server.Services.GetRequiredService<AdminService>();
        string userId = await admin.CreateUserAsync("bob", "old", admin: false, Ct);
        MatrixClient bob = server.CreateClient();
        await bob.LoginWithPasswordAsync(userId, "old", cancellationToken: Ct);

        // Act and assert: reset
        await admin.ResetPasswordAsync(userId, "new", Ct);
        MatrixException loggedOut = await Assert.ThrowsAsync<MatrixException>(() => bob.WhoAmIAsync(Ct));
        Assert.Equal(ErrorCodes.UnknownToken, loggedOut.Errcode);
        await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync(userId, "old", cancellationToken: Ct));
        await server.CreateClient().LoginWithPasswordAsync(userId, "new", cancellationToken: Ct);

        // Act and assert: deactivate, reactivate
        await admin.SetDeactivatedAsync(userId, true, Ct);
        Assert.Equal(0, (await admin.GetUsersAsync(Ct)).Single().Devices);
        await Assert.ThrowsAsync<MatrixException>(() => server.CreateClient().LoginWithPasswordAsync(userId, "new", cancellationToken: Ct));
        await admin.SetDeactivatedAsync(userId, false, Ct);
        await server.CreateClient().LoginWithPasswordAsync(userId, "new", cancellationToken: Ct);
    }

    /// <summary>Rooms are listed with name, alias, members and encryption; publishing puts a room in the public directory.</summary>
    [Fact]
    public async Task ListAndPublishRooms()
    {
        // Arrange
        await using TestHomeserver server = await TestHomeserver.StartAsync();
        AdminService admin = server.Services.GetRequiredService<AdminService>();
        await admin.CreateUserAsync("carol", "secret", admin: false, Ct);
        MatrixClient carol = server.CreateClient();
        await carol.LoginWithPasswordAsync("carol", "secret", cancellationToken: Ct);
        InitialStateEvent encryption = new() { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } };
        string roomId = await carol.CreateRoomAsync(new CreateRoomRequest { Name = "Studio", RoomAliasName = "studio", InitialState = [encryption] }, Ct);

        // Act
        AdminRoom before = Assert.Single(await admin.GetRoomsAsync(Ct));
        await admin.SetPublishedAsync(roomId, true, Ct);
        PublicRoomsResponse directory = await carol.GetPublicRoomsAsync(cancellationToken: Ct);

        // Assert
        Assert.Equal("Studio", before.Name);
        Assert.Equal("#studio:test.local", before.Alias);
        Assert.Equal(1, before.Members);
        Assert.True(before.Encrypted);
        Assert.False(before.Published);
        Assert.Equal([roomId], directory.Chunk.Select(r => r.RoomId));
        Assert.True((await admin.GetRoomsAsync(Ct)).Single().Published);
    }
}
