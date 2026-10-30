namespace Uniform.Matrix.Client.Avalonia.Core.Tests;

using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Headless;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.VisualTree;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using global::Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;
using Uniform.Matrix.Client.Avalonia.Core.Views;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>The app's view models against our homeserver in memory: sign in, rooms, messages both ways, resume and sign out.</summary>
public class ChatFlowTests
{
    /// <summary>How long to wait for a condition; generous, as CI runs the test assemblies in parallel on few cores.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    /// <summary>Wait on the UI thread until <paramref name="condition"/> holds; fail with <paramref name="what"/> after a while.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        // Poll, letting the dispatcher run in between
        DateTime end = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    /// <summary>Render the app in a window and check that it draws; save the frame to <c>UNIFORM_MATRIX_SCREENSHOTS</c> when set.</summary>
    private static async Task CaptureAsync(MainViewModel model, string fileName, double width = 1000, double height = 680)
    {
        // Show, let layout and rendering run, then capture
        Window window = new() { Width = width, Height = height, Content = new MainView { DataContext = model } };
        window.Show();
        await Task.Delay(300);
        Bitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("UNIFORM_MATRIX_SCREENSHOTS") is string folder) frame.Save(Path.Combine(folder, fileName), new PngBitmapEncoderOptions());
        // Done
        window.Close();
    }

    /// <summary>Sign in through the form and return the chat page.</summary>
    private static async Task<ChatViewModel> SignInAsync(MainViewModel model, string userId, string password = "password")
    {
        // Fill the form and send it
        model.Login.UserId = userId;
        model.Login.Password = password;
        model.Login.Homeserver = LocalServer.Address;
        await model.Login.SignInCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => model.Page is ChatViewModel, "the chat page");
        // Return chat
        return (ChatViewModel)model.Page;
    }

    /// <summary>A signed-in user sees the room with its history, receives a message and sends a formatted one; the screen renders.</summary>
    [AvaloniaFact]
    public async Task ChatInRoom()
    {
        // Arrange: Bob's room with history, which Alice joins
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Topic = "Food talk", Preset = RoomPresets.PublicChat });
        await bob.SendTextAsync(roomId, "welcome to the kitchen");
        await using (Stream logo = AssetLoader.Open(new Uri("avares://Uniform.Matrix.Client.Avalonia.Core/Assets/logo.png")))
        {
            MxcUri avatar = await bob.UploadMediaAsync(logo, "image/png", "logo.png");
            await bob.SendStateEventAsync(roomId, EventTypes.RoomAvatar, new RoomAvatarContent { Url = avatar.ToString() });
            await bob.SetAvatarUrlAsync(avatar.ToString());
        }
        await alice.JoinRoomAsync(roomId);
        string data = Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName;
        MainViewModel model = new(server.Platform(data), new AvaloniaUiDispatcher());

        try
        {
            // Act: sign in; the first room opens on wide screens
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Rooms.Any(r => r.Name == "Kitchen"), "the room in the list");
            await WaitUntilAsync(() => chat.SelectedRoom?.RoomId == roomId && chat.Messages.Items.Any(m => m.Text == "welcome to the kitchen"), "the room's history");

            // Act: Bob writes, Alice answers with Markdown
            await bob.SendHtmlAsync(roomId, "lunch at noon? bring `forks`", "lunch at <strong>noon</strong>? bring <code>forks</code>");
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Html?.Contains("<strong>noon</strong>") == true), "Bob's new message");
            chat.Composer = "sure, **noon** works";
            await chat.SendCommand.ExecuteAsync(null);

            // Assert: the server has it formatted, and it shows as sent
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.IsOwn && m.Text == "sure, *noon* works") && !chat.Messages.Items.Any(m => m.IsPending), "Alice's message to be sent");
            MessagesResponse history = await bob.GetMessagesAsync(roomId, limit: 10);
            ClientEvent sent = history.Chunk.First(e => e.Type == EventTypes.RoomMessage && e.Sender == alice.UserId);
            Assert.True(sent.TryGetContent(out MessageContent? content));
            Assert.Contains("<strong>noon</strong>", content.FormattedBody);
            Assert.Equal("Food talk", chat.SelectedRoom!.Topic);
            await WaitUntilAsync(() => chat.SelectedRoom.HasAvatar, "the room's avatar");
            await WaitUntilAsync(() => chat.SelectedRoom.LastMessage == "You: sure, *noon* works", "Alice's message as the room's latest");
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.SenderId == bob.UserId && m.Text.StartsWith("lunch") && m.HasSenderAvatar), "Bob's picture beside his message");
            Assert.NotEmpty(chat.SelectedRoom.LastTime);

            // Assert: the screen draws, in both themes
            await CaptureAsync(model, "chat.png");
            await CaptureAsync(model, "narrow-room.png", 400, 720);
            chat.BackCommand.Execute(null);
            await CaptureAsync(model, "narrow-list.png", 400, 720);
            Window narrow = new() { Width = 400, Height = 720, Content = new MainView { DataContext = model } };
            narrow.Show();
            await Task.Delay(100);
            Assert.Equal(400, narrow.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RoomPanel").Bounds.Width, 1);
            narrow.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            try
            {
                await CaptureAsync(model, "chat-dark.png");
            }
            finally
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Default;
            }
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>The room list shows a message's time today, its weekday within a week, and its date before that.</summary>
    [Fact]
    public void FormatListTimes()
    {
        // Arrange
        DateTimeOffset now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

        // Act and assert
        Assert.Equal("13:00", RoomItemViewModel.FormatTime(now.AddHours(-2), now));
        Assert.Equal(now.AddDays(-3).ToString("ddd"), RoomItemViewModel.FormatTime(now.AddDays(-3), now));
        Assert.Equal(now.AddDays(-30).ToString("d"), RoomItemViewModel.FormatTime(now.AddDays(-30), now));
    }

    /// <summary>Replies quote their message, reactions are counted, edits replace the text and deletions leave a marker.</summary>
    [AvaloniaFact]
    public async Task ReplyReactEditDelete()
    {
        // Arrange: Bob's message in a room with Alice
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Talk", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        await bob.SendTextAsync(roomId, "hello alice");
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == "hello alice"), "Bob's message");
            MessageViewModel hello = chat.Messages.Items.First(m => m.Text == "hello alice");

            // Act: reply and react
            chat.StartReply(hello);
            Assert.StartsWith("Replying to bob", chat.ComposeMode);
            chat.Composer = "hi bob";
            await chat.SendCommand.ExecuteAsync(null);
            await chat.ReactAsync(hello, "👍");

            // Assert: the reply quotes Bob, the reaction is counted
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == "hi bob" && m.EventId is not null), "the reply");
            Assert.Equal("bob: hello alice", chat.Messages.Items.First(m => m.Text == "hi bob" && m.EventId is not null).ReplyQuote);
            await WaitUntilAsync(() => hello.Reactions == "👍 1", "the reaction");

            // Act: edit own message, keeping its Markdown
            chat.Composer = "see you **soon**";
            await chat.SendCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Source == "see you **soon**" && m.EventId is not null), "the message to edit");
            MessageViewModel own = chat.Messages.Items.First(m => m.Source == "see you **soon**" && m.EventId is not null);
            chat.StartEdit(own);
            Assert.Equal("see you **soon**", chat.Composer);
            chat.Composer = "see you **tomorrow**";
            await chat.SendCommand.ExecuteAsync(null);

            // Assert: the item shows the new text
            await WaitUntilAsync(() => own.IsEdited && own.Source == "see you **tomorrow**", "the edit");
            Assert.Contains("<strong>tomorrow</strong>", own.Html);

            // Act and assert: delete it
            await chat.DeleteAsync(own);
            await WaitUntilAsync(() => own.Text == "Message deleted", "the deletion");
            Assert.False(own.CanEdit);
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>A new account is asked to set up recovery; one device sets it up after the password prompt; a second device of the same account is asked to verify itself, is verified by comparing emoji, and recovers with the key.</summary>
    [AvaloniaFact]
    public async Task SetUpRecoveryAndVerify()
    {
        // Arrange: Grace signed in on two devices
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient grace = await server.RegisterAsync("grace");
        MainViewModel first = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        MainViewModel second = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            // Act: the new account is asked to set up recovery; set it up on the first device, confirming with the password
            ChatViewModel firstChat = await SignInAsync(first, grace.UserId!);
            await WaitUntilAsync(() => firstChat.HasSecuritySuggestion, "the suggestion to set up recovery");
            Assert.StartsWith("Set up recovery", firstChat.SecuritySuggestion);
            await firstChat.OpenSecurityCommand.ExecuteAsync(null);
            SecurityViewModel firstSecurity = firstChat.Security!;
            Assert.True(firstSecurity.CanSetUp, string.Join("; ", firstSecurity.Lines.Select(l => $"{l.Label}: {l.Value}")) + firstSecurity.Message);
            await firstSecurity.SetUpCommand.ExecuteAsync(null);
            if (firstSecurity.NeedsPassword)
            {
                firstSecurity.Password = "password";
                await firstSecurity.SetUpCommand.ExecuteAsync(null);
            }
            Assert.True(firstSecurity.HasNewRecoveryKey, firstSecurity.Message);
            string recoveryKey = firstSecurity.NewRecoveryKey;
            Assert.Contains(firstSecurity.Lines, l => l.Label == "This device" && l.Value == "Verified");
            await CaptureAsync(first, "security.png");
            firstChat.CloseSecurityCommand.Execute(null);
            await WaitUntilAsync(() => !firstChat.HasSecuritySuggestion, "the suggestion to go away");

            // Act: the second device is asked to verify itself, and asks; the first accepts, both compare
            ChatViewModel secondChat = await SignInAsync(second, grace.UserId!);
            await WaitUntilAsync(() => secondChat.HasSecuritySuggestion, "the suggestion to verify the second device");
            Assert.StartsWith("Verify this device", secondChat.SecuritySuggestion);
            await secondChat.OpenSecurityCommand.ExecuteAsync(null);
            Assert.True(secondChat.Security!.CanVerify);
            await secondChat.Security.VerifyCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Verification is { IsIncoming: true }, "the request on the first device");
            await firstChat.Verification!.AcceptCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Verification is { IsComparing: true } && secondChat.Verification is { IsComparing: true }, "the emoji on both devices");
            Assert.Equal(firstChat.Verification!.Emoji.Select(e => e.Symbol), secondChat.Verification!.Emoji.Select(e => e.Symbol));
            await CaptureAsync(second, "verification.png");
            await firstChat.Verification.MatchCommand.ExecuteAsync(null);
            await secondChat.Verification.MatchCommand.ExecuteAsync(null);

            // Assert: both finish verified
            await WaitUntilAsync(() => firstChat.Verification is { IsFinished: true } && secondChat.Verification is { IsFinished: true }, "the verification to finish");
            Assert.EndsWith("is verified.", secondChat.Verification.Message);
            await WaitUntilAsync(() => !secondChat.HasSecuritySuggestion, "the second device's suggestion to go away");
            await firstSecurity.RefreshAsync();
            string secondDevice = secondChat.Security.Lines.Single(l => l.Label == "Device").Value;
            Assert.Equal(3, firstSecurity.Devices.Count);
            Assert.True(firstSecurity.Devices[0].IsThisDevice);
            Assert.StartsWith("Verified", firstSecurity.Devices.Single(d => d.DeviceId == secondDevice).Details);

            // Act and assert: the recovery key also unlocks the backup on the second device
            await secondChat.Security.RefreshAsync();
            secondChat.Security.RecoveryInput = recoveryKey;
            await secondChat.Security.RecoverCommand.ExecuteAsync(null);
            Assert.Contains("message keys restored", secondChat.Security.Message);

            // Act and assert: the first device signs out the second with the password
            firstSecurity.StartSignOut(firstSecurity.Devices.Single(d => d.DeviceId == secondDevice));
            firstSecurity.Password = "password";
            await firstSecurity.ConfirmSignOutCommand.ExecuteAsync(null);
            Assert.Equal(2, firstSecurity.Devices.Count);
            Assert.DoesNotContain(await grace.GetDevicesAsync(), d => d.DeviceId == secondDevice);
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
        }
    }

    /// <summary>A second device sees older encrypted messages it has no keys for; once verified, the first device forwards the keys, and the open room and the room list show the messages without a restart.</summary>
    [AvaloniaFact]
    public async Task ShowMessagesWhenKeysArrive()
    {
        // Arrange: Heidi with recovery set up, and an encrypted room with a message from her first device
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient heidi = await server.RegisterAsync("heidi");
        MainViewModel first = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        MainViewModel second = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel firstChat = await SignInAsync(first, heidi.UserId!);
            await WaitUntilAsync(() => firstChat.HasSecuritySuggestion, "the suggestion to set up recovery");
            await firstChat.OpenSecurityCommand.ExecuteAsync(null);
            await firstChat.Security!.SetUpCommand.ExecuteAsync(null);
            if (firstChat.Security.NeedsPassword)
            {
                firstChat.Security.Password = "password";
                await firstChat.Security.SetUpCommand.ExecuteAsync(null);
            }
            Assert.True(firstChat.Security.HasNewRecoveryKey, firstChat.Security.Message);
            firstChat.CloseSecurityCommand.Execute(null);
            firstChat.JoinTarget = "Diary";
            await firstChat.OpenTargetCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.SelectedRoom?.Name == "Diary", "the new room to open");
            string roomId = firstChat.SelectedRoom!.RoomId;
            firstChat.Composer = "dear diary";
            await firstChat.SendCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Messages.Items.Any(m => m.Text == "dear diary") && !firstChat.Messages.Items.Any(m => m.IsPending), "the message to be sent");

            // Act: the second device opens the room, which it cannot read yet
            ChatViewModel secondChat = await SignInAsync(second, heidi.UserId!);
            await WaitUntilAsync(() => secondChat.Rooms.Any(r => r.RoomId == roomId), "the room on the second device");
            secondChat.SelectedRoom = secondChat.Rooms.First(r => r.RoomId == roomId);
            await WaitUntilAsync(() => secondChat.Messages.Items.Any(m => m.Text.StartsWith("Encrypted message")), "the undecryptable message");
            Assert.Equal("You: Encrypted message", secondChat.SelectedRoom!.LastMessage);

            // Act: verify the second device from the first
            await WaitUntilAsync(() => secondChat.HasSecuritySuggestion, "the suggestion to verify the second device");
            await secondChat.OpenSecurityCommand.ExecuteAsync(null);
            await secondChat.Security!.VerifyCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Verification is { IsIncoming: true }, "the request on the first device");
            await firstChat.Verification!.AcceptCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Verification is { IsComparing: true } && secondChat.Verification is { IsComparing: true }, "the emoji on both devices");
            await firstChat.Verification.MatchCommand.ExecuteAsync(null);
            await secondChat.Verification!.MatchCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => firstChat.Verification is { IsFinished: true } && secondChat.Verification is { IsFinished: true }, "the verification to finish");

            // Assert: the open room and its list entry show the message
            await WaitUntilAsync(() => secondChat.Messages.Items.Any(m => m.Text == "dear diary"), "the decrypted message in the open room");
            Assert.DoesNotContain(secondChat.Messages.Items, m => m.Text.StartsWith("Encrypted message"));
            await WaitUntilAsync(() => secondChat.Rooms.First(r => r.RoomId == roomId).LastMessage == "You: dear diary", "the decrypted message in the room list");
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
        }
    }

    /// <summary>The sign-in form creates an account after the server's policies are accepted, then signs in; the account is deleted from the security panel with the password, which returns to the form and ends the account.</summary>
    [AvaloniaFact]
    public async Task CreateAndDeleteAccount()
    {
        // Arrange: a server with terms
        await using LocalServer server = await LocalServer.StartAsync(new Dictionary<string, string?> { ["Server:TermsUrl"] = "https://example.org/terms" });
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            // Act: create an account; the first attempt shows the policies
            model.Login.SwitchModeCommand.Execute(null);
            Assert.Equal("Create account", model.Login.ActionText);
            model.Login.UserId = $"@judy:{LocalServer.ServerName}";
            model.Login.Password = "password";
            model.Login.Homeserver = LocalServer.Address;
            await model.Login.SignInCommand.ExecuteAsync(null);
            Assert.True(model.Login.HasPolicies);
            Assert.Equal("https://example.org/terms", model.Login.Policies.Single().Url);
            Assert.StartsWith("Read and accept", model.Login.ErrorMessage);
            await CaptureAsync(model, "create-account.png", 400, 760);
            model.Login.AcceptTerms = true;
            await model.Login.SignInCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => model.Page is ChatViewModel, "the chat page of the new account");
            ChatViewModel chat = (ChatViewModel)model.Page;

            // Act: delete it, a wrong password first
            await chat.OpenSecurityCommand.ExecuteAsync(null);
            SecurityViewModel security = chat.Security!;
            security.StartDeleteAccountCommand.Execute(null);
            security.Password = "wrong";
            await security.ConfirmDeleteAccountCommand.ExecuteAsync(null);
            Assert.Equal("The password was not accepted.", security.Message);
            security.Password = "password";
            await security.ConfirmDeleteAccountCommand.ExecuteAsync(null);

            // Assert: back on the form, and the account cannot sign in
            await WaitUntilAsync(() => model.Page is LoginViewModel, "the sign-in form");
            MatrixClient again = new(new HttpClient(server.Platform("").HttpHandler!()), new Uri(LocalServer.Address));
            await Assert.ThrowsAsync<MatrixException>(() => again.LoginWithPasswordAsync($"@judy:{LocalServer.ServerName}", "password"));
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Another member's message can be reported with a reason, which the server keeps for its admin; blocking the sender hides their messages, and unblocking from the security panel shows them again.</summary>
    [AvaloniaFact]
    public async Task ReportAndBlock()
    {
        // Arrange: Mallory writes in Kim's room
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient kim = await server.RegisterAsync("kim");
        MatrixClient mallory = await server.RegisterAsync("mallory");
        string roomId = await kim.CreateRoomAsync(new CreateRoomRequest { Name = "Garden", Preset = RoomPresets.PublicChat });
        await mallory.JoinRoomAsync(roomId);
        await mallory.SendTextAsync(roomId, "buy cheap pills");
        await kim.SendTextAsync(roomId, "welcome");
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel chat = await SignInAsync(model, kim.UserId!);
            await WaitUntilAsync(() => chat.SelectedRoom?.RoomId == roomId && chat.Messages.Items.Any(m => m.Text == "buy cheap pills"), "Mallory's message");
            MessageViewModel spam = chat.Messages.Items.Single(m => m.Text == "buy cheap pills");

            // Act and assert: report it
            await chat.ReportMessageAsync(spam, "Spam");
            IReadOnlyList<Uniform.Matrix.Server.Rooms.Report> reports = await server.Services.GetRequiredService<Uniform.Matrix.Server.Rooms.ReportService>().GetAsync(includeResolved: false, CancellationToken.None);
            Assert.Contains(reports, r => r.EventId == spam.EventId && r.Reporter == kim.UserId && r.Reason == "Spam");
            await WaitUntilAsync(() => chat.Notice.StartsWith("Reported"), "the notice of the report");

            // Act and assert: block Mallory; her message goes, Kim's stays
            await chat.SetBlockedAsync(mallory.UserId!, blocked: true);
            await WaitUntilAsync(() => !chat.Messages.Items.Any(m => m.Text == "buy cheap pills") && chat.Messages.Items.Any(m => m.Text == "welcome"), "Mallory's message to be hidden");
            chat.LoadMembersCommand.Execute(null);
            Assert.True(chat.Members.Single(m => m.UserId == mallory.UserId).IsBlocked);
            Assert.Contains(mallory.UserId!, await kim.GetIgnoredUsersAsync());

            // Act and assert: unblock from the security panel
            await chat.OpenSecurityCommand.ExecuteAsync(null);
            Assert.Equal([mallory.UserId!], chat.Security!.BlockedUsers);
            await chat.Security.UnblockAsync(mallory.UserId!);
            Assert.Empty(chat.Security.BlockedUsers);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.Text == "buy cheap pills"), "Mallory's message back");
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Records notifications instead of showing them.</summary>
    private sealed class RecordingNotifier : INotifier
    {
        /// <summary>Titles and bodies shown so far.</summary>
        public List<(string Title, string Body)> Shown { get; } = [];

        /// <summary>Record the notification.</summary>
        public Task ShowAsync(string title, string body, CancellationToken cancellationToken = default)
        {
            // Under a lock, as tests read from another thread
            lock (Shown) Shown.Add((title, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>In the background, direct messages, mentions and invites notify; other messages in groups do not, and nothing does while the window has the focus on that room.</summary>
    [AvaloniaFact]
    public async Task NotifyInBackground()
    {
        // Arrange: Alice in a group with Bob, and the app in the background
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string group = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Team", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(group);
        RecordingNotifier notifier = new();
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName) with { Notifier = notifier }, new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.Rooms.Any(r => r.RoomId == group), "the group");
            chat.IsActive = false;

            // Act: chatter, a mention, and a direct chat
            await bob.SendTextAsync(group, "just chatting");
            await bob.SendHtmlAsync(group, "alice: look", $"<a href=\"https://matrix.to/#/{alice.UserId}\">alice</a>: look", new Mentions { UserIds = [alice.UserId!] });
            string direct = await bob.CreateDirectRoomAsync(alice.UserId!);
            await WaitUntilAsync(() => { lock (notifier.Shown) return notifier.Shown.Any(n => n.Title == "Invitation"); }, "the invite notification");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == direct);
            await chat.AcceptInviteCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.SelectedRoom is { IsInvite: false, IsDirect: true }, "the joined direct chat");
            chat.SelectedRoom = chat.Rooms.First(r => r.RoomId == group);
            await bob.SendTextAsync(direct, "psst");

            // Assert
            await WaitUntilAsync(() => { lock (notifier.Shown) return notifier.Shown.Any(n => n.Body == "psst"); }, "the direct message notification");
            lock (notifier.Shown)
            {
                Assert.Contains(notifier.Shown, n => n.Title == "bob in Team" && n.Body == "alice: look");
                Assert.DoesNotContain(notifier.Shown, n => n.Body == "just chatting");
                Assert.Equal("bob", notifier.Shown.Single(n => n.Body == "psst").Title);
            }
            await WaitUntilAsync(() => chat.UnreadRooms > 0 && chat.Rooms.Any(r => r.RoomId == direct && r.HasUnread), "the unread badge of the direct chat");
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>Two people in a room verify each other from the member list by comparing emoji.</summary>
    [AvaloniaFact]
    public async Task VerifyOtherUser()
    {
        // Arrange: Heidi and Ivan both in the app, in one room
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient heidi = await server.RegisterAsync("heidi");
        MatrixClient ivan = await server.RegisterAsync("ivan");
        string roomId = await heidi.CreateRoomAsync(new CreateRoomRequest { Name = "Pair", Preset = RoomPresets.PublicChat });
        await ivan.JoinRoomAsync(roomId);
        MainViewModel first = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        MainViewModel second = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            ChatViewModel heidiChat = await SignInAsync(first, heidi.UserId!);
            ChatViewModel ivanChat = await SignInAsync(second, ivan.UserId!);
            await WaitUntilAsync(() => heidiChat.SelectedRoom?.RoomId == roomId && ivanChat.SelectedRoom?.RoomId == roomId, "the room on both");

            // Act: Heidi asks from the member list; Ivan accepts, both compare
            await WaitUntilAsync(() => { heidiChat.LoadMembersCommand.Execute(null); return heidiChat.Members.Any(m => m.UserId == ivan.UserId && m.CanVerify); }, "Ivan in the member list");
            await heidiChat.VerifyMemberAsync(heidiChat.Members.First(m => m.UserId == ivan.UserId));
            await WaitUntilAsync(() => ivanChat.Verification is { IsIncoming: true }, "the request at Ivan");
            await ivanChat.Verification!.AcceptCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => heidiChat.Verification is { IsComparing: true } && ivanChat.Verification is { IsComparing: true }, "the emoji on both");
            Assert.Equal(heidiChat.Verification!.Emoji.Select(e => e.Symbol), ivanChat.Verification!.Emoji.Select(e => e.Symbol));
            await heidiChat.Verification.MatchCommand.ExecuteAsync(null);
            await ivanChat.Verification.MatchCommand.ExecuteAsync(null);

            // Assert
            await WaitUntilAsync(() => heidiChat.Verification is { IsFinished: true } && ivanChat.Verification is { IsFinished: true }, "the verification to finish");
            Assert.EndsWith("is verified.", heidiChat.Verification.Message);
            Assert.StartsWith("@ivan", heidiChat.Verification.Message);
            heidiChat.LoadMembersCommand.Execute(null);
            MemberItem verified = heidiChat.Members.First(m => m.UserId == ivan.UserId);
            Assert.True(verified.IsVerified);
            Assert.False(verified.ShowVerify);
            Assert.EndsWith("verified", verified.Details);
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
        }
    }

    /// <summary>A file sent from the app reaches the room as a file message, and its attachment downloads with the same bytes.</summary>
    [AvaloniaFact]
    public async Task SendAndSaveFile()
    {
        // Arrange: Alice in Bob's room
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Files", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        byte[] bytes = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];

        try
        {
            // Act: send a file to the open room
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.SelectedRoom?.RoomId == roomId, "the room to open");
            await chat.SendFileAsync(new MemoryStream(bytes), "notes.txt");
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.IsAttachment && m.FileName == "notes.txt"), "the file message");

            // Assert: Bob sees a file message, and the download matches
            MessagesResponse history = await bob.GetMessagesAsync(roomId, limit: 10);
            ClientEvent sent = history.Chunk.First(e => e.Type == EventTypes.RoomMessage && e.Sender == alice.UserId);
            Assert.True(sent.TryGetContent(out MessageContent? content));
            Assert.Equal(MessageTypes.File, content.MessageType);
            MessageViewModel message = chat.Messages.Items.First(m => m.IsAttachment);
            string? path = await chat.DownloadAsync(message);
            Assert.NotNull(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            string? named = await chat.DownloadForOpeningAsync(message);
            Assert.Equal("notes.txt", Path.GetFileName(named));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(named!));
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>The room box creates a room and starts a direct chat; both open, and the invited user gets the invites.</summary>
    [AvaloniaFact]
    public async Task CreateRoomAndChat()
    {
        // Arrange
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient erin = await server.RegisterAsync("erin");
        MatrixClient frank = await server.RegisterAsync("frank");
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            // Act: create a room, invite Frank from its menu
            ChatViewModel chat = await SignInAsync(model, erin.UserId!);
            chat.JoinTarget = "Book club";
            Assert.Equal("Create", chat.OpenTargetText);
            await chat.OpenTargetCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.SelectedRoom?.Name == "Book club", "the new room to open");
            chat.InviteTarget = frank.UserId!;
            await chat.InviteCommand.ExecuteAsync(null);

            // Act: start a direct chat with Frank
            chat.JoinTarget = frank.UserId!;
            Assert.Equal("Chat", chat.OpenTargetText);
            await chat.OpenTargetCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => chat.SelectedRoom is { IsDirect: true }, "the direct chat to open");
            chat.LoadMembersCommand.Execute(null);

            // Assert: Frank is invited to both, and listed as invited
            SyncResponse sync = await frank.SyncAsync(timeout: TimeSpan.Zero);
            Assert.Equal(2, sync.Rooms?.Invite?.Count);
            Assert.Contains(chat.Members, m => m.UserId == frank.UserId && m.IsInvited);

            // Assert: links to Frank open the chat; links to unknown rooms wait in the room box
            string directId = chat.SelectedRoom!.RoomId;
            chat.SelectedRoom = chat.Rooms.First(r => r.Name == "Book club");
            Assert.True(chat.OpenMatrixLink($"https://matrix.to/#/{Uri.EscapeDataString(frank.UserId!)}"));
            Assert.Equal(directId, chat.SelectedRoom?.RoomId);
            Assert.True(chat.OpenMatrixLink("https://matrix.to/#/%23elsewhere%3Atest.local?via=test.local"));
            Assert.Equal("#elsewhere:test.local", chat.JoinTarget);
            Assert.False(chat.OpenMatrixLink("https://example.org/"));
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>The room directory lists and searches public rooms, joins one that then opens, and renders; a room ID link with servers keeps them in the room box.</summary>
    [AvaloniaFact]
    public async Task BrowseDirectoryAndJoin()
    {
        // Arrange: two public rooms of Grace's
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient grace = await server.RegisterAsync("grace");
        MatrixClient heidi = await server.RegisterAsync("heidi");
        await grace.CreateRoomAsync(new CreateRoomRequest { Name = "Garden", Topic = "Plants", Preset = RoomPresets.PublicChat, Visibility = "public", RoomAliasName = "garden" });
        await grace.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen", Preset = RoomPresets.PublicChat, Visibility = "public" });
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        try
        {
            // Act: list, search, join
            ChatViewModel chat = await SignInAsync(model, heidi.UserId!);
            await chat.OpenDirectoryCommand.ExecuteAsync(null);
            DirectoryViewModel directory = chat.RoomDirectory!;
            List<string> listed = [.. directory.Rooms.Select(r => r.Title).Order()];
            directory.SearchTerm = "plants";
            await directory.SearchCommand.ExecuteAsync(null);
            List<string> found = [.. directory.Rooms.Select(r => r.Title)];
            await CaptureAsync(model, "directory.png");
            await directory.JoinCommand.ExecuteAsync(directory.Rooms[0]);
            await WaitUntilAsync(() => chat.SelectedRoom?.Name == "Garden", "the joined room to open");

            // Act: back closes the directory, then nothing on a wide screen
            await chat.OpenDirectoryCommand.ExecuteAsync(null);
            bool closedDirectory = chat.GoBack();
            bool closedMore = chat.GoBack();

            // Assert
            Assert.True(closedDirectory);
            Assert.False(closedMore);
            Assert.Equal(["Garden", "Kitchen"], listed);
            Assert.Equal(["Garden"], found);
            Assert.Null(chat.RoomDirectory);
            Assert.True(chat.OpenMatrixLink("matrix:roomid/abc:elsewhere.test?via=elsewhere.test"));
            Assert.Equal("!abc:elsewhere.test elsewhere.test", chat.JoinTarget);
        }
        finally
        {
            await model.StopAsync();
        }
    }

    /// <summary>In the window, Enter in the message box sends and Shift+Enter starts a new line.</summary>
    [AvaloniaFact]
    public async Task SendWithEnter()
    {
        // Arrange: the chat in a window, with the message box focused
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient bob = await server.RegisterAsync("bob");
        MatrixClient alice = await server.RegisterAsync("alice");
        string roomId = await bob.CreateRoomAsync(new CreateRoomRequest { Name = "Keys", Preset = RoomPresets.PublicChat });
        await alice.JoinRoomAsync(roomId);
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());
        Window window = new() { Width = 1000, Height = 680, Content = new MainView { DataContext = model } };

        try
        {
            ChatViewModel chat = await SignInAsync(model, alice.UserId!);
            await WaitUntilAsync(() => chat.SelectedRoom?.RoomId == roomId, "the room to open");
            window.Show();
            await Task.Delay(100);
            TextBox composer = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ComposerBox");
            composer.Focus();

            // Act: two lines, then Enter
            window.KeyTextInput("first");
            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
            window.KeyTextInput("second");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            // Assert: one message with both lines, and an empty box
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.EventId is not null && m.Source.Replace("\r", "") == "first\nsecond"), "the sent message");
            Assert.Equal("", chat.Composer);

            // Act and assert: right-click on the own message offers its actions
            static bool IsSent(ListBoxItem item) => item.DataContext is MessageViewModel { IsOwn: true, Kind: MessageKind.Message, EventId: not null };
            await WaitUntilAsync(() => window.GetVisualDescendants().OfType<ListBoxItem>().Any(IsSent), "the message on screen");
            ListBoxItem row = window.GetVisualDescendants().OfType<ListBoxItem>().Last(IsSent);
            Point center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Right);
            window.MouseUp(center, MouseButton.Right);
            ChatView view = window.GetVisualDescendants().OfType<ChatView>().Single();
            Assert.NotNull(view.MessageMenu);
            Assert.Equal(["Reply", "React", "Edit", "Copy text", "Delete"], view.MessageMenu.Items.OfType<MenuItem>().Select(i => i.Header as string));
            view.MessageMenu.Hide();

            // Act and assert: pasting text types it; pasting an image sends it
            await window.Clipboard!.SetTextAsync("pasted");
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
            await WaitUntilAsync(() => chat.Composer == "pasted", "the pasted text");
            await window.Clipboard!.SetBitmapAsync(new Bitmap(AssetLoader.Open(new Uri("avares://Uniform.Matrix.Client.Avalonia.Core/Assets/logo.png"))));
            window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
            await WaitUntilAsync(() => chat.Messages.Items.Any(m => m.IsAttachment && m.FileName?.EndsWith(".png") == true && m.EventId is not null), "the pasted image");
        }
        finally
        {
            window.Close();
            await model.StopAsync();
        }
    }

    /// <summary>The next start resumes the remembered account without the password; signing out forgets it and logs out the device.</summary>
    [AvaloniaFact]
    public async Task ResumeAndSignOut()
    {
        // Arrange: a first sign-in
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient carol = await server.RegisterAsync("carol");
        string data = Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName;
        GuiPlatform platform = server.Platform(data);
        MainViewModel first = new(platform, new AvaloniaUiDispatcher());
        await SignInAsync(first, carol.UserId!);
        await first.StopAsync();

        // Act: a new start resumes
        MainViewModel second = new(platform, new AvaloniaUiDispatcher());
        await second.StartAsync();
        await WaitUntilAsync(() => second.Page is ChatViewModel, "the resumed chat");
        int devicesBefore = (await carol.GetDevicesAsync()).Count;
        await ((ChatViewModel)second.Page).SignOutCommand.ExecuteAsync(null);

        // Assert: back on the form, the device is gone, and the next start stays on the form
        Assert.IsType<LoginViewModel>(second.Page);
        Assert.Equal(devicesBefore - 1, (await carol.GetDevicesAsync()).Count);
        MainViewModel third = new(platform, new AvaloniaUiDispatcher());
        await third.StartAsync();
        Assert.IsType<LoginViewModel>(third.Page);
    }

    /// <summary>A start without network resumes the stored session and keeps trying to connect, instead of asking to sign in again.</summary>
    [AvaloniaFact]
    public async Task ResumeOffline()
    {
        // Arrange: a first sign-in
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient dave = await server.RegisterAsync("dave");
        string data = Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName;
        GuiPlatform platform = server.Platform(data);
        MainViewModel first = new(platform, new AvaloniaUiDispatcher());
        await SignInAsync(first, dave.UserId!);
        await first.StopAsync();

        // Act: start again with every request failing as without network
        GuiPlatform offline = platform with { ConfigureServices = services => services.AddHttpClient(ClientSession.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new OfflineHandler()) };
        MainViewModel second = new(offline, new AvaloniaUiDispatcher());
        try
        {
            await second.StartAsync();

            // Assert: the chat, which keeps trying to connect
            await WaitUntilAsync(() => second.Page is ChatViewModel chat && chat.StatusText.Contains("retrying"), "the chat, retrying");
        }
        finally
        {
            await second.StopAsync();
        }
    }

    /// <summary>Fails every request as a phone without network does.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        /// <summary>Fail.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("No network");
    }

    /// <summary>A wrong password keeps the form open with the server's reason.</summary>
    [AvaloniaFact]
    public async Task RejectWrongPassword()
    {
        // Arrange
        await using LocalServer server = await LocalServer.StartAsync();
        MatrixClient dave = await server.RegisterAsync("dave");
        MainViewModel model = new(server.Platform(Directory.CreateTempSubdirectory("uniform-matrix-gui-data-").FullName), new AvaloniaUiDispatcher());

        // Act
        model.Login.UserId = dave.UserId!;
        model.Login.Password = "wrong";
        model.Login.Homeserver = LocalServer.Address;
        await model.Login.SignInCommand.ExecuteAsync(null);

        // Assert
        Assert.IsType<LoginViewModel>(model.Page);
        Assert.Contains("password", model.Login.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await model.StopAsync();
    }
}
