namespace Uniform.Matrix.Client.Terminal.Ui;

using System.Collections.ObjectModel;
using Uniform.Matrix.Client.Terminal.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;
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
    private readonly ILogger<MainWindow> logger;
    private readonly string ownUserId;

    private readonly ListView roomList = new() { Title = "Rooms", X = 0, Y = 1, Width = RoomListWidth, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly TimelinePane timeline = new() { Title = "Matrix", X = RoomListWidth, Y = 1, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly ListView memberList = new() { Title = "Members", X = Pos.AnchorEnd(MemberListWidth), Y = 1, Width = MemberListWidth, Height = Dim.Fill(2), BorderStyle = LineStyle.Single };
    private readonly Label inputPrefix = new() { X = 0, Y = Pos.AnchorEnd(2) };
    private readonly TextField input = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
    private readonly Shortcut statusText = new() { Title = "", HelpText = "Connecting…", Width = Dim.Fill() };
    private readonly ObservableCollection<string> roomRows = [];
    private readonly List<string> roomIds = [];
    private readonly HashSet<string> roomsWithHighlight = [];
    private readonly ObservableCollection<string> memberRows = [];
    private readonly Dictionary<string, string> lastReadMarker = [];
    private readonly HashSet<string> membersLoaded = [];
    private string? currentRoomId;
    private bool updatingRooms;

    /// <summary>Create the window and start listening to sync, outbox and connection changes.</summary>
    public MainWindow(IApplication app, ClientSession session, SyncProcessor processor, OutboxSender outbox, ClientStore store, ConnectionStatus status, MediaCache mediaCache, CryptoService crypto, AccountPaths paths, IOptionsMonitor<UiOptions> uiOptions, SettingsStore settings, OptionCatalog optionCatalog, IHostEnvironment environment, IConfiguration configuration, FileLoggerProvider fileLogger, IOptions<StorageOptions> storageOptions, ILogger<MainWindow> logger)
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
        crypto.VerificationIgnored += (sender, device, reason) => app.Invoke(() => ShowStatus($"Verification request of {sender} {device} ignored: {reason}"));
        _ = Task.Run(ConsumeSyncChangesAsync);
    }

    /// <summary>Current display settings; they change when applied in the settings window.</summary>
    private UiOptions UiOptions => uiOptionsMonitor.CurrentValue;

    /// <summary>Logged-in client, or null until connected.</summary>
    private MatrixClient? Client => session.WhenReady.IsCompletedSuccessfully ? session.WhenReady.Result : null;

    /// <summary>Build the File, Room, View and Help menus.</summary>
    private MenuBar CreateMenuBar() => new([
        new MenuBarItem("_File", [
            new MenuItem { Title = "_Encryption…", HelpText = "Keys, recovery and backup", Action = () => _ = ShowEncryptionAsync() },
            new MenuItem { Title = "_Verify this device…", HelpText = "Emoji comparison", Action = () => VerifyUser(ownUserId) },
            new MenuItem { Title = "_Devices…", HelpText = "See and log out sessions", Action = () => _ = ManageDevicesAsync() },
            new MenuItem { Title = "_Settings…", HelpText = "View and change options", Action = ShowSettings },
            new MenuItem { Title = "_Log out…", HelpText = "End the session on the server", Action = LogOut },
            new Line(),
            new MenuItem { Title = "_Quit", HelpText = "Exit, keeping the session", Key = Key.Q.WithCtrl, Action = () => app.RequestStop() },
        ]),
        new MenuBarItem("_Room", [
            new MenuItem { Title = "_Join…", HelpText = "Join by alias or ID", Key = Key.O.WithCtrl, Action = JoinRoom },
            new MenuItem { Title = "Room _directory…", HelpText = "Search public rooms", Key = Key.R.WithCtrl, Action = BrowseDirectory },
            new MenuItem { Title = "_New room…", HelpText = "Create a private room", Action = CreateRoom },
            new MenuItem { Title = "Direct _message…", HelpText = "Start a chat with a user", Action = StartDirectChat },
            new Line(),
            new MenuItem { Title = "Send _file…", HelpText = "Upload a file here", Action = SendFile },
            new MenuItem { Title = "_Invite…", HelpText = "Invite a user here", Action = InviteUser },
            new MenuItem { Title = "Set _topic…", HelpText = "Change the topic", Action = SetTopic },
            new MenuItem { Title = "Set nam_e…", HelpText = "Change the room name", Action = SetRoomName },
            new MenuItem { Title = "Set _avatar…", HelpText = "Picture of the room", Action = SetRoomAvatar },
            new MenuItem { Title = "_Power level…", HelpText = "Make a member admin or moderator", Action = SetPowerLevel },
            new MenuItem { Title = "_Kick…", HelpText = "Remove a member", Action = KickMember },
            new MenuItem { Title = "_Ban…", HelpText = "Remove a member for good", Action = BanMember },
            new MenuItem { Title = "_Unban…", HelpText = "Let a banned user back", Action = UnbanMember },
            new MenuItem { Title = "Enable en_cryption…", HelpText = "Encrypt this room; cannot be undone", Action = EnableEncryption },
            new MenuItem { Title = "_Leave", HelpText = "Leave this room", Action = LeaveRoom },
        ]),
        new MenuBarItem("_View", [
            new MenuItem { Title = "_Older messages", HelpText = "Load history", Key = Key.PageUp.WithCtrl, Action = () => _ = LoadOlderAsync() },
            new MenuItem { Title = "_Members", HelpText = "Show or hide", Key = Key.F2, Action = () => SetMemberListVisible(!memberList.Visible) },
            new MenuItem { Title = "_Select message", HelpText = "Reply, react, edit…", Key = Key.CursorUp.WithCtrl, Action = SelectMessage },
            new MenuItem { Title = "_Logs…", HelpText = "This and earlier sessions", Action = ShowLogs },
            new Line(),
            new MenuItem { Title = "_Next room", Key = Key.N.WithCtrl, Action = () => SelectRelativeRoom(+1) },
            new MenuItem { Title = "_Previous room", Key = Key.P.WithCtrl, Action = () => SelectRelativeRoom(-1) },
        ]),
        new MenuBarItem("_Help", [
            new MenuItem { Title = "_Commands and keys", Key = Key.F1, Action = ShowHelp },
            new MenuItem { Title = "_About", Action = () => Dialogs.Info(app, "About", "Uniform.Matrix.Client.Terminal\nA Matrix chat client for the terminal.\n\nhttps://spec.matrix.org") },
        ]),
    ]);

    /// <summary>Build the status bar with key hints and the connection and typing status.</summary>
    private StatusBar CreateStatusBar()
    {
        // Key hints, then live status
        StatusBar bar = new() { Y = Pos.AnchorEnd() };
        bar.Add(new Shortcut { Title = "Help", Key = Key.F1, Action = ShowHelp, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = "Rooms", Key = Key.N.WithCtrl, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = "Join", Key = Key.O.WithCtrl, BindKeyToApplication = false });
        bar.Add(new Shortcut { Title = "Quit", Key = Key.Q.WithCtrl, BindKeyToApplication = false });
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
        foreach (Room room in changes.Invited) ShowStatus($"Invited to {processor.Read(_ => room.DisplayName)}");
    }

    /// <summary>Show connection changes; a fatal error is reported and end the app.</summary>
    private void OnConnectionChanged(ConnectionState state, string? message)
    {
        // Status line
        ShowStatus(DescribeConnection(state, message));

        // Fatal error ends the app
        if (state != ConnectionState.Failed) return;
        Dialogs.Error(app, "Cannot connect", $"{message}\n\nSee the log file for details.");
        app.RequestStop();
    }

    /// <summary>Return the status bar text for a connection state.</summary>
    private string DescribeConnection(ConnectionState state, string? message) => state switch
    {
        ConnectionState.Connecting => message ?? "Connecting…",
        ConnectionState.Connected => $"Connected as {ownUserId}",
        ConnectionState.Retrying => $"Connection problem: {message}",
        _ => $"Failed: {message}",
    };

    /// <summary>Show the connection state again after a temporary status message.</summary>
    private void RestoreStatus() => ShowStatus(DescribeConnection(status.State, status.Message));

    /// <summary>Show <paramref name="text"/> in the status bar, followed by who is typing.</summary>
    private void ShowStatus(string text)
    {
        // Keep the base text for typing updates
        statusText.HelpText = text;
        RefreshTyping();
    }

    /// <summary>Show who is typing in the open room.</summary>
    private void RefreshTyping()
    {
        // Names of others typing
        List<string> typing = currentRoomId is null ? [] : processor.Read(r => r?.TryGetRoom(currentRoomId, out Room? room) == true ? room.TypingUserIds.Where(u => u != ownUserId).Select(room.GetMemberDisplayName).ToList() : []);
        // Show; the status bar sizes items at layout, so lay out again
        statusText.Title = typing.Count == 0 ? "" : $"{string.Join(", ", typing)} typing…";
        statusText.SetNeedsLayout();
        statusText.SuperView?.SetNeedsLayout();
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
        // Mark, name and unread count
        string prefix = room.Membership == Memberships.Invite ? "+" : room.IsDirect ? "@" : " ";
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
            timeline.SetEntries([new TimelineEntry { Text = "-!- You are invited to this room. Press Enter to join or reject.", Kind = EntryKind.Info }]);
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
        await RunSafelyAsync("Loading members", client => processor.LoadMembersAsync(client, roomId));
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
            Dialogs.Info(app, "Encryption", "This room is already encrypted.");
            return;
        }
        if (!crypto.Available)
        {
            Dialogs.Error(app, "Encryption", $"Encryption is not available on this device.\n\n{crypto.UnavailableReason}");
            return;
        }

        // Confirm and enable
        if (!Dialogs.Confirm(app, "Enable encryption", "Encrypt all new messages in this room? This cannot be turned off later.")) return;
        _ = RunSafelyAsync("Enabling encryption", client => client.SendStateEventAsync(roomId, EventTypes.RoomEncryption, new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm }));
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
            return (room.IsEncrypted ? $"{header} [encrypted]" : header, names);
        });

        // Apply
        timeline.Title = title;
        memberList.Title = $"Members ({members.Count})";
        memberRows.Clear();
        foreach (string name in members) memberRows.Add(name);
        RefreshTyping();
    }

    /// <summary>Format events of <paramref name="roomId"/> under the state lock.</summary>
    private List<TimelineEntry> Format(string roomId, IEnumerable<ClientEvent> events) => processor.Read(r =>
        r is not null && r.TryGetRoom(roomId, out Room? room) ? events.Select(e => MessageFormatter.Format(room, e, ownUserId, timeline.FindEvent)).OfType<TimelineEntry>().ToList() : []);

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
                logger.LogInformation("Read marker for {RoomId} failed: {Error}", roomId, ex.Message);
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
            case "nick": _ = RunSafelyAsync("Changing display name", client => client.SetDisplayNameAsync(argument)); break;
            case "msg" or "query": StartDirectChat(argument); break;
            case "list": BrowseDirectory(); break;
            case "create": CreateRoom(argument); break;
            case "quit" or "exit": app.RequestStop(); break;
            case "help": ShowHelp(); break;
            default: Dialogs.Error(app, "Unknown command", $"/{parts[0]} is not a command. See Help → Commands and keys."); break;
        }
    }

    /// <summary>Join a room by alias or ID, asking when <paramref name="target"/> is empty.</summary>
    private void JoinRoom(string target)
    {
        // Ask
        string? room = target.Length > 0 ? target : Dialogs.Ask(app, "Join room", "Room alias or ID, e.g. #matrix:matrix.org:");
        if (room is null) return;
        // Join; the room appears with the next sync
        _ = RunSafelyAsync($"Joining {room}", async client => { await client.JoinRoomAsync(room); app.Invoke(() => ShowStatus($"Joined {room}")); });
    }

    /// <summary>Join from the menu, asking for the room.</summary>
    private void JoinRoom() => JoinRoom("");

    /// <summary>Search the public room directory and join the chosen room.</summary>
    private void BrowseDirectory()
    {
        // Assert connected
        if (Client is not MatrixClient client)
        {
            Dialogs.Error(app, "Not connected", "Wait until the client is connected.");
            return;
        }
        // Pick and join
        string? room = ClientDialogs.PickPublicRoom(app, async term => (await client.GetPublicRoomsAsync(limit: 50, searchTerm: term)).Chunk);
        if (room is not null) JoinRoom(room);
    }

    /// <summary>Create a private room, asking for the name when <paramref name="roomName"/> is empty.</summary>
    private void CreateRoom(string roomName)
    {
        // Ask
        string? name = roomName.Length > 0 ? roomName : Dialogs.Ask(app, "New room", "Room name:");
        if (name is null) return;
        // Create
        // Encrypted like other clients do for private rooms, when encryption works
        IReadOnlyList<InitialStateEvent>? state = crypto.Available ? [new InitialStateEvent { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } }] : null;
        _ = RunSafelyAsync("Creating room", client => client.CreateRoomAsync(new CreateRoomRequest { Name = name, Preset = RoomPresets.PrivateChat, InitialState = state }));
    }

    /// <summary>Create a room from the menu.</summary>
    private void CreateRoom() => CreateRoom("");

    /// <summary>Start a direct chat, asking for the user when <paramref name="userId"/> is empty.</summary>
    private void StartDirectChat(string userId)
    {
        // Ask and validate
        string? target = userId.Length > 0 ? userId : Dialogs.Ask(app, "Direct message", "User ID, e.g. @alice:matrix.org:");
        if (target is null) return;
        if (!UserId.TryParse(target, out _))
        {
            Dialogs.Error(app, "Invalid user", $"{target} is not a user ID like @alice:matrix.org.");
            return;
        }
        // Create room and invite
        _ = RunSafelyAsync($"Starting chat with {target}", client => client.CreateDirectRoomAsync(target, encrypted: crypto.Available));
    }

    /// <summary>Start a direct chat from the menu.</summary>
    private void StartDirectChat() => StartDirectChat("");

    /// <summary>Invite a user to the open room, asking when <paramref name="userId"/> is empty.</summary>
    private void InviteUser(string userId)
    {
        // Assert room and ask
        if (currentRoomId is not string roomId) return;
        string? target = userId.Length > 0 ? userId : Dialogs.Ask(app, "Invite", "User ID, e.g. @alice:matrix.org:");
        if (target is null) return;
        // Invite
        _ = RunSafelyAsync($"Inviting {target}", client => client.InviteAsync(roomId, target));
    }

    /// <summary>Invite from the menu.</summary>
    private void InviteUser() => InviteUser("");

    /// <summary>Set the topic of the open room, asking when <paramref name="topic"/> is empty.</summary>
    private void SetTopic(string topic)
    {
        // Assert room and ask with the current topic
        if (currentRoomId is not string roomId) return;
        string current = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.Topic ?? "" : "");
        string? text = topic.Length > 0 ? topic : Dialogs.Ask(app, "Set topic", "Topic:", current);
        if (text is null) return;
        // Set
        _ = RunSafelyAsync("Setting topic", client => client.SetRoomTopicAsync(roomId, text));
    }

    /// <summary>Set the topic from the menu.</summary>
    private void SetTopic() => SetTopic("");

    /// <summary>Leave the open room after confirmation.</summary>
    private void LeaveRoom()
    {
        // Assert room and confirm
        if (currentRoomId is not string roomId) return;
        string name = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? room.DisplayName : roomId);
        if (!Dialogs.Confirm(app, "Leave room", $"Leave {name}?")) return;
        // Leave
        _ = RunSafelyAsync($"Leaving {name}", client => client.LeaveRoomAsync(roomId));
    }

    /// <summary>Ask whether to accept an invite, and join or rejects it.</summary>
    private void AnswerInvite(string roomId)
    {
        // Ask
        (string name, string? inviter, bool direct) = processor.Read(r => r?.TryGetRoom(roomId, out Room? room) == true ? (room.DisplayName, room.InviterUserId, room.IsDirect) : (roomId, null, false));
        int? answer = MessageBox.Query(app, "Invite", $"{inviter ?? "Someone"} invited you to {name}.", "_Later", "_Reject", "_Join");
        // Act; direct chats are recorded as such, as the inviter did
        if (answer == 2) _ = RunSafelyAsync($"Joining {name}", async client =>
        {
            await client.JoinRoomAsync(roomId);
            if (direct && inviter is not null) await client.MarkDirectRoomAsync(inviter, roomId);
        });
        if (answer == 1) _ = RunSafelyAsync($"Rejecting {name}", client => client.LeaveRoomAsync(roomId));
    }

    /// <summary>Log out after confirmation, deleting the device on the server, and quits.</summary>
    private void LogOut()
    {
        // Confirm
        if (!Dialogs.Confirm(app, "Log out", "End this session on the server? Messages stay in the local database.")) return;
        // Log out, forget the token and quit
        _ = RunSafelyAsync("Logging out", async client =>
        {
            await client.LogoutAsync();
            await store.ClearAccessTokenAsync();
            app.Invoke(() => app.RequestStop());
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
    private void ShowHelp() => Dialogs.Info(app, "Commands and keys", string.Join('\n',
        "**bold** _italic_ `code` ~~strike~~ [text](url)   Markdown is sent formatted",
        "/plain text     send without Markdown",
        "/me text        action, like * you wave",
        "/join #room     join a room",
        "/leave          leave this room",
        "/topic text     set the topic",
        "/name text      set the room name",
        "/avatar [file]  set the room picture; /avatar none removes it",
        "/kick @user [reason]   remove a member",
        "/ban @user [reason]    remove a member for good",
        "/unban @user    let a banned user back",
        "/power @user level     set a power level: number, admin, moderator or member",
        "/op @user, /deop @user  make admin, or ordinary member",
        "/invite @user   invite a user",
        "/msg @user      start a direct chat",
        "/nick name      set your display name",
        "/list           browse public rooms",
        "/create name    create a private room",
        "/upload         send a file",
        "/devices        manage your sessions",
        "/settings       view and change options",
        "/logs           read this and earlier session logs",
        "/encryption     keys, recovery and backup",
        "/recover        unlock old messages with the recovery key",
        "/verify [@user] verify your other device, or a user, with emoji",
        "/quit           exit",
        "",
        "Alt+1…9         go to room 1…9",
        "Ctrl+N, Ctrl+P  next, previous room",
        "Ctrl+O          join a room",
        "Ctrl+R          room directory",
        "PageUp/Down     scroll messages",
        "Ctrl+PageUp     load older messages",
        "F2              show or hide members",
        "Ctrl+Up, Enter  select a message, then act on it",
        "Tab             complete a nick",
        "Esc             cancel reply or edit",
        "F10             open the menu"));

    /// <summary>Run <paramref name="action"/> with the client, showing failures in a dialog.</summary>
    private async Task RunSafelyAsync(string what, Func<MatrixClient, Task> action)
    {
        // Assert connected
        if (Client is not MatrixClient client)
        {
            Dialogs.Error(app, "Not connected", "Wait until the client is connected.");
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
