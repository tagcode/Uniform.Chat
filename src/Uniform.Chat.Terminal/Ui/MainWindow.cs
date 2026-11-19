namespace Uniform.Chat.Terminal.Ui;

using System.Collections.ObjectModel;
using Uniform.Chat.Terminal.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;
using Uniform.Matrix.Calls;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using global::Terminal.Gui.App;
using global::Terminal.Gui.Drawing;
using global::Terminal.Gui.Input;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;
using Uniform.Chat.Terminal.Localization;

/// <summary>Main window: menu bar, room list, message log, member list, input line and status bar.</summary>
/// <remarks>
/// All members run on the UI thread; background results are marshalled with <see cref="IApplication.Invoke(Action)"/>. Split into partial files:
/// <list type="bullet">
/// <item><description>MainWindow.cs: layout, menus, keys, rooms, history and commands.</description></item>
/// <item><description>MainWindow.Messages.cs: message actions, replies and edits, typing, nick completion, files and devices.</description></item>
/// <item><description>MainWindow.Images.cs: inline ASCII previews of images and the image viewer.</description></item>
/// <item><description>MainWindow.Security.cs: encryption status, recovery and device verification.</description></item>
/// <item><description>MainWindow.Admin.cs: room administration: name, avatar, kick, ban and power levels.</description></item>
/// <item><description>MainWindow.Tools.cs: settings and log windows.</description></item>
/// <item><description>MainWindow.Calls.cs: voice calls.</description></item>
/// <item><description>MainWindow.Moderation.cs: reporting, blocking users and deleting the account.</description></item>
/// <item><description>MainWindow.Server.cs: server administration for server admins: accounts, registration tokens, reports, rooms and notices.</description></item>
/// </list>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private const int RoomListWidth = 28;
    private const int MemberListWidth = 24;
    private const int HistoryPageSize = 50;

    private readonly IApplication app;
    private readonly ClientSession session;
    private readonly SyncProcessor processor;
    private readonly OutboxSender outbox;
    private readonly ClientStore store;
    private readonly ConnectionStatus status;
    private readonly MediaCache mediaCache;
    private readonly IOptionsMonitor<UiOptions> uiOptionsMonitor;
    private readonly SettingsStore settings;
    private readonly OptionCatalog optionCatalog;
    private readonly IHostEnvironment environment;
    private readonly IConfiguration configuration;
    private readonly FileLoggerProvider fileLogger;
    private readonly StorageOptions storageOptions;
    private readonly CryptoService crypto;
    private readonly CallService calls;
    private readonly ILogger<MainWindow> logger;
    private readonly string ownUserId;

    private readonly ListView roomList = new() { Title = Strings.Rooms, X = 0, Y = 1, Width = RoomListWidth, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly TimelinePane timeline = new() { Title = "Matrix", X = RoomListWidth, Y = 1, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly ListView memberList = new() { Title = Strings.Members, X = Pos.AnchorEnd(MemberListWidth), Y = 1, Width = MemberListWidth, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly Label inputPrefix = new() { X = 0, Y = Pos.AnchorEnd(2) };
    private readonly TextField input = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
    private readonly Label statusText = new() { Text = Strings.Connecting, X = 1, Width = Dim.Fill() };
    private string statusMessage = Strings.Connecting;
    private readonly ObservableCollection<string> roomRows = [];
    private readonly List<string> roomIds = [];
    private readonly HashSet<string> roomsWithHighlight = [];
    private readonly ObservableCollection<string> memberRows = [];
    private readonly Dictionary<string, string> lastReadMarker = [];
    private readonly HashSet<string> membersLoaded = [];
    private string? currentRoomId;
    private bool updatingRooms;

    /// <summary>Create the window and start listening to sync, outbox and connection changes.</summary>
    public MainWindow(IApplication app, ClientSession session, SyncProcessor processor, OutboxSender outbox, ClientStore store, ConnectionStatus status, MediaCache mediaCache, CryptoService crypto, CallService calls, AccountPaths paths, IOptionsMonitor<UiOptions> uiOptions, SettingsStore settings, OptionCatalog optionCatalog, IHostEnvironment environment, IConfiguration configuration, FileLoggerProvider fileLogger, IOptions<StorageOptions> storageOptions, ILogger<MainWindow> logger)
    {
        // Store dependencies
        this.app = app;
        this.session = session;
        this.processor = processor;
        this.outbox = outbox;
        this.store = store;
        this.status = status;
        this.mediaCache = mediaCache;
        uiOptionsMonitor = uiOptions;
        this.settings = settings;
        this.optionCatalog = optionCatalog;
        this.environment = environment;
        this.configuration = configuration;
        this.fileLogger = fileLogger;
        this.storageOptions = storageOptions.Value;
        this.crypto = crypto;
        this.calls = calls;
        this.logger = logger;
        ownUserId = paths.UserId.ToString();

        // Lay out views
        Title = $"Matrix — {ownUserId}";
        BorderStyle = LineStyle.None;
        roomList.SetSource(roomRows);
        memberList.SetSource(memberRows);
        SetMemberListVisible(true);
        input.X = Pos.Right(inputPrefix);
        Add(CreateMenuBar(), roomList, timeline, memberList, inputPrefix, input, CreateStatusBar());

        // Wire input
        roomList.ValueChanged += (_, _) => OnRoomSelected();
        roomList.Accepting += (_, e) => { e.Handled = true; EnterRoom(); };
        roomList.RowRender += OnRoomRowRender;
        input.Accepting += (_, e) => { e.Handled = true; HandleInput(); };
        input.TextChanged += (_, _) => OnInputChanged();
        timeline.Accepting += (_, e) => { e.Handled = true; ShowMessageActions(); };
        timeline.PreviewProvider = GetPreview;
        app.Keyboard.KeyDown += OnKeyDown;
        input.SetFocus();

        // Listen to background services
        status.Changed += (state, message) => app.Invoke(() => OnConnectionChanged(state, message));
        outbox.RoomChanged += roomId => app.Invoke(() => { if (roomId == currentRoomId) _ = RefreshPendingAsync(roomId); });
        processor.EventsDecrypted += roomId => app.Invoke(() => { if (roomId == currentRoomId) _ = ReloadTimelineAsync(roomId); });
        uiOptions.OnChange(_ => app.Invoke(OnUiOptionsChanged));
        crypto.VerificationChanged += v => { VerificationState state = v.State; app.Invoke(() => OnVerificationChanged(v, state)); };
        crypto.VerificationIgnored += (sender, device, reason) => app.Invoke(() => ShowStatus(Texts.Format(Strings.VerificationIgnored, sender, device, reason)));
        calls.Devices = OpenCallDevices;
        calls.CallChanged += call => app.Invoke(() => OnCallChanged(call));
        calls.IncomingCall += call => app.Invoke(() => OnCallChanged(call));
        _ = Task.Run(ConsumeSyncChangesAsync);
    }

    /// <summary>Current display settings; they change when applied in the settings window.</summary>
    private UiOptions UiOptions => uiOptionsMonitor.CurrentValue;

    /// <summary>Logged-in client, or null until connected.</summary>
    private MatrixClient? Client => session.WhenReady.IsCompletedSuccessfully ? session.WhenReady.Result : null;

    /// <summary>Build the File, Room, View and Help menus.</summary>
    private MenuBar CreateMenuBar() => new([
        new MenuBarItem(Strings.MenuFile, [
            new MenuItem { Title = Strings.MenuEncryption, HelpText = Strings.MenuEncryptionHelp, Action = () => _ = ShowEncryptionAsync() },
            new MenuItem { Title = Strings.MenuVerifyDevice, HelpText = Strings.MenuVerifyDeviceHelp, Action = () => VerifyUser(ownUserId) },
            new MenuItem { Title = Strings.MenuDevices, HelpText = Strings.MenuDevicesHelp, Action = () => _ = ManageDevicesAsync() },
            new MenuItem { Title = Strings.MenuSettings, HelpText = Strings.MenuSettingsHelp, Action = ShowSettings },
            new MenuItem { Title = Strings.MenuLogOut, HelpText = Strings.MenuLogOutHelp, Action = LogOut },
            new MenuItem { Title = Strings.MenuDeleteAccount, HelpText = Strings.MenuDeleteAccountHelp, Action = DeleteAccount },
            new Line(),
            new MenuItem { Title = Strings.MenuQuit, HelpText = Strings.MenuQuitHelp, Key = Key.Q.WithCtrl, Action = () => app.RequestStop() },
        ]),
        new MenuBarItem(Strings.MenuRoom, [
            new MenuItem { Title = Strings.MenuJoin, HelpText = Strings.MenuJoinHelp, Key = Key.O.WithCtrl, Action = JoinRoom },
            new MenuItem { Title = Strings.MenuDirectory, HelpText = Strings.MenuDirectoryHelp, Key = Key.R.WithCtrl, Action = BrowseDirectory },
            new MenuItem { Title = Strings.MenuNewRoom, HelpText = Strings.MenuNewRoomHelp, Action = CreateRoom },
            new MenuItem { Title = Strings.MenuNewPublicRoom, HelpText = Strings.MenuNewPublicRoomHelp, Action = CreatePublicRoom },
            new MenuItem { Title = Strings.MenuDirectMessage, HelpText = Strings.MenuDirectMessageHelp, Action = StartDirectChat },
            new Line(),
            new MenuItem { Title = Strings.MenuSendFile, HelpText = Strings.MenuSendFileHelp, Action = SendFile },
            new MenuItem { Title = Strings.MenuCall, HelpText = Strings.MenuCallHelp, Action = PlaceCall },
            new MenuItem { Title = Strings.MenuHangUp, HelpText = Strings.MenuHangUpHelp, Action = HangupCall },
            new MenuItem { Title = Strings.MenuInvite, HelpText = Strings.MenuInviteHelp, Action = InviteUser },
            new MenuItem { Title = Strings.MenuSetTopic, HelpText = Strings.MenuSetTopicHelp, Action = SetTopic },
            new MenuItem { Title = Strings.MenuSetName, HelpText = Strings.MenuSetNameHelp, Action = SetRoomName },
            new MenuItem { Title = Strings.MenuSetAvatar, HelpText = Strings.MenuSetAvatarHelp, Action = SetRoomAvatar },
            new MenuItem { Title = Strings.MenuAddAlias, HelpText = Strings.MenuAddAliasHelp, Action = AddAlias },
            new MenuItem { Title = Strings.MenuDirectoryListing, HelpText = Strings.MenuDirectoryListingHelp, Action = ChangeDirectoryListing },
            new MenuItem { Title = Strings.MenuPowerLevel, HelpText = Strings.MenuPowerLevelHelp, Action = SetPowerLevel },
            new MenuItem { Title = Strings.MenuKick, HelpText = Strings.MenuKickHelp, Action = KickMember },
            new MenuItem { Title = Strings.MenuBan, HelpText = Strings.MenuBanHelp, Action = BanMember },
            new MenuItem { Title = Strings.MenuUnban, HelpText = Strings.MenuUnbanHelp, Action = UnbanMember },
            new MenuItem { Title = Strings.MenuEnableEncryption, HelpText = Strings.MenuEnableEncryptionHelp, Action = EnableEncryption },
            new MenuItem { Title = Strings.MenuLeave, HelpText = Strings.MenuLeaveHelp, Action = LeaveRoom },
        ]),
        new MenuBarItem(Strings.MenuView, [
            new MenuItem { Title = Strings.MenuOlderMessages, HelpText = Strings.MenuOlderMessagesHelp, Key = Key.PageUp.WithCtrl, Action = () => _ = LoadOlderAsync() },
            new MenuItem { Title = Strings.MenuMembers, HelpText = Strings.MenuMembersHelp, Key = Key.F2, Action = () => SetMemberListVisible(!memberList.Visible) },
            new MenuItem { Title = Strings.MenuSelectMessage, HelpText = Strings.MenuSelectMessageHelp, Key = Key.CursorUp.WithCtrl, Action = SelectMessage },
            new MenuItem { Title = Strings.MenuLogs, HelpText = Strings.MenuLogsHelp, Action = ShowLogs },
            new MenuItem { Title = Strings.MenuAudioTest, HelpText = Strings.MenuAudioTestHelp, Action = TestAudio },
            new Line(),
            new MenuItem { Title = Strings.MenuNextRoom, Key = Key.N.WithCtrl, Action = () => SelectRelativeRoom(+1) },
            new MenuItem { Title = Strings.MenuPreviousRoom, Key = Key.P.WithCtrl, Action = () => SelectRelativeRoom(-1) },
        ]),
        new MenuBarItem(Strings.MenuServer, [
            new MenuItem { Title = Strings.MenuAccounts, HelpText = Strings.MenuAccountsHelp, Action = () => ManageAccounts("") },
            new MenuItem { Title = Strings.MenuRegistrationTokens, HelpText = Strings.MenuRegistrationTokensHelp, Action = ManageRegistrationTokens },
            new MenuItem { Title = Strings.MenuReports, HelpText = Strings.MenuReportsHelp, Action = ManageReports },
            new MenuItem { Title = Strings.MenuServerRooms, HelpText = Strings.MenuServerRoomsHelp, Action = () => ManageServerRooms("") },
            new MenuItem { Title = Strings.MenuServerNotice, HelpText = Strings.MenuServerNoticeHelp, Action = () => SendServerNotice("") },
            new MenuItem { Title = Strings.MenuWhois, HelpText = Strings.MenuWhoisHelp, Action = () => ShowWhois("") },
        ]),
        new MenuBarItem(Strings.MenuHelp, [
            new MenuItem { Title = Strings.MenuCommandsAndKeys, Key = Key.F1, Action = ShowHelp },
            new MenuItem { Title = Strings.MenuAbout, Action = () => Dialogs.Info(app, Strings.About, Texts.Format(Strings.AboutText, Uniform.Matrix.Hosting.AppVersion.Of(typeof(MainWindow).Assembly))) },
        ]),
    ]);

    /// <summary>Build the status bar with key hints and the connection and typing status.</summary>
    private StatusBar CreateStatusBar()
    {
        // Key hints, then live status
        StatusBar bar = new() { Y = Pos.AnchorEnd() };
        bar.Add(new Shortcut { Title = Strings.Help, Key = Key.F1, Action = ShowHelp, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = Strings.Rooms, Key = Key.N.WithCtrl, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = Strings.Join, Key = Key.O.WithCtrl, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = Strings.Quit, Key = Key.Q.WithCtrl, BindKeyToApplication = false });
        bar.Add(statusText);
        // Return bar
        return bar;
    }

    /// <summary>Handle application-wide keys: menu shortcuts, Alt+1…9 for rooms, and PageUp/PageDown to scroll the log from the input line.</summary>
    /// <remarks>Menu items show their keys but do not bind them application-wide, so the shortcuts are handled here.</remarks>
    private void OnKeyDown(object? sender, Key key)
    {
        // Dialogs, e.g. settings, have their own keys
        if (!ReferenceEquals(app.TopRunnableView, this)) return;

        // Alt+digit selects that room, like irssi windows
        for (int i = 1; i <= 9; i++)
        {
            if (key != new Key((char)('0' + i)).WithAlt) continue;
            SelectRoomAt(i - 1);
            key.Handled = true;
            return;
        }

        // Menu shortcuts
        Action? action = GetShortcutAction(key);
        if (action is not null)
        {
            key.Handled = true;
            action();
            return;
        }

        // Leaving the log or reply mode
        if (key == Key.Esc && timeline.HasFocus)
        {
            input.SetFocus();
            key.Handled = true;
            return;
        }
        if (key == Key.Esc && input.HasFocus && (replyTo is not null || editEventId is not null))
        {
            bool wasEdit = editEventId is not null;
            CancelCompose();
            if (wasEdit) input.Text = "";
            key.Handled = true;
            return;
        }

        // Complete nicks in a non-empty input line; an empty one lets Tab move focus
        if (key == Key.Tab && input.HasFocus && input.Text.Length > 0)
        {
            CompleteNick();
            key.Handled = true;
            return;
        }

        // Scroll the log while typing
        if (!input.HasFocus) return;
        if (key == Key.PageUp) timeline.PageUp();
        else if (key == Key.PageDown) timeline.PageDown();
        else return;
        key.Handled = true;
    }

    /// <summary>Return the action of a menu shortcut key, or null.</summary>
    private Action? GetShortcutAction(Key key)
    {
        // Same keys as shown in the menus
        if (key == Key.Q.WithCtrl) return () => app.RequestStop();
        if (key == Key.O.WithCtrl) return JoinRoom;
        if (key == Key.R.WithCtrl) return BrowseDirectory;
        if (key == Key.N.WithCtrl) return () => SelectRelativeRoom(+1);
        if (key == Key.P.WithCtrl) return () => SelectRelativeRoom(-1);
        if (key == Key.PageUp.WithCtrl) return () => _ = LoadOlderAsync();
        if (key == Key.F2) return () => SetMemberListVisible(!memberList.Visible);
        if (key == Key.F1) return ShowHelp;
        // F3 as well, though Terminal.Gui 2.5 drops the ESC O R form most terminals send for it
        if (key == Key.CursorUp.WithCtrl || key == Key.F3) return SelectMessage;
        // Not a shortcut
        return null;
    }

    /// <summary>Read sync changes off the UI thread and apply them on it.</summary>
    private async Task ConsumeSyncChangesAsync()
    {
        // Forward each change set
        await foreach (SyncChanges changes in processor.Changes.ReadAllAsync())
            app.Invoke(() => ApplyChanges(changes));
    }

    /// <summary>Update rooms, the open timeline and status from one sync.</summary>
    private void ApplyChanges(SyncChanges changes)
    {
        // Remember rooms that mention us
        foreach (TimelineBatch batch in changes.Timeline)
        {
            if (batch.Room.RoomId == currentRoomId) continue;
            if (processor.Read(_ => batch.Room.HighlightCount) > 0) roomsWithHighlight.Add(batch.Room.RoomId);
        }
        RefreshRooms();

        // Open the configured or first room on start
        if (currentRoomId is null && roomIds.Count > 0) SelectRoomAt(FindStartRoom());

        // Append to the open room
        TimelineBatch? current = changes.Timeline.FirstOrDefault(b => b.Room.RoomId == currentRoomId);
        if (current is not null && currentRoomId is string roomId)
        {
            timeline.Append(Format(roomId, current.Events));
            ApplyRedactions(roomId, current.Events);
            _ = RefreshPendingAsync(roomId);
            MarkRead(roomId);
        }

        // Header and typing may have changed
        if (current is not null || changes.TypingChanged.Any(r => r.RoomId == currentRoomId)) RefreshHeader();
        foreach (Room room in changes.Invited) ShowStatus(Texts.Format(Strings.InvitedTo, processor.Read(_ => room.DisplayName)));
    }

    /// <summary>Show connection changes; a fatal error is reported and end the app.</summary>
    private void OnConnectionChanged(ConnectionState state, string? message)
    {
        // Status line, and once connected the first time, what encryption still needs
        ShowStatus(DescribeConnection(state, message));
        if (state == ConnectionState.Connected && !securityChecked)
        {
            securityChecked = true;
            _ = SuggestSecurityAsync();
        }

        // Fatal error ends the app
        if (state != ConnectionState.Failed) return;
        Dialogs.Error(app, Strings.CannotConnect, Texts.Format(Strings.SeeLogFile, message));
        app.RequestStop();
    }

    /// <summary>Return the status bar text for a connection state.</summary>
    private string DescribeConnection(ConnectionState state, string? message) => state switch
    {
        ConnectionState.Connecting => message ?? Strings.Connecting,
        ConnectionState.Connected => Texts.Format(Strings.ConnectedAs, ownUserId),
        ConnectionState.Retrying => Texts.Format(Strings.ConnectionProblem, message),
        _ => Texts.Format(Strings.Failed, message),
    };

    /// <summary>Show the connection state again after a temporary status message.</summary>
    private void RestoreStatus() => ShowStatus(DescribeConnection(status.State, status.Message));

    /// <summary>Show <paramref name="text"/> in the status bar, after who is typing.</summary>
    private void ShowStatus(string text)
    {
        // Keep the base text for typing updates
        statusMessage = text;
        RefreshTyping();
    }

    /// <summary>Show who is typing in the open room.</summary>
    private void RefreshTyping()
    {
        // Names of others typing
        List<string> typing = currentRoomId is null ? [] : processor.Read(r => r?.TryGetRoom(currentRoomId, out Room? room) == true ? room.TypingUserIds.Where(u => u != ownUserId).Select(room.GetMemberDisplayName).ToList() : []);
        // Show, with the status after it
        statusText.Text = typing.Count == 0 ? statusMessage : Texts.Format(Strings.TypingStatus, string.Join(", ", typing), statusMessage);
    }

    /// <summary>Rebuild the room list: invites, favourites, others, low priority; keep the selection.</summary>
    private void RefreshRooms()
    {
        // Snapshot rooms under lock
        List<(string Id, string Row)> rows = processor.Read(r => r is null ? [] : r.Rooms.Values
            .Where(room => room.Membership is Memberships.Join or Memberships.Invite)
            .OrderBy(room => room.Membership == Memberships.Invite ? 0 : room.Tags.ContainsKey(RoomTags.Favourite) ? 1 : room.Tags.ContainsKey(RoomTags.LowPriority) ? 3 : 2)
            .ThenBy(room => room.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(room => (room.RoomId, FormatRoomRow(room)))
            .ToList());

        // Replace rows without triggering room selection
        updatingRooms = true;
        roomIds.Clear();
        roomRows.Clear();
        foreach ((string id, string row) in rows)
        {
            roomIds.Add(id);
            roomRows.Add($"{roomIds.Count,2} {row}");
        }
        int selected = currentRoomId is null ? -1 : roomIds.IndexOf(currentRoomId);
        if (selected >= 0) roomList.SelectedItem = selected;
        updatingRooms = false;
    }

    /// <summary>Format one room list row: invite or direct mark, name and unread count; the number for Alt+N is added by <see cref="RefreshRooms"/>.</summary>
    private string FormatRoomRow(Room room)
    {
        // Mark, name and unread count; a direct chat named after a user ID has its @ already
        string prefix = room.Membership == Memberships.Invite ? "+" : room.IsDirect && !room.DisplayName.StartsWith('@') ? "@" : " ";
        string unread = room.NotificationCount > 0 && room.RoomId != currentRoomId ? $" ({room.NotificationCount})" : "";
        // Return row
        return $"{prefix}{room.DisplayName}{unread}";
    }

    /// <summary>Highlight rooms with mentions and unread messages.</summary>
    private void OnRoomRowRender(object? sender, ListViewRowEventArgs args)
    {
        // Assert row with marks
        if (args.Row >= roomIds.Count) return;
        string roomId = roomIds[args.Row];
        bool unread = roomRows[args.Row].EndsWith(')');
        if (roomId == currentRoomId || (!unread && !roomsWithHighlight.Contains(roomId))) return;
        // Color mentions yellow, other unread white
        TuiAttribute normal = roomList.GetAttributeForRole(VisualRole.Normal);
        args.RowAttribute = new TuiAttribute(roomsWithHighlight.Contains(roomId) ? Color.BrightYellow : Color.White, normal.Background);
    }

    /// <summary>Show the room picked in the room list; while browsing the list with arrow keys, focus stays in the list.</summary>
    private void OnRoomSelected()
    {
        // Ignore programmatic selection
        if (updatingRooms || roomList.SelectedItem is not int index || index >= roomIds.Count) return;
        if (roomIds[index] == currentRoomId) return;
        // Open, keeping the list focused when it has focus
        _ = OpenRoomAsync(roomIds[index], enter: !roomList.HasFocus);
    }

    /// <summary>Enter the room selected in the room list: answer its invite, or move to the input line.</summary>
    private void EnterRoom()
    {
        // Selected room, opened if browsing has not opened it yet
        if (roomList.SelectedItem is not int index || index >= roomIds.Count) return;
        string roomId = roomIds[index];
        if (roomId != currentRoomId)
        {
            _ = OpenRoomAsync(roomId, enter: true);
            return;
        }

        // Invites ask; joined rooms take input
        input.SetFocus();
        bool invited = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.Membership == Memberships.Invite);
        if (invited) AnswerInvite(roomId);
    }

    /// <summary>Select the room at <paramref name="index"/> in the room list.</summary>
    private void SelectRoomAt(int index)
    {
        // Assert index
        if (index < 0 || index >= roomIds.Count) return;
        // Select and open
        roomList.SelectedItem = index;
        _ = OpenRoomAsync(roomIds[index]);
    }

    /// <summary>Return the list index of <see cref="UiOptions.StartRoom"/>, matched by room ID or main alias, or 0 when unset or not found.</summary>
    private int FindStartRoom()
    {
        // Assert configured
        if (UiOptions.StartRoom is not string start) return 0;
        // Match ID, then alias
        int index = roomIds.IndexOf(start);
        if (index >= 0) return index;
        // Return alias match or the first room
        return Math.Max(0, roomIds.FindIndex(id => processor.Read(r => r?.TryGetRoom(id, out Room? room) == true && room.CanonicalAlias == start)));
    }

    /// <summary>Select the next or previous room, wrapping around.</summary>
    private void SelectRelativeRoom(int step)
    {
        // Assert rooms
        if (roomIds.Count == 0) return;
        int current = currentRoomId is null ? -1 : roomIds.IndexOf(currentRoomId);
        // Select
        SelectRoomAt((current + step + roomIds.Count) % roomIds.Count);
    }

    /// <summary>Show a room: header, members, stored history and pending messages.</summary>
    /// <param name="roomId">Room to show.</param>
    /// <param name="enter">Whether to move to the input line and answer an invite; false while browsing the room list, which keeps focus.</param>
    private async Task OpenRoomAsync(string roomId, bool enter = true)
    {
        // Switch, leaving composing and typing in the previous room
        if (currentRoomId is string previous) SendTyping(previous, false);
        CancelCompose();
        currentRoomId = roomId;
        roomsWithHighlight.Remove(roomId);
        RefreshRooms();
        RefreshHeader();
        if (enter) input.SetFocus();

        // Invites ask first, or wait for Enter while browsing
        string membership = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.Membership : Memberships.Leave);
        if (membership == Memberships.Invite)
        {
            timeline.SetEntries([new TimelineEntry { Text = Strings.InvitedToThisRoom, Kind = EntryKind.Info }]);
            if (enter) AnswerInvite(roomId);
            return;
        }

        // Stored history, then fill from the server when short
        IReadOnlyList<ClientEvent> events = await store.GetRoomEventsAsync(roomId, 200);
        if (currentRoomId != roomId) return;
        timeline.SetEntries(Format(roomId, events));
        await RefreshPendingAsync(roomId);
        if (events.Count < 30) await LoadOlderAsync();
        MarkRead(roomId);

        // Full member list once per room
        if (!membersLoaded.Add(roomId)) return;
        await RunSafelyAsync(Strings.LoadingMembers, client => processor.LoadMembersAsync(client, roomId));
        if (currentRoomId == roomId) RefreshHeader();
    }

    /// <summary>Reload the shown history of <paramref name="roomId"/> from the database, e.g. after late keys decrypted it.</summary>
    private async Task ReloadTimelineAsync(string roomId)
    {
        // Load and show if still open
        IReadOnlyList<ClientEvent> events = await store.GetRoomEventsAsync(roomId, Math.Max(200, timeline.Entries.Count));
        if (currentRoomId != roomId) return;
        timeline.SetEntries(Format(roomId, events));
        await RefreshPendingAsync(roomId);
    }

    /// <summary>Turn on encryption in the open room after confirmation; rooms cannot turn it off again.</summary>
    private void EnableEncryption()
    {
        // Assert room without encryption
        if (currentRoomId is not string roomId) return;
        bool already = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true && room.IsEncrypted);
        if (already)
        {
            Dialogs.Info(app, Strings.Encryption, Strings.AlreadyEncrypted);
            return;
        }
        if (!crypto.Available)
        {
            Dialogs.Error(app, Strings.Encryption, Texts.Format(Strings.EncryptionUnavailable, crypto.UnavailableReason));
            return;
        }

        // Confirm and enable
        if (!Dialogs.Confirm(app, Strings.EnableEncryption, Strings.EnableEncryptionQuestion)) return;
        _ = RunSafelyAsync(Strings.EnablingEncryption, client => client.SendStateEventAsync(roomId, EventTypes.RoomEncryption, new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm }));
    }

    /// <summary>Return the member list mark of a member, as in irssi: <c>@</c> admin, <c>%</c> moderator, <c>+</c> invited.</summary>
    private static string RolePrefix(long level, string membership) => membership == Memberships.Invite ? "+" : level >= PowerLevelsContent.Admin ? "@" : level >= PowerLevelsContent.Moderator ? "%" : "";

    /// <summary>Update the timeline title with room name and topic, and the member list.</summary>
    private void RefreshHeader()
    {
        // Snapshot room under lock
        (string title, List<string> members) = currentRoomId is null ? ("Matrix", []) : processor.Read(r =>
        {
            if (r is null || !r.TryGetRoom(currentRoomId, out Room? room)) return ("Matrix", new List<string>());
            string header = room.Topic is { Length: > 0 } topic ? $"{room.DisplayName} — {topic}" : room.DisplayName;
            List<string> names = [.. room.Members.Values.Where(m => m.IsActive)
                .Select(m => (Level: room.GetPowerLevel(m.UserId), Member: m))
                .OrderByDescending(m => m.Level >= PowerLevelsContent.Admin ? 2 : m.Level >= PowerLevelsContent.Moderator ? 1 : 0)
                .ThenBy(m => room.GetMemberDisplayName(m.Member.UserId), StringComparer.CurrentCultureIgnoreCase)
                .Select(m => RolePrefix(m.Level, m.Member.Membership) + room.GetMemberDisplayName(m.Member.UserId))];
            return (room.IsEncrypted ? Texts.Format(Strings.EncryptedHeader, header) : header, names);
        });

        // Apply
        timeline.Title = title;
        memberList.Title = Texts.Format(Strings.MembersCount, members.Count);
        memberRows.Clear();
        foreach (string name in members) memberRows.Add(name);
        RefreshTyping();
    }

    /// <summary>Format events of <paramref name="roomId"/> under the state lock.</summary>
    private List<TimelineEntry> Format(string roomId, IEnumerable<ClientEvent> events) => processor.Read(r =>
        r is not null && r.TryGetRoom(roomId, out Room? room) ? events.Where(e => e.StateKey is not null || e.Sender == ownUserId || !r.IgnoredUsers.Contains(e.Sender)).Select(e => MessageFormatter.Format(room, e, ownUserId, timeline.FindEvent)).OfType<TimelineEntry>().ToList() : []);

    /// <summary>Reload unsent messages of <paramref name="roomId"/> and show them if it is still open.</summary>
    private async Task RefreshPendingAsync(string roomId)
    {
        // Load unsent
        IReadOnlyList<OutboxMessage> unsent = await store.GetUnsentAsync(roomId);
        string nick = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.GetMemberDisplayName(ownUserId) : ownUserId);
        // Show on the UI thread
        app.Invoke(() => { if (currentRoomId == roomId) timeline.SetPending(unsent.Select(m => MessageFormatter.FormatOutbox(m, nick))); });
    }

    /// <summary>Prepend older messages: first from the database, then from the server.</summary>
    private async Task LoadOlderAsync()
    {
        // Assert open room
        if (currentRoomId is not string roomId) return;

        // Database first; fetch from the server until something older arrives or history ends
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IReadOnlyList<ClientEvent> older = await store.GetRoomEventsAsync(roomId, HistoryPageSize, timeline.OldestTimestamp);
            if (currentRoomId != roomId) return;
            if (older.Count > 0)
            {
                timeline.Prepend(Format(roomId, older));
                return;
            }
            if (Client is not MatrixClient client) return;
            IReadOnlyList<ClientEvent> fetched = await processor.BackfillAsync(client, roomId, HistoryPageSize);
            if (fetched.Count == 0) return;
        }
    }

    /// <summary>Send a read marker for the newest event of an open room once per new event.</summary>
    private void MarkRead(string roomId)
    {
        // Assert new last event
        string? last = timeline.Entries.LastOrDefault(e => e.EventId is not null)?.EventId;
        if (last is null || Client is not MatrixClient client) return;
        if (lastReadMarker.TryGetValue(roomId, out string? marked) && marked == last) return;
        lastReadMarker[roomId] = last;

        // Send in the background; failures only lose the unread reset
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SetReadMarkersAsync(roomId, last, last);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Read marker for {RoomId} failed: {Error}", roomId, ex.Describe());
            }
        });
    }

    /// <summary>Handle the input line: <c>/commands</c> or a message to the open room.</summary>
    private void HandleInput()
    {
        // Take the line
        string text = input.Text.Trim();
        input.Text = "";
        if (text.Length == 0) return;

        // Files dragged or pasted in, checked first as their paths may start with / like commands
        if (OfferDroppedFiles(text)) return;

        // Plain message
        if (!text.StartsWith('/'))
        {
            SendMessage(text, emote: false);
            return;
        }

        // Command and argument
        string[] parts = text[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        string argument = parts.Length > 1 ? parts[1] : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "me": SendMessage(argument, emote: true); break;
            case "plain": SendMessage(argument, emote: false, plain: true); break;
            case "upload" or "file": SendFile(); break;
            case "devices": _ = ManageDevicesAsync(); break;
            case "settings" or "set": ShowSettings(); break;
            case "logs" or "log": ShowLogs(); break;
            case "audiotest": TestAudio(); break;
            case "call": PlaceCall(); break;
            case "answer": AnswerCall(); break;
            case "reject": RejectCall(); break;
            case "hangup": HangupCall(); break;
            case "mute": ToggleMute(); break;
            case "encryption": _ = ShowEncryptionAsync(); break;
            case "recover": RecoverWithKey(); break;
            case "verify": VerifyUser(argument.Length > 0 ? argument : ownUserId); break;
            case "join": JoinRoom(argument); break;
            case "leave" or "part": LeaveRoom(); break;
            case "topic": SetTopic(argument); break;
            case "name": SetRoomName(argument); break;
            case "avatar": SetRoomAvatar(argument); break;
            case "kick": KickMember(argument); break;
            case "ban": BanMember(argument); break;
            case "unban": UnbanMember(argument); break;
            case "power": SetPowerLevel(argument); break;
            case "op": SetPowerLevel(argument.Length > 0 ? $"{argument.Split(' ')[0]} admin" : ""); break;
            case "deop": SetPowerLevel(argument.Length > 0 ? $"{argument.Split(' ')[0]} member" : ""); break;
            case "invite": InviteUser(argument); break;
            case "nick": _ = RunSafelyAsync(Strings.ChangingDisplayName, client => client.SetDisplayNameAsync(argument)); break;
            case "msg" or "query": StartDirectChat(argument); break;
            case "list": BrowseDirectory(argument); break;
            case "create": CreateRoom(argument); break;
            case "alias": AddAlias(argument); break;
            case "unalias": RemoveAlias(argument); break;
            case "publish": SetDirectoryListing(listed: true); break;
            case "unpublish": SetDirectoryListing(listed: false); break;
            case "ignore" or "block": SetIgnored(argument, ignored: true); break;
            case "unignore" or "unblock": SetIgnored(argument, ignored: false); break;
            case "report": Report(argument); break;
            case "whois": ShowWhois(argument); break;
            case "accounts" or "users": ManageAccounts(argument); break;
            case "suspend": SetSuspended(argument, suspended: true); break;
            case "unsuspend": SetSuspended(argument, suspended: false); break;
            case "lock": SetLocked(argument, locked: true); break;
            case "unlock": SetLocked(argument, locked: false); break;
            case "notice": SendServerNotice(argument); break;
            case "tokens": ManageRegistrationTokens(); break;
            case "reports": ManageReports(); break;
            case "rooms": ManageServerRooms(argument); break;
            case "quit" or "exit": app.RequestStop(); break;
            case "help": ShowHelp(); break;
            default: Dialogs.Error(app, Strings.UnknownCommand, Texts.Format(Strings.NotACommand, parts[0])); break;
        }
    }

    /// <summary>Join a room by alias, ID or link, asking when <paramref name="target"/> is empty; words after the room are servers to join through.</summary>
    private void JoinRoom(string target)
    {
        // Ask
        string? text = target.Length > 0 ? target : Dialogs.Ask(app, Strings.JoinRoom, Strings.JoinRoomPrompt);
        if (text is null) return;
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return;

        // Assert a room, not a user
        if (!MatrixLink.TryParse(words[0], out MatrixLink? link) || link.IsUser)
        {
            Dialogs.Error(app, Strings.InvalidRoom, Texts.Format(Strings.NotARoom, words[0]));
            return;
        }
        // Join with the link's servers and the typed ones
        JoinRoom(link with { Via = [.. link.Via.Concat(words.Skip(1)).Distinct()] });
    }

    /// <summary>Join the room of <paramref name="link"/> through its servers, or open it when joined already.</summary>
    private void JoinRoom(MatrixLink link)
    {
        // Joined rooms open
        string room = link.Target;
        if (FindJoinedRoom(room) is string roomId)
        {
            _ = OpenRoomAsync(roomId);
            return;
        }

        // Newer room IDs name no server, so one is needed to join through
        if (room.StartsWith('!') && !room.Contains(':') && link.Via.Count == 0)
        {
            Dialogs.Error(app, Strings.ServerNeeded, Texts.Format(Strings.RoomNamesNoServer, room, room));
            return;
        }
        // Join; the room appears with the next sync
        _ = RunSafelyAsync(Texts.Format(Strings.JoiningRoom, room), async client => { await client.JoinRoomAsync(room, link.Via.Count > 0 ? link.Via : null); app.Invoke(() => ShowStatus(Texts.Format(Strings.JoinedRoom, room))); });
    }

    /// <summary>Join from the menu, asking for the room.</summary>
    private void JoinRoom() => JoinRoom("");

    /// <summary>Return the ID of the joined room with ID or main alias <paramref name="roomIdOrAlias"/>, or null.</summary>
    private string? FindJoinedRoom(string roomIdOrAlias) =>
        processor.Read(r => r?.Rooms.Values.FirstOrDefault(room => room.Membership == Memberships.Join && (room.RoomId == roomIdOrAlias || room.CanonicalAlias == roomIdOrAlias))?.RoomId);

    /// <summary>Follow <paramref name="link"/> from a message: open a joined room or a direct chat, or offer to join the room or start a chat.</summary>
    private void FollowLink(MatrixLink link)
    {
        // Users: an existing direct chat opens, others are offered one
        if (link.IsUser)
        {
            if (link.Target == ownUserId) return;
            string? direct = processor.Read(r => r?.Rooms.Values.FirstOrDefault(room => room.Membership == Memberships.Join && room.DirectUserId == link.Target)?.RoomId);
            if (direct is not null) _ = OpenRoomAsync(direct);
            else if (Dialogs.Confirm(app, Strings.DirectMessage, Texts.Format(Strings.StartDirectChatQuestion, link.Target))) StartDirectChat(link.Target);
            return;
        }

        // Rooms: joined ones open, others are offered to join
        if (FindJoinedRoom(link.Target) is string roomId)
        {
            _ = OpenRoomAsync(roomId);
            return;
        }
        string through = link.Via.Count > 0 ? Texts.Format(Strings.JoinThrough, string.Join(", ", link.Via)) : "";
        if (Dialogs.Confirm(app, Strings.JoinRoom, Texts.Format(Strings.JoinQuestion, link.Target, through))) JoinRoom(link);
    }

    /// <summary>Server whose directory <see cref="BrowseDirectory(string)"/> showed last; empty for this homeserver.</summary>
    private string directoryServer = "";

    /// <summary>Search the public room directory of <paramref name="server"/>, or of the last one shown when empty, and join the chosen room.</summary>
    private void BrowseDirectory(string server)
    {
        // Assert connected
        if (Client is not MatrixClient client)
        {
            Dialogs.Error(app, Strings.NotConnected, Strings.WaitUntilConnected);
            return;
        }
        // Pick, remembering the server, and join through it
        if (server.Length > 0) directoryServer = server;
        (MatrixLink Room, string Server)? picked = ClientDialogs.PickPublicRoom(app, directoryServer, async (other, term) => (await client.GetPublicRoomsAsync(other, limit: 50, searchTerm: term)).Chunk);
        if (picked is not { } choice) return;
        directoryServer = choice.Server;
        JoinRoom(choice.Room);
    }

    /// <summary>Search the public room directory from the menu.</summary>
    private void BrowseDirectory() => BrowseDirectory("");

    /// <summary>Create a private room, asking for the name when <paramref name="roomName"/> is empty; an alias first, as in <c>/create #room name</c>, makes a public room instead.</summary>
    private void CreateRoom(string roomName)
    {
        // Public rooms start with their alias
        if (roomName.StartsWith('#'))
        {
            string[] parts = roomName.Split(' ', 2, StringSplitOptions.TrimEntries);
            CreatePublicRoom(parts[0], parts.Length > 1 ? parts[1] : "");
            return;
        }

        // Ask
        string? name = roomName.Length > 0 ? roomName : Dialogs.Ask(app, Strings.NewRoom, Strings.RoomNamePrompt);
        if (name is null) return;
        // Create
        // Encrypted like other clients do for private rooms, when encryption works
        IReadOnlyList<InitialStateEvent>? state = crypto.Available ? [new InitialStateEvent { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } }] : null;
        _ = RunSafelyAsync(Strings.CreatingRoom, client => client.CreateRoomAsync(new CreateRoomRequest { Name = name, Preset = RoomPresets.PrivateChat, InitialState = state }));
    }

    /// <summary>Create a room from the menu.</summary>
    private void CreateRoom() => CreateRoom("");

    /// <summary>Start a direct chat, asking for the user when <paramref name="userId"/> is empty.</summary>
    private void StartDirectChat(string userId)
    {
        // Ask and validate
        string? target = userId.Length > 0 ? userId : Dialogs.Ask(app, Strings.DirectMessage, Strings.UserIdPrompt);
        if (target is null) return;
        if (!UserId.TryParse(target, out _))
        {
            Dialogs.Error(app, Strings.InvalidUser, Texts.Format(Strings.NotAUser, target));
            return;
        }
        // Create room and invite
        _ = RunSafelyAsync(Texts.Format(Strings.StartingChatWith, target), client => client.CreateDirectRoomAsync(target, encrypted: crypto.Available));
    }

    /// <summary>Start a direct chat from the menu.</summary>
    private void StartDirectChat() => StartDirectChat("");

    /// <summary>Invite a user to the open room, asking when <paramref name="userId"/> is empty.</summary>
    private void InviteUser(string userId)
    {
        // Assert room and ask
        if (currentRoomId is not string roomId) return;
        string? target = userId.Length > 0 ? userId : Dialogs.Ask(app, Strings.Invite, Strings.UserIdPrompt);
        if (target is null) return;
        // Invite
        _ = RunSafelyAsync(Texts.Format(Strings.Inviting, target), client => client.InviteAsync(roomId, target));
    }

    /// <summary>Invite from the menu.</summary>
    private void InviteUser() => InviteUser("");

    /// <summary>Set the topic of the open room, asking when <paramref name="topic"/> is empty.</summary>
    private void SetTopic(string topic)
    {
        // Assert room and ask with the current topic
        if (currentRoomId is not string roomId) return;
        string current = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.Topic ?? "" : "");
        string? text = topic.Length > 0 ? topic : Dialogs.Ask(app, Strings.SetTopic, Strings.TopicPrompt, current);
        if (text is null) return;
        // Set
        _ = RunSafelyAsync(Strings.SettingTopic, client => client.SetRoomTopicAsync(roomId, text));
    }

    /// <summary>Set the topic from the menu.</summary>
    private void SetTopic() => SetTopic("");

    /// <summary>Leave the open room after confirmation.</summary>
    private void LeaveRoom()
    {
        // Assert room and confirm
        if (currentRoomId is not string roomId) return;
        string name = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.DisplayName : roomId);
        if (!Dialogs.Confirm(app, Strings.LeaveRoom, Texts.Format(Strings.LeaveQuestion, name))) return;
        // Leave
        _ = RunSafelyAsync(Texts.Format(Strings.Leaving, name), client => client.LeaveRoomAsync(roomId));
    }

    /// <summary>Ask whether to accept an invite, and join or rejects it.</summary>
    private void AnswerInvite(string roomId)
    {
        // Ask
        (string name, string? inviter, bool direct) = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? (room.DisplayName, room.InviterUserId, room.IsDirect) : (roomId, null, false));
        int? answer = MessageBox.Query(app, Strings.Invite, Texts.Format(Strings.InvitedYou, inviter ?? Strings.Someone, name), Strings.ButtonLater, Strings.ButtonReject, Strings.ButtonJoin);
        // Act; direct chats are recorded as such, as the inviter did
        if (answer == 2) _ = RunSafelyAsync(Texts.Format(Strings.JoiningRoom, name), async client =>
        {
            await client.JoinRoomAsync(roomId);
            if (direct && inviter is not null) await client.MarkDirectRoomAsync(inviter, roomId);
        });
        if (answer == 1) _ = RunSafelyAsync(Texts.Format(Strings.Rejecting, name), client => client.LeaveRoomAsync(roomId));
    }

    /// <summary>Log out after confirmation, deleting the device on the server, and quits.</summary>
    private void LogOut()
    {
        // Confirm
        if (!Dialogs.Confirm(app, Strings.LogOut, Strings.LogOutQuestion)) return;
        // Log out, forget the token and the user ID, so the next start asks who signs in, and quit
        _ = RunSafelyAsync(Strings.LoggingOut, async client =>
        {
            // Best effort: an unreachable server still ends the local session
            try { await client.LogoutAsync(); }
            catch (Exception ex) when (ex is MatrixException or HttpRequestException or TaskCanceledException) { logger.LogWarning("Logging out on the server failed: {Error}", ex.Describe()); }
            await store.ClearAccessTokenAsync();
            app.Invoke(() =>
            {
                settings.Save(new Dictionary<string, string?> { [$"{MatrixOptions.Section}:{nameof(MatrixOptions.UserId)}"] = null });
                app.RequestStop();
            });
        });
    }

    /// <summary>Show or hide the member list, giving its space to the log.</summary>
    private void SetMemberListVisible(bool visible)
    {
        // Toggle and resize the log
        memberList.Visible = visible;
        timeline.Width = Dim.Fill(visible ? MemberListWidth : 0);
    }

    /// <summary>Show input commands and keys.</summary>
    private void ShowHelp() => Dialogs.Info(app, Strings.CommandsAndKeys, Strings.HelpText);

    /// <summary>Run <paramref name="action"/> with the client, showing failures in a dialog.</summary>
    private async Task RunSafelyAsync(string what, Func<MatrixClient, Task> action)
    {
        // Assert connected
        if (Client is not MatrixClient client)
        {
            Dialogs.Error(app, Strings.NotConnected, Strings.WaitUntilConnected);
            return;
        }
        // Run, reporting errors
        try
        {
            await action(client);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{What} failed", what);
            app.Invoke(() => Dialogs.Error(app, what, ex.Message));
        }
    }
}
