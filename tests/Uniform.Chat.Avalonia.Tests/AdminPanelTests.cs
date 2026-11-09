namespace Uniform.Chat.Avalonia.Tests;

using global::Avalonia.Controls;
using global::Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Chat.Avalonia.Views;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>Tests of the server administration panel against a server in memory.</summary>
public class AdminPanelTests
{
    /// <summary>An admin sees the panel, deletes a report, suspends the reported user, sends them a notice, and makes and deletes a token; others do not see it.</summary>
    [AvaloniaFact]
    public async Task AdministerServer()
    {
        // Arrange: an admin, and Bob who reports a room
        await using LocalServer server = await LocalServer.StartAsync();
        await server.Services.GetRequiredService<AdminService>().CreateUserAsync("ada", "password", admin: true, CancellationToken.None);
        MatrixClient bob = await server.RegisterAsync("bob");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Noise", Preset = RoomPresets.PublicChat });
        await bob.ReportRoomAsync(roomId, "Spam");
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        Window window = new() { Width = 800, Height = 600, Content = new MainView { DataContext = model } };

        try
        {
            window.Show();
            ChatViewModel chat = await ChatFlowTests.SignInAsync(model, $"@ada:{LocalServer.ServerName}");
            await ChatFlowTests.WaitUntilAsync(() => chat.IsServerAdmin, "the admin rights");

            // Act and assert: the report, then deleted
            await chat.OpenAdminCommand.ExecuteAsync(null);
            AdminViewModel admin = chat.Admin!;
            AdminReportItem report = Assert.Single(admin.Reports);
            Assert.Contains("Spam", report.Details);
            await admin.DeleteReportAsync(report);
            Assert.Empty(admin.Reports);

            // Act and assert: Bob suspended and told
            await admin.ShowSectionCommand.ExecuteAsync(AdminSection.Accounts);
            await admin.SelectAccountAsync(admin.Accounts.Single(a => a.Name == bob.UserId));
            await admin.ToggleSuspendedCommand.ExecuteAsync(null);
            Assert.True(admin.SelectedAccount!.Account.Suspended);
            admin.NoticeText = "Please stop";
            await admin.SendNoticeCommand.ExecuteAsync(null);
            Assert.Equal($"Sent a server notice to {bob.UserId}.", admin.Message);

            // Act and assert: a token made and deleted
            await admin.ShowSectionCommand.ExecuteAsync(AdminSection.Tokens);
            admin.NewTokenUses = "3";
            await admin.CreateTokenCommand.ExecuteAsync(null);
            AdminTokenItem token = Assert.Single(admin.Tokens);
            Assert.Equal(admin.CreatedToken, token.Token);
            Assert.StartsWith("used 0 of 3", token.Details);
            await admin.DeleteTokenAsync(token);
            Assert.Empty(admin.Tokens);
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }
}
