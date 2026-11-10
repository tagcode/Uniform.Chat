namespace Uniform.Chat.Avalonia.ViewModels;

using System.Collections.ObjectModel;
using global::Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Chat.Avalonia.Services;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>The signed-in screen: the room list, the open room's messages and the message box.</summary>
/// <remarks>Sync changes arrive on a background thread and are applied on the UI thread through <see cref="IUiDispatcher"/>. Partial files: ChatViewModel.cs, and ChatViewModel.Actions.cs for the action sheets of messages and rooms.</remarks>
public sealed partial class ChatViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>How many lines a room opens with.</summary>
    private const int InitialLines = 250;
    /// <summary>How many older lines scrolling to the top adds.</summary>
    private const int MoreLines = 100;
    /// <summary>How many events a page of history from the server asks for.</summary>
    private const int PageSize = 50;
    private static readonly TimeSpan TypingRenewal = TimeSpan.FromSeconds(20);
    private readonly ClientConnection connection;
    private readonly IUiDispatcher ui;
    private string directoryServer = "";
    private readonly bool singlePane;
    private readonly Func<Task> signOut;
    private readonly Dictionary<string, RoomItemViewModel> roomsById = [];
    private readonly Dictionary<string, string> readMarkers = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly INotifier? notifier;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly HashSet<string> avatarsLoading = [];
    private readonly Dictionary<string, Task<Bitmap?>> pictures = [];
    private string? openedRoomId;
    private long? oldestLine;
    private DateTimeOffset typingSentAt = DateTimeOffset.MinValue;
    private int pendingVersion;
    private string? openWhenArrived;
    private bool notifiedChanges;
    private Task? consuming;
    private bool securityChecked;
    private bool securitySuggestionDismissed;

    /// <summary>Create the screen over a started connection.</summary>
    /// <param name="connection">Engine of the signed-in account.</param>
    /// <param name="ui">Dispatcher of the user interface thread.</param>
    /// <param name="singlePane">Whether the screen shows one pane at a time, as on phones.</param>
    /// <param name="signOut">Called by <see cref="SignOutCommand"/> to end the session and return to the sign-in form.</param>
    /// <param name="notifier">System notifications, or null for none.</param>
    /// <param name="settings">App settings, shown in the settings panel; null hides it.</param>
    /// <param name="call">The call bar; null for no calls.</param>
    public ChatViewModel(ClientConnection connection, IUiDispatcher ui, bool singlePane, Func<Task> signOut, INotifier? notifier = null, SettingsViewModel? settings = null, CallViewModel? call = null)
    {
        // Dependencies
        this.connection = connection;
        this.ui = ui;
        this.singlePane = singlePane;
        this.signOut = signOut;
        this.notifier = notifier;
        Settings = settings;
        Call = call;
        Messages = new MessageList(connection.UserId) { IsBlocked = connection.Processor.IsIgnored };
        Messages.Items.CollectionChanged += (_, e) => ShowSenderAvatars(e.NewItems?.OfType<MessageViewModel>() ?? []);
        IsNarrow = singlePane;
        StatusText = Describe(connection.Status.State, connection.Status.Message);
    }

    /// <summary>Rooms, invites first, then favourites, others and low priority, each by name.</summary>
    public ObservableCollection<RoomItemViewModel> Rooms { get; } = [];

    /// <summary>The call bar; null for no calls.</summary>
    public CallViewModel? Call { get; }

    /// <summary>The open room's timeline.</summary>
    public MessageList Messages { get; }

    /// <summary>Signed-in user.</summary>
    public string UserId => connection.UserId;

    /// <summary>The open room, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenRoom), nameof(ShowRoomList), nameof(ShowRoom), nameof(CanCallRoom), nameof(CanVideoCallRoom))]
    public partial RoomItemViewModel? SelectedRoom { get; set; }

    /// <summary>Whether the open room has one other member, who can be voice called.</summary>
    public bool CanCallRoom => Call?.CanCall == true && SelectedRoom is { IsOneToOne: true, IsInvite: false };

    /// <summary>Whether the open room can be video called: a direct room, on a device with a camera.</summary>
    public bool CanVideoCallRoom => CanCallRoom && Call?.CanVideoCall == true;

    /// <summary>Video call the other member of the open room.</summary>
    [RelayCommand]
    private async Task VideoCallRoomAsync()
    {
        // The call bar does the rest
        if (Call is null || SelectedRoom is not RoomItemViewModel room) return;
        await Call.PlaceCallAsync(room.RoomId, video: true);
    }

    /// <summary>Voice call the other member of the open room.</summary>
    [RelayCommand]
    private async Task CallRoomAsync()
    {
        // The call bar does the rest
        if (Call is null || SelectedRoom is not RoomItemViewModel room) return;
        await Call.PlaceCallAsync(room.RoomId);
    }

    /// <summary>Text being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Composer { get; set; } = "";

    /// <summary>Connection state, e.g. <c>Syncing</c>; empty when connected.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>Who is typing in the open room, e.g. <c>Bob is typing…</c>; empty for nobody.</summary>
    [ObservableProperty]
    public partial string TypingText { get; set; } = "";

    /// <summary>Text of the room box: a room alias or ID to join, a user ID to chat with, or a name for a new room.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTargetCommand))]
    [NotifyPropertyChangedFor(nameof(OpenTargetText))]
    public partial string JoinTarget { get; set; } = "";

    /// <summary>Get the room box's button text: Join, Chat or Create by what was typed.</summary>
    public string OpenTargetText => JoinTarget.Trim() switch
    {
        ['@', ..] => "Chat",
        ['#' or '!', ..] => "Join",
        _ => "Create",
    };

    /// <summary>Message the composer answers, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeMode), nameof(IsComposingSpecial))]
    public partial MessageViewModel? ReplyingTo { get; set; }

    /// <summary>Own message the composer replaces, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeMode), nameof(IsComposingSpecial))]
    public partial MessageViewModel? Editing { get; set; }

    /// <summary>Get what the composer does besides sending a new message, e.g. <c>Replying to Bob: snippet</c>.</summary>
    public string ComposeMode => Editing is not null ? "Editing message" : ReplyingTo is MessageViewModel parent ? $"Replying to {parent.SenderName}: {MessageList.Snippet(parent.Text)}" : "";

    /// <summary>Get whether the composer replies or edits.</summary>
    public bool IsComposingSpecial => Editing is not null || ReplyingTo is not null;

    /// <summary>User ID to invite to the open room.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
    public partial string InviteTarget { get; set; } = "";

    /// <summary>New topic for the open room.</summary>
    [ObservableProperty]
    public partial string TopicDraft { get; set; } = "";

    /// <summary>Joined and invited members of the open room by name, filled by <see cref="LoadMembersCommand"/>.</summary>
    public ObservableCollection<MemberItem> Members { get; } = [];

    /// <summary>Whether the app's window has the focus; notifications are shown only while it has not.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Rooms with unread messages, and invites, for the window title.</summary>
    [ObservableProperty]
    public partial int UnreadRooms { get; set; }

    /// <summary>App settings, or null when not offered.</summary>
    public SettingsViewModel? Settings { get; }

    /// <summary>Whether the settings panel is shown.</summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>Open room directory, or null.</summary>
    [ObservableProperty]
    public partial DirectoryViewModel? RoomDirectory { get; set; }

    /// <summary>Open security panel, or null.</summary>
    [ObservableProperty]
    public partial SecurityViewModel? Security { get; set; }

    /// <summary>Open server administration panel, or null.</summary>
    [ObservableProperty]
    public partial AdminViewModel? Admin { get; set; }

    /// <summary>Whether the user administers the server, so the administration panel is offered.</summary>
    [ObservableProperty]
    public partial bool IsServerAdmin { get; set; }

    /// <summary>Open the server administration panel at the reports.</summary>
    [RelayCommand]
    private async Task OpenAdminAsync()
    {
        // Shown, then loaded
        AdminViewModel admin = new(connection);
        Admin = admin;
        await admin.RefreshAsync();
    }

    /// <summary>Close the server administration panel.</summary>
    [RelayCommand]
    private void CloseAdmin() => Admin = null;

    /// <summary>Ask the server whether the user administers it.</summary>
    private async Task CheckServerAdminAsync()
    {
        // Quietly; most users are not
        bool admin = false;
        await RunAsync("Checking admin rights", async client => admin = await client.IsServerAdminAsync(), report: false);
        ui.Post(() => IsServerAdmin = admin);
    }

    /// <summary>Verification in progress or just ended, or null.</summary>
    [ObservableProperty]
    public partial VerificationViewModel? Verification { get; set; }

    /// <summary>Last error or result to show, e.g. why joining failed; empty for none.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = "";

    /// <summary>Whether the server refused something because the account is suspended; the message box is off until the banner is dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSuspended { get; set; }

    /// <summary>Whether the open room takes messages: not while suspended, nor in the server's notices room.</summary>
    public bool CanCompose => !IsSuspended && SelectedRoom is not { IsServerNotice: true } && SelectedRoom is not { IsInvite: true };

    /// <summary>Whether the window is too narrow for two panes; always true on phones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoomList), nameof(ShowRoom))]
    public partial bool IsNarrow { get; set; }

    /// <summary>Whether more history may be loaded for the open room.</summary>
    [ObservableProperty]
    public partial bool IsLoadingHistory { get; set; }

    /// <summary>Whether a room is open.</summary>
    public bool HasOpenRoom => SelectedRoom is not null;

    /// <summary>Whether the room list is shown: always on wide screens, on narrow ones until a room is opened.</summary>
    public bool ShowRoomList => !IsNarrow || SelectedRoom is null;

    /// <summary>Whether the open room is shown: always on wide screens, on narrow ones once a room is opened.</summary>
    public bool ShowRoom => !IsNarrow || SelectedRoom is not null;

    /// <summary>Start following the engine: connection state, sync changes and late-decrypted messages.</summary>
    public void Start()
    {
        // Engine events come on background threads
        connection.Status.Changed += OnConnectionChanged;
        connection.Processor.EventsDecrypted += OnEventsDecrypted;
        connection.Outbox.RoomChanged += OnOutboxChanged;
        connection.Crypto.VerificationChanged += OnVerificationChanged;
        consuming = Task.Run(ConsumeChangesAsync);
        // Rooms known already, e.g. after a quick resume, and security when connected already
        ui.Post(RefreshRooms);
        if (connection.Status.State == ConnectionState.Connected) OnConnectionChanged(ConnectionState.Connected, connection.Status.Message);
        _ = CheckServerAdminAsync();
    }

    /// <summary>Stop following the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        // Unsubscribe and end the reader
        connection.Status.Changed -= OnConnectionChanged;
        connection.Processor.EventsDecrypted -= OnEventsDecrypted;
        connection.Outbox.RoomChanged -= OnOutboxChanged;
        connection.Crypto.VerificationChanged -= OnVerificationChanged;
        Call?.Dispose();
        await stopping.CancelAsync();
        if (consuming is not null) await consuming.ContinueWith(_ => { }, TaskScheduler.Default);
        stopping.Dispose();
    }

    /// <summary>Show the selected room.</summary>
    partial void OnSelectedRoomChanged(RoomItemViewModel? value)
    {
        // Opening clears the composer state of the previous room; whether it takes messages depends on the room
        OnPropertyChanged(nameof(CanCompose));
        SendCommand.NotifyCanExecuteChanged();
        TypingText = "";
        ReplyingTo = null;
        Editing = null;
        openedRoomId = null;
        if (value is null)
        {
            Messages.Items.Clear();
            return;
        }
        _ = OpenRoomAsync(value.RoomId);
    }

    /// <summary>Send typing notices while writing, renewed now and then.</summary>
    partial void OnComposerChanged(string value)
    {
        // Typing in the open, joined room
        if (SelectedRoom is not { IsInvite: false } room || value.Length == 0) return;
        if (DateTimeOffset.UtcNow - typingSentAt < TypingRenewal) return;
        typingSentAt = DateTimeOffset.UtcNow;
        _ = RunAsync("Typing", client => client.SetTypingAsync(room.RoomId, true), report: false);
    }

    /// <summary>Queue the message in the composer to the open room; Markdown is sent formatted.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // Assert a joined room and text
        if (SelectedRoom is not { IsInvite: false } room || Composer.Trim() is not { Length: > 0 } text) return;
        Composer = "";

        // Emotes with /me, the rest as formatted text; replies and edits by the compose mode
        bool emote = text.StartsWith("/me ", StringComparison.Ordinal);
        if (emote) text = text[4..];
        string? html = MarkdownComposer.ToHtml(text);
        MessageContent content = Editing is { EventId: string edited } ? MatrixClient.CreateEditContent(edited, text, html)
            : ReplyingTo is { Event: ClientEvent parent } ? MatrixClient.CreateReplyContent(parent, text, connection.UserId, html: html)
            : new() { MessageType = emote ? MessageTypes.Emote : MessageTypes.Text, Body = text, Format = html is null ? null : MessageFormats.Html, FormattedBody = html };
        ReplyingTo = null;
        Editing = null;
        await connection.Outbox.EnqueueAsync(room.RoomId, EventTypes.RoomMessage, content);
        typingSentAt = DateTimeOffset.MinValue;
        _ = RunAsync("Typing", client => client.SetTypingAsync(room.RoomId, false), report: false);
        await RefreshPendingAsync(room.RoomId);
    }

    /// <summary>Follow a matrix.to link or <c>matrix:</c> URI inside the app: open a room already joined, or put a room, a room ID with the servers to join through, or a person in the room box to join or chat with; return false for other links.</summary>
    public bool OpenMatrixLink(string href)
    {
        // Assert a link to a room or user
        if (!href.StartsWith("https://matrix.to/", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("matrix:", StringComparison.OrdinalIgnoreCase) || !MatrixLink.TryParse(href, out MatrixLink? link)) return false;
        string target = link.Target;

        // Joined rooms and existing chats open; others wait in the room box for Join or Chat
        string? roomId = connection.Processor.Read(store => store?.Rooms.Values.FirstOrDefault(room => room.Membership == Memberships.Join && (room.RoomId == target || room.CanonicalAlias == target || room.DirectUserId == target))?.RoomId);
        if (roomId is not null && roomsById.TryGetValue(roomId, out RoomItemViewModel? room))
        {
            SelectedRoom = room;
            return true;
        }
        if (target == connection.UserId) return true;
        JoinTarget = target[0] == '!' ? string.Join(' ', [target, .. link.Via]) : target;
        Notice = target[0] == '@' ? $"Choose Chat to start a chat with {target}." : $"Choose Join to join {target}.";
        // Done
        return true;
    }

    /// <summary>Answer <paramref name="message"/> with the next message.</summary>
    public void StartReply(MessageViewModel message)
    {
        // Replace any edit
        if (!message.CanAnswer) return;
        Editing = null;
        ReplyingTo = message;
    }

    /// <summary>Edit own <paramref name="message"/>: its text goes to the composer.</summary>
    public void StartEdit(MessageViewModel message)
    {
        // Replace any reply
        if (!message.CanEdit) return;
        ReplyingTo = null;
        Editing = message;
        Composer = message.Kind == MessageKind.Emote ? $"/me {message.Source}" : message.Source;
    }

    /// <summary>Stop replying or editing; an edit's text leaves the composer.</summary>
    [RelayCommand]
    private void CancelCompose()
    {
        // Edits put text in the composer, replies keep what was typed
        if (Editing is not null) Composer = "";
        ReplyingTo = null;
        Editing = null;
    }

    /// <summary>React to <paramref name="message"/> with <paramref name="key"/>, usually an emoji; reacting again with the same key does nothing.</summary>
    public async Task ReactAsync(MessageViewModel message, string key)
    {
        // Once per key, through the outbox
        if (SelectedRoom is not { IsInvite: false } room || !message.CanAnswer) return;
        if (message.ReactionSenders.TryGetValue(key, out HashSet<string>? senders) && senders.Contains(connection.UserId)) return;
        await connection.Outbox.EnqueueAsync(room.RoomId, EventTypes.Reaction, MatrixClient.CreateReactionContent(message.EventId!, key));
    }

    /// <summary>Delete own <paramref name="message"/> for everyone.</summary>
    public Task DeleteAsync(MessageViewModel message) =>
        SelectedRoom is { IsInvite: false } room && message.CanDelete ? RunAsync("Deleting the message", client => client.RedactAsync(room.RoomId, message.EventId!)) : Task.CompletedTask;

    /// <summary>Reasons offered when reporting a message, a user or a room.</summary>
    public static IReadOnlyList<string> ReportReasons { get; } = ["Spam", "Harassment or hate", "Sexual or violent content", "Illegal content", "Other"];

    /// <summary>Report <paramref name="message"/> of the open room to the admins of the user's server.</summary>
    public async Task ReportMessageAsync(MessageViewModel message, string reason)
    {
        // Sent messages of the open room only
        if (SelectedRoom is not { IsInvite: false } room || message.EventId is null || message.IsPending) return;
        if (await RunAsync("Reporting the message", client => client.ReportEventAsync(room.RoomId, message.EventId, reason))) ui.Post(() => Notice = "Reported to the admins of your server. You can also block the sender from the message's menu.");
    }

    /// <summary>Report the user <paramref name="userId"/> to the admins of the user's server.</summary>
    public async Task ReportUserAsync(string userId, string reason)
    {
        // Anyone but oneself
        if (userId == connection.UserId) return;
        if (await RunAsync("Reporting the user", client => client.ReportUserAsync(userId, reason))) ui.Post(() => Notice = $"Reported {userId} to the admins of your server.");
    }

    /// <summary>Report the open room to the admins of the user's server.</summary>
    public Task ReportRoomAsync(string reason) => SelectedRoom is RoomItemViewModel room ? ReportRoomAsync(room, reason) : Task.CompletedTask;

    /// <summary>Report <paramref name="room"/> to the admins of the user's server.</summary>
    public async Task ReportRoomAsync(RoomItemViewModel room, string reason)
    {
        // Any room in the list
        if (await RunAsync("Reporting the room", client => client.ReportRoomAsync(room.RoomId, reason))) ui.Post(() => Notice = $"Reported {room.Name} to the admins of your server.");
    }

    /// <summary>Block <paramref name="userId"/>, so their messages and invites no longer show, or unblock them.</summary>
    public async Task SetBlockedAsync(string userId, bool blocked)
    {
        // Anyone but oneself; the server keeps the list, as m.ignored_user_list
        if (userId == connection.UserId) return;
        if (!await RunAsync(blocked ? "Blocking" : "Unblocking", client => connection.Processor.SetIgnoredAsync(client, userId, blocked))) return;

        // Show the room again without, or with, their messages
        ui.Post(() =>
        {
            Notice = blocked ? $"Blocked {userId}: their messages and invites no longer show. Unblock them in Security and recovery." : $"Unblocked {userId}.";
            LoadMembers();
            if (SelectedRoom is RoomItemViewModel open) _ = OpenRoomAsync(open.RoomId);
        });
    }

    /// <summary>Return whether there is something to send.</summary>
    private bool CanSend() => Composer.Trim().Length > 0 && CanCompose;

    /// <summary>Send a file to the open room as an image, video, audio or file message by its type; files to encrypted rooms are encrypted first.</summary>
    /// <param name="content">File contents.</param>
    /// <param name="fileName">Name shown to others.</param>
    public async Task SendFileAsync(Stream content, string fileName)
    {
        // Assert a joined room, and encryption when the room needs it
        if (SelectedRoom is not { IsInvite: false } room) return;
        if (room.IsEncrypted && !connection.Crypto.Available)
        {
            Notice = $"This room is encrypted, but encryption is not available on this device: {connection.Crypto.UnavailableReason}";
            return;
        }

        // Encrypted rooms: encrypt and upload, then send through the outbox, which encrypts the message
        string mimeType = MimeTypes.FromPath(fileName);
        Notice = $"Sending {fileName}…";
        bool sent = await RunAsync($"Sending {fileName}", async client =>
        {
            if (!room.IsEncrypted)
            {
                await client.SendFileAsync(room.RoomId, content, fileName, mimeType);
                return;
            }
            using MemoryStream buffer = new();
            await content.CopyToAsync(buffer);
            byte[] bytes = buffer.ToArray();
            EncryptedFile file = await client.UploadEncryptedAsync(bytes);
            await connection.Outbox.EnqueueAsync(room.RoomId, EventTypes.RoomMessage, MatrixClient.CreateFileContent(file, fileName, mimeType, bytes.Length));
        });
        if (sent) Notice = "";
    }

    /// <summary>Download an attachment into the media cache, decrypting it when needed; return its local path, or null when it failed.</summary>
    public async Task<string?> DownloadAsync(MessageViewModel message)
    {
        // Assert an attachment and a login
        if (message.Content is not MessageContent content || !connection.Session.WhenReady.IsCompletedSuccessfully) return null;
        try
        {
            StoredMedia media = await connection.Media.GetAsync(await connection.Session.WhenReady, content);
            // Return cached file
            return connection.Media.GetFullPath(media);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Notice = $"Downloading {message.FileName} failed: {exception.Message}";
            return null;
        }
    }

    /// <summary>Download an attachment and return a copy under its own file name, so the system knows which app opens it; null when it failed.</summary>
    public async Task<string?> DownloadForOpeningAsync(MessageViewModel message)
    {
        // Cached files have no extension; the copy sits next to them, as private as the cache
        if (await DownloadAsync(message) is not string path) return null;
        string name = string.Concat((Path.GetFileName(message.FileName ?? "") is { Length: > 0 } fileName ? fileName : "attachment").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string folder = Path.Combine(Path.GetDirectoryName(path)!, "open", Path.GetFileName(path));
        string copy = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        if (!File.Exists(copy)) File.Copy(path, copy);
        // Return copy
        return copy;
    }

    /// <summary>Load older messages of the open room: from the database first, then from the server.</summary>
    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        // Assert open room and no load running
        if (SelectedRoom is not RoomItemViewModel room || IsLoadingHistory) return;
        IsLoadingHistory = true;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                RoomLines older = await connection.Store.GetRoomLinesAsync(room.RoomId, MoreLines, oldestLine);
                if (SelectedRoom != room) return;
                if (older.LineCount > 0)
                {
                    oldestLine = older.OldestLine;
                    connection.Processor.Read(store => { if (store?.TryGetRoom(room.RoomId, out Room? state) == true) Messages.Prepend(state, older.Events); return 0; });
                    LoadThumbnails();
                    return;
                }
                if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
                IReadOnlyList<ClientEvent> fetched = await connection.Processor.BackfillAsync(await connection.Session.WhenReady, room.RoomId, PageSize);
                if (fetched.Count == 0) return;
            }
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException)
        {
            Notice = $"Loading history failed: {exception.Message}";
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    /// <summary>Go back to the room list on narrow screens.</summary>
    [RelayCommand]
    private void Back() => SelectedRoom = null;

    /// <summary>Go back one step, as Android's back button does: close the panel on top, else leave the open room on narrow screens; return false when there is nothing to go back from, so the app may close.</summary>
    public bool GoBack()
    {
        // An action sheet, from its choices to its top level and then closed; then the panels over the screen; a verification in progress stays until it ends or is cancelled
        if (ActionSheet is not null)
        {
            if (!ActionSheet.GoBack()) ActionSheet = null;
            return true;
        }
        if (Verification is { IsFinished: true })
        {
            Verification = null;
            return true;
        }
        if (RoomDirectory is not null)
        {
            RoomDirectory = null;
            return true;
        }
        if (Security is not null)
        {
            Security = null;
            return true;
        }
        if (IsSettingsOpen && Settings?.AudioTest is not null)
        {
            Settings.CloseAudioTest();
            return true;
        }
        if (IsSettingsOpen && Settings?.Licenses is not null)
        {
            Settings.Licenses = null;
            return true;
        }
        if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
            return true;
        }
        if (!IsNarrow || SelectedRoom is null) return false;
        SelectedRoom = null;
        // Done
        return true;
    }

    /// <summary>Accept the open invite.</summary>
    [RelayCommand]
    private Task AcceptInviteAsync() => SelectedRoom is RoomItemViewModel room ? AcceptAsync(room) : Task.CompletedTask;

    /// <summary>Accept the invite to <paramref name="room"/>.</summary>
    public async Task AcceptAsync(RoomItemViewModel room)
    {
        // Direct chats are recorded as such, as the inviter did
        string? directWith = connection.Processor.Read(store => store?.TryGetRoom(room.RoomId, out Room? invited) == true && invited.IsDirect ? invited.InviterUserId : null);
        await RunAsync($"Joining {room.Name}", async client =>
        {
            await client.JoinRoomAsync(room.RoomId);
            if (directWith is not null) await client.MarkDirectRoomAsync(directWith, room.RoomId);
        });
    }

    /// <summary>Decline the open invite.</summary>
    [RelayCommand]
    private Task DeclineInviteAsync() => SelectedRoom is RoomItemViewModel room ? DeclineAsync(room) : Task.CompletedTask;

    /// <summary>Decline the invite to <paramref name="room"/>, and close it when it is open.</summary>
    public async Task DeclineAsync(RoomItemViewModel room)
    {
        // Leave, then close the room
        await RunAsync($"Declining {room.Name}", client => client.LeaveRoomAsync(room.RoomId));
        if (SelectedRoom == room) SelectedRoom = null;
    }

    /// <summary>Join the room in <see cref="JoinTarget"/>, given by alias, ID or link and optionally followed by servers to join through; start a direct chat with the user in it, or create a private room by that name; open the room when it arrives.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTarget))]
    private async Task OpenTargetAsync()
    {
        // A room, user or link, then servers to join through; a valid user ID for chats
        string[] words = JoinTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        MatrixLink? link = MatrixLink.TryParse(words[0], out MatrixLink? parsed) ? parsed with { Via = [.. parsed.Via.Concat(words.Skip(1)).Distinct()] } : null;
        string target = link?.Target ?? JoinTarget.Trim();
        if (target[0] == '@' && !Uniform.Matrix.Protocol.Core.UserId.TryParse(target, out _))
        {
            Notice = $"{target} is not a user ID; they look like @name:server.";
            return;
        }

        // Private rooms and chats are encrypted when this device can
        string? roomId = null;
        bool encrypted = connection.Crypto.Available;
        IReadOnlyList<InitialStateEvent>? state = encrypted ? [new InitialStateEvent { Type = EventTypes.RoomEncryption, Content = new EncryptionContent { Algorithm = EncryptionAlgorithms.Megolm } }] : null;
        bool done = target[0] switch
        {
            '#' or '!' => await JoinLinkAsync(link ?? new MatrixLink { Target = target }),
            '@' => await RunAsync($"Starting a chat with {target}", async client => roomId = await client.CreateDirectRoomAsync(target, encrypted)),
            _ => await RunAsync($"Creating {target}", async client => roomId = await client.CreateRoomAsync(new CreateRoomRequest { Name = target, Preset = RoomPresets.PrivateChat, InitialState = state })),
        };
        if (!done) return;

        // Open it now, or when sync brings it
        JoinTarget = "";
        if (roomId is not null) openWhenArrived = roomId;
        RefreshRooms();
    }

    /// <summary>Join the room of <paramref name="link"/> through its servers and open it when it arrives, or open it at once when joined already; return whether it was joined or opened.</summary>
    private async Task<bool> JoinLinkAsync(MatrixLink link)
    {
        // Rooms joined already open at once
        string? joinedId = connection.Processor.Read(store => store?.Rooms.Values.FirstOrDefault(room => room.Membership == Memberships.Join && (room.RoomId == link.Target || room.CanonicalAlias == link.Target))?.RoomId);
        if (joinedId is not null && roomsById.TryGetValue(joinedId, out RoomItemViewModel? open))
        {
            SelectedRoom = open;
            return true;
        }

        // Join, then open when sync brings it
        string? roomId = null;
        if (!await RunAsync($"Joining {link.Target}", async client => roomId = await client.JoinRoomAsync(link.Target, link.Via.Count > 0 ? link.Via : null))) return false;
        openWhenArrived = roomId;
        RefreshRooms();
        // Return joined
        return true;
    }

    /// <summary>Return whether the room box has text.</summary>
    private bool CanOpenTarget() => JoinTarget.Trim().Length > 1;

    /// <summary>Fill <see cref="Members"/> and <see cref="TopicDraft"/> from the open room, e.g. when its menu opens.</summary>
    [RelayCommand]
    private void LoadMembers()
    {
        // Joined first, then invited, by name
        Members.Clear();
        if (SelectedRoom is not RoomItemViewModel open) return;
        TopicDraft = open.Topic ?? "";
        bool canVerify = connection.Crypto.Available;
        List<MemberItem> members = connection.Processor.Read(store => store?.TryGetRoom(open.RoomId, out Room? room) == true
            ? room.Members.Values
                .Where(member => member.Membership is Memberships.Join or Memberships.Invite)
                .OrderBy(member => member.Membership == Memberships.Join ? 0 : 1)
                .ThenBy(member => room.GetMemberDisplayName(member.UserId), StringComparer.CurrentCultureIgnoreCase)
                .Select(member => new MemberItem(member.UserId, room.GetMemberDisplayName(member.UserId), member.Membership == Memberships.Invite, canVerify && member.UserId != connection.UserId, canVerify && member.UserId != connection.UserId && connection.Crypto.IsUserVerified(member.UserId), member.UserId == connection.UserId, store.IgnoredUsers.Contains(member.UserId)))
                .ToList()
            : []);
        foreach (MemberItem member in members) Members.Add(member);
    }

    /// <summary>Ask <paramref name="member"/> to verify each other by comparing emoji; the request shows in the verification panel.</summary>
    public async Task VerifyMemberAsync(MemberItem member)
    {
        // Their keys first, then the request: in their direct chat as a room message, as Element and the spec do it, else to all their devices
        try
        {
            await connection.Crypto.TrackUserAsync(member.UserId);
            string? direct = connection.Processor.Read(store => SelectedRoom is { IsInvite: false } open && store?.TryGetRoom(open.RoomId, out Room? room) == true && room.IsDirect && room.DirectUserId == member.UserId ? open.RoomId : null);
            if (direct is not null && connection.Session.WhenReady.IsCompletedSuccessfully)
            {
                MatrixClient client = await connection.Session.WhenReady;
                await connection.Crypto.RequestVerificationInRoomAsync(direct, member.UserId, (content, token) => connection.Outbox.SendNowAsync(client, direct, EventTypes.RoomMessage, content, token));
                return;
            }
            await connection.Crypto.RequestVerificationAsync(member.UserId, deviceId: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error tell
            Notice = $"Verifying {member.Name} failed: {exception.Message}";
        }
    }

    /// <summary>Invite the user in <see cref="InviteTarget"/> to the open room.</summary>
    [RelayCommand(CanExecute = nameof(CanInvite))]
    private async Task InviteAsync()
    {
        // Assert a joined room
        if (SelectedRoom is not { IsInvite: false } room) return;
        string target = InviteTarget.Trim();
        if (!await RunAsync($"Inviting {target}", client => client.InviteAsync(room.RoomId, target))) return;
        InviteTarget = "";
        Notice = $"Invited {target}.";
    }

    /// <summary>Return whether the invite box has a user ID.</summary>
    private bool CanInvite() => Uniform.Matrix.Protocol.Core.UserId.TryParse(InviteTarget.Trim(), out _);

    /// <summary>Set the open room's topic to <see cref="TopicDraft"/>.</summary>
    [RelayCommand]
    private Task SetTopicAsync() => SelectedRoom is { IsInvite: false } room ? RunAsync("Setting the topic", client => client.SetRoomTopicAsync(room.RoomId, TopicDraft.Trim())) : Task.CompletedTask;

    /// <summary>Leave the open room and close it.</summary>
    [RelayCommand]
    private Task LeaveRoomAsync() => SelectedRoom is RoomItemViewModel room ? LeaveAsync(room) : Task.CompletedTask;

    /// <summary>Leave <paramref name="room"/>, and close it when it is open.</summary>
    public async Task LeaveAsync(RoomItemViewModel room)
    {
        // Leave, then back to the list
        if (!await RunAsync($"Leaving {room.Name}", client => client.LeaveRoomAsync(room.RoomId))) return;
        if (SelectedRoom == room) SelectedRoom = null;
    }

    /// <summary>Show the security panel with the account's current state.</summary>
    [RelayCommand]
    private async Task OpenSecurityAsync()
    {
        // Recovery shows the stored messages again
        SecurityViewModel security = new(connection, async () =>
        {
            await connection.Processor.RetryStoredEncryptedAsync();
            if (SelectedRoom is RoomItemViewModel room) await OpenRoomAsync(room.RoomId);
        }, async () =>
        {
            if (SelectedRoom is RoomItemViewModel room) await OpenRoomAsync(room.RoomId);
        }, signOut);
        Security = security;
        await security.RefreshAsync();
    }

    /// <summary>Show or hide the settings panel.</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        // Opened with what the system allows now
        IsSettingsOpen = Settings is not null && !IsSettingsOpen;
        if (IsSettingsOpen) Settings?.RefreshNotificationsAllowed();
    }

    /// <summary>Show the public room directory, of the server shown last.</summary>
    [RelayCommand]
    private async Task OpenDirectoryAsync()
    {
        // A fresh search of the last server
        DirectoryViewModel directory = new(connection, ui, directoryServer, async link =>
        {
            if (!await JoinLinkAsync(link)) return false;
            RoomDirectory = null;
            return true;
        }, roomId => roomsById.TryGetValue(roomId, out RoomItemViewModel? joined) && !joined.IsInvite);
        directory.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DirectoryViewModel.Server)) directoryServer = directory.Server.Trim(); };
        RoomDirectory = directory;
        await directory.SearchAsync();
    }

    /// <summary>Hide the room directory.</summary>
    [RelayCommand]
    private void CloseDirectory() => RoomDirectory = null;

    /// <summary>Hide the security panel, and see whether the suggestion above the rooms still applies.</summary>
    [RelayCommand]
    private void CloseSecurity()
    {
        // Closed, then checked again
        Security = null;
        _ = CheckSecurityAsync();
    }

    /// <summary>A suggestion above the rooms to set up recovery, or to verify this device, until it is done or dismissed; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecuritySuggestion))]
    public partial string SecuritySuggestion { get; set; } = "";

    /// <summary>Whether there is a security suggestion.</summary>
    public bool HasSecuritySuggestion => SecuritySuggestion.Length > 0;

    /// <summary>Check the account's cross-signing: suggest setting up recovery on a new account, which signs this device so that others see it verified, or verifying this device on an account that has it.</summary>
    internal async Task CheckSecurityAsync()
    {
        // Nothing to suggest without encryption, or once dismissed
        if (securitySuggestionDismissed || !connection.Crypto.Available)
        {
            SecuritySuggestion = "";
            return;
        }
        SecurityStatus status;
        try
        {
            status = await connection.Crypto.GetSecurityStatusAsync();
        }
        catch (Exception ex) when (ex is MatrixException or HttpRequestException or InvalidOperationException)
        {
            connection.Logger.LogInformation("Security status not available: {Error}", ex.Message);
            return;
        }
        // Done
        SecuritySuggestion = !status.CrossSigningSetUp ? "Set up recovery, so that others see this device as verified, and a recovery key unlocks your messages on new devices."
            : !status.ThisDeviceCrossSigned ? "Verify this device with another of yours, or with your recovery key, so that others see it as verified and it reads your older messages."
            : "";
    }

    /// <summary>Hide the security suggestion until the app starts again.</summary>
    [RelayCommand]
    private void DismissSecuritySuggestion()
    {
        // For this session
        securitySuggestionDismissed = true;
        SecuritySuggestion = "";
    }

    /// <summary>Follow a verification from the engine's background thread on the UI thread.</summary>
    private void OnVerificationChanged(Verification verification)
    {
        // Read the state now, as it may change again before the post runs
        VerificationState state = verification.State;
        ui.Post(() => ShowVerification(verification, state));
    }

    /// <summary>Show a verification's new state; a new request replaces a finished one.</summary>
    private void ShowVerification(Verification verification, VerificationState state)
    {
        // The open one
        if (Verification?.Verification.TransactionId == verification.TransactionId)
        {
            Verification.Update(state);
            if (state == VerificationState.Done && Security is SecurityViewModel security) _ = security.RefreshAsync();
            if (state == VerificationState.Done) _ = CheckSecurityAsync();
            return;
        }

        // A new one, unless another is still going
        if (state is VerificationState.Done or VerificationState.Cancelled || Verification is { IsFinished: false }) return;
        VerificationViewModel shown = new(connection, verification, () => { if (Verification?.Verification == verification) Verification = null; });
        shown.Update(state);
        Verification = shown;
    }

    /// <summary>Sign out: log out this device and return to the sign-in form.</summary>
    [RelayCommand]
    private Task SignOutAsync() => signOut();

    /// <summary>Clear the notice.</summary>
    [RelayCommand]
    private void DismissNotice() => Notice = "";

    /// <summary>Hide the suspension banner and allow trying again; the next refusal shows it again.</summary>
    [RelayCommand]
    private void DismissSuspension() => IsSuspended = false;

    /// <summary>Return whether <paramref name="error"/> is the server refusing a suspended account, and show the banner if so.</summary>
    private bool NoteSuspension(string? error)
    {
        // The error code anywhere in the message
        if (error?.Contains(ErrorCodes.UserSuspended, StringComparison.Ordinal) != true) return false;
        ui.Post(() => IsSuspended = true);
        // Done
        return true;
    }

    /// <summary>Show a room's newest stored lines and unsent messages, at least as many as are shown when the room opens again, e.g. after decryption; fetch more when few are stored.</summary>
    private async Task OpenRoomAsync(string roomId)
    {
        // Stored lines, with the edits, reactions and deletions on them
        int lines = openedRoomId == roomId ? Math.Max(InitialLines, Messages.Items.Count(m => !m.IsOutbox)) : InitialLines;
        RoomLines page = await connection.Store.GetRoomLinesAsync(roomId, lines);
        if (SelectedRoom?.RoomId != roomId) return;
        openedRoomId = roomId;
        oldestLine = page.OldestLine;
        connection.Processor.Read(store => { if (store?.TryGetRoom(roomId, out Room? room) == true) { Messages.Reset(room, page.Events); RefreshTyping(room); } return 0; });
        await RefreshPendingAsync(roomId);
        LoadThumbnails();

        // More from the server, then mark read
        if (page.LineCount < 30) await LoadOlderAsync();
        MarkRead(roomId);
        RefreshRooms();
    }

    /// <summary>Read sync changes until stopped and apply each on the UI thread.</summary>
    private async Task ConsumeChangesAsync()
    {
        // One reader of the change stream
        try
        {
            await foreach (SyncChanges changes in connection.Processor.Changes.ReadAllAsync(stopping.Token))
            {
                ui.Post(() => ApplySafely(changes));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
    }

    /// <summary>Apply one sync's changes, logging failures instead of ending the app.</summary>
    private void ApplySafely(SyncChanges changes)
    {
        // A bad event must not stop the screen from updating
        try
        {
            ApplyChanges(changes);
        }
        catch (Exception exception)
        {
            connection.Logger.LogError(exception, "Showing sync changes failed");
        }
    }

    /// <summary>Update the room list, the open room and typing from one sync.</summary>
    internal void ApplyChanges(SyncChanges changes)
    {
        // Notifications compare with the rooms known before
        if (notifier is not null) Notify(changes);

        // Rooms, then the open one, whose member count may decide calls
        RefreshRooms();
        OnPropertyChanged(nameof(CanCallRoom));
        OnPropertyChanged(nameof(CanVideoCallRoom));
        if (!singlePane && !IsNarrow && SelectedRoom is null && Rooms.Count > 0) SelectedRoom = Rooms[0];
        if (SelectedRoom is not RoomItemViewModel open) return;

        // New messages of the open room
        TimelineBatch? batch = changes.Timeline.FirstOrDefault(b => b.Room.RoomId == open.RoomId);
        if (batch is not null)
        {
            connection.Processor.Read(_ => { Messages.Append(batch.Room, batch.Events); return 0; });
            _ = RefreshPendingAsync(open.RoomId);
            LoadThumbnails();
            MarkRead(open.RoomId);
        }

        // Typing and the open room's own state, e.g. a joined invite
        if (batch is not null || changes.TypingChanged.Any(r => r.RoomId == open.RoomId)) connection.Processor.Read(store => { if (store?.TryGetRoom(open.RoomId, out Room? room) == true) RefreshTyping(room); return 0; });
        if (changes.Joined.Any(r => r.RoomId == open.RoomId) && batch is null) _ = OpenRoomAsync(open.RoomId);
    }

    /// <summary>Notify of new messages in direct chats, mentions and new invites, as the settings choose, unless the window has the focus and shows that room.</summary>
    private void Notify(SyncChanges changes)
    {
        // Collect under the lock, at most a few per sync; history and the first sync's invites are old news
        List<(string Title, string Body)> notifications = connection.Processor.Read(store =>
        {
            List<(string Title, string Body)> found = [];
            foreach (TimelineBatch batch in changes.Timeline)
            {
                if (IsActive && batch.Room.RoomId == SelectedRoom?.RoomId) continue;
                foreach (ClientEvent clientEvent in batch.Events.Where(e => e.Type == EventTypes.RoomMessage && e.Sender != connection.UserId && e.Timestamp >= startedAt.AddMinutes(-1)))
                {
                    if (store?.IgnoredUsers.Contains(clientEvent.Sender) == true || !clientEvent.TryGetContent(out MessageContent? content) || content.RelatesTo?.RelType == RelationTypes.Replace) continue;
                    bool mentioned = content.Mentions?.UserIds?.Contains(connection.UserId) == true || content.Mentions?.Room == true;
                    if (!(batch.Room.IsDirect && Settings?.NotifyDirectMessages != false || mentioned && Settings?.NotifyMentions != false)) continue;
                    string sender = batch.Room.GetMemberDisplayName(clientEvent.Sender);
                    string text = EventText.GetText(clientEvent, batch.Room.GetMemberDisplayName) ?? "";
                    found.Add((batch.Room.IsDirect ? sender : $"{sender} in {batch.Room.DisplayName}", content.MessageType == MessageTypes.Emote ? $"* {sender} {text}" : text));
                }
            }
            foreach (Room invited in changes.Invited.Where(r => Settings?.NotifyInvites != false && notifiedChanges && !roomsById.ContainsKey(r.RoomId) && store?.IgnoredUsers.Contains(r.InviterUserId ?? "") != true))
                found.Add(("Invitation", $"{(invited.InviterUserId is string inviter ? invited.GetMemberDisplayName(inviter) : "Someone")} invited you to {invited.DisplayName}"));
            return found.Take(3).ToList();
        });
        notifiedChanges = true;

        // Show without waiting
        foreach ((string title, string body) in notifications) _ = ShowNotificationAsync(title, MessageList.Snippet(body, 200));
    }

    /// <summary>Show one notification; a failing notifier is logged, not shown.</summary>
    private async Task ShowNotificationAsync(string title, string body)
    {
        // Platform notifier
        try
        {
            await notifier!.ShowAsync(title, body, stopping.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            connection.Logger.LogInformation("Notification failed: {Error}", exception.Message);
        }
    }

    /// <summary>Rebuild the room list from room state, keeping items and the selection.</summary>
    private void RefreshRooms()
    {
        // Snapshot in list order under the lock: invites, favourites, others and low priority, each the most recently active first
        string? openId = SelectedRoom?.RoomId;
        DateTimeOffset now = DateTimeOffset.Now;
        List<RoomItemViewModel> ordered = connection.Processor.Read(store => store is null ? [] : store.Rooms.Values
            .Where(room => room.Membership is Memberships.Join or Memberships.Invite)
            .Select(room =>
            {
                if (!roomsById.TryGetValue(room.RoomId, out RoomItemViewModel? item)) roomsById[room.RoomId] = item = new RoomItemViewModel(room.RoomId);
                item.Update(room, room.RoomId == openId || IsMarkedRead(room), connection.UserId, now);
                return (Room: room, Item: item);
            })
            .OrderBy(entry => entry.Room.Membership == Memberships.Invite ? 0 : entry.Room.Tags.ContainsKey(RoomTags.ServerNotice) ? 1 : entry.Room.Tags.ContainsKey(RoomTags.Favourite) ? 2 : entry.Room.Tags.ContainsKey(RoomTags.LowPriority) ? 4 : 3)
            .ThenByDescending(entry => entry.Item.LastActivity)
            .ThenBy(entry => entry.Room.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.Item)
            .ToList());

        // Move items into place without losing the selection
        for (int i = 0; i < ordered.Count; i++)
        {
            int current = Rooms.IndexOf(ordered[i]);
            if (current == i) continue;
            if (current < 0) Rooms.Insert(i, ordered[i]);
            else Rooms.Move(current, i);
        }
        while (Rooms.Count > ordered.Count) Rooms.RemoveAt(Rooms.Count - 1);
        UnreadRooms = Rooms.Count(r => r.HasUnread || r.IsInvite);
        foreach (RoomItemViewModel item in ordered.Where(i => i.AvatarUri is not null && i.Avatar is null && avatarsLoading.Add(i.RoomId))) _ = LoadAvatarAsync(item, item.AvatarUri!);
        if (SelectedRoom is not null && !Rooms.Contains(SelectedRoom)) SelectedRoom = null;

        // A room just joined or created opens
        if (openWhenArrived is null || !roomsById.TryGetValue(openWhenArrived, out RoomItemViewModel? arrived) || !Rooms.Contains(arrived)) return;
        openWhenArrived = null;
        SelectedRoom = arrived;
    }

    /// <summary>Show who else is typing in the open room; call under the room store's lock.</summary>
    private void RefreshTyping(Room room)
    {
        // Others only
        List<string> names = [.. room.TypingUserIds.Where(u => u != connection.UserId).Select(room.GetMemberDisplayName)];
        TypingText = names.Count switch
        {
            0 => "",
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            _ => "Several people are typing…",
        };
    }

    /// <summary>Show the open room's unsent messages; only the newest of overlapping loads is shown, so a slow early load cannot bring back a sent message.</summary>
    private async Task RefreshPendingAsync(string roomId)
    {
        // Load, then show if still open and still the newest load
        int version = Interlocked.Increment(ref pendingVersion);
        IReadOnlyList<OutboxMessage> unsent = await connection.Store.GetUnsentAsync(roomId);
        foreach (OutboxMessage message in unsent.Where(m => m.State == OutboxState.Failed)) NoteSuspension(message.LastError);
        ui.Post(() =>
        {
            if (SelectedRoom?.RoomId != roomId || version != Volatile.Read(ref pendingVersion)) return;
            connection.Processor.Read(store => { if (store?.TryGetRoom(roomId, out Room? room) == true) Messages.SetPending(room, unsent); return 0; });
        });
    }

    /// <summary>Return whether the read marker sent for <paramref name="room"/> is at its newest event, so it has nothing unread even before the server's count says so; call under the room store's lock.</summary>
    private bool IsMarkedRead(Room room) => readMarkers.TryGetValue(room.RoomId, out string? marked) && marked == room.Timeline.LastOrDefault(e => e.EventId is not null)?.EventId;

    /// <summary>Send a read marker for the newest event of the open room, once per new event; a failed one is sent again with the next.</summary>
    private void MarkRead(string roomId)
    {
        // The room's newest event, also one not shown, such as a call, which the server counts as unread too
        string? newest = connection.Processor.Read(store => store?.TryGetRoom(roomId, out Room? room) == true ? room.Timeline.LastOrDefault(e => e.EventId is not null)?.EventId : null) ?? Messages.NewestEventId;
        if (newest is null) return;
        if (readMarkers.TryGetValue(roomId, out string? marked) && marked == newest) return;
        readMarkers[roomId] = newest;
        _ = SendReadMarkerAsync(roomId, newest);
    }

    /// <summary>Send the read marker, forgetting it when it failed, so that the next refresh sends it again.</summary>
    private async Task SendReadMarkerAsync(string roomId, string eventId)
    {
        // Forget on failure, on the UI thread that owns the markers
        if (await RunAsync("Read marker", client => client.SetReadMarkersAsync(roomId, eventId, eventId), report: false)) return;
        ui.Post(() => { if (readMarkers.TryGetValue(roomId, out string? marked) && marked == eventId) readMarkers.Remove(roomId); });
    }

    /// <summary>Load thumbnails of shown images that have none yet.</summary>
    private void LoadThumbnails()
    {
        // Each image once
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return;
        foreach (MessageViewModel message in Messages.Items.Where(m => m.ImageUri is not null && m.Thumbnail is null).ToList())
        {
            _ = LoadThumbnailAsync(message);
        }
    }

    /// <summary>Width that images are decoded to for the message list, in pixels.</summary>
    private const int ThumbnailWidth = 800;

    /// <summary>Download a thumbnail through the media cache and show it: the server's thumbnail, or for encrypted images, which the server cannot read, and images it cannot make thumbnails of, the whole file decoded small.</summary>
    private async Task LoadThumbnailAsync(MessageViewModel message)
    {
        // Cached file, decoded off the UI thread
        try
        {
            MatrixClient client = await connection.Session.WhenReady;
            StoredMedia media;
            if (message.Content is { File: not null } encrypted) media = await connection.Media.GetAsync(client, encrypted);
            else
            {
                try
                {
                    media = await connection.Media.GetThumbnailAsync(client, MxcUri.Parse(message.ImageUri!), ThumbnailWidth, 600);
                }
                catch (MatrixException) when (message.Content is MessageContent content)
                {
                    media = await connection.Media.GetAsync(client, content);
                }
            }
            string path = connection.Media.GetFullPath(media);
            int? width = message.Content?.Info?.Width;
            Bitmap bitmap = await Task.Run(() => DecodeSmall(path, width));
            ui.Post(() => message.Thumbnail = bitmap);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            connection.Logger.LogInformation("Thumbnail of {Uri} failed: {Error}", message.ImageUri, exception.Message);
        }
    }

    /// <summary>Decode the image at <paramref name="path"/> at most <see cref="ThumbnailWidth"/> pixels wide, so large photos take little memory; <paramref name="width"/> is its width when the message tells.</summary>
    private static Bitmap DecodeSmall(string path, int? width)
    {
        // Scaled while decoding when known to be wide
        using FileStream file = File.OpenRead(path);
        if (width > ThumbnailWidth) return Bitmap.DecodeToWidth(file, ThumbnailWidth);
        Bitmap bitmap = new(file);
        if (bitmap.PixelSize.Width <= ThumbnailWidth) return bitmap;

        // Wider than the message said: decode again, scaled
        bitmap.Dispose();
        file.Position = 0;
        // Return scaled
        return Bitmap.DecodeToWidth(file, ThumbnailWidth);
    }

    /// <summary>Download a room's avatar as a small thumbnail and show it in the list.</summary>
    private async Task LoadAvatarAsync(RoomItemViewModel room, string uri)
    {
        // A failed one is tried again on a later refresh
        Bitmap? bitmap = await GetPictureAsync(uri);
        ui.Post(() =>
        {
            if (bitmap is not null && room.AvatarUri == uri) room.Avatar = bitmap;
            avatarsLoading.Remove(room.RoomId);
        });
    }

    /// <summary>Show the pictures of the senders of <paramref name="messages"/>, each picture loaded once per session; call on the UI thread.</summary>
    private void ShowSenderAvatars(IEnumerable<MessageViewModel> messages)
    {
        // Loaded or loading pictures are shared by every message with them
        foreach (MessageViewModel message in messages)
        {
            if (message.SenderAvatarUri is not string uri || message.SenderAvatar is not null) continue;
            Task<Bitmap?> loading = GetPictureAsync(uri);
            if (loading.IsCompletedSuccessfully) message.SenderAvatar = loading.Result;
            else _ = loading.ContinueWith(done => ui.Post(() => message.SenderAvatar = done.Result), TaskScheduler.Default);
        }
    }

    /// <summary>Return the picture of <paramref name="uri"/>, loading it once for every room and message showing it; a failed load is forgotten, so a later use tries again. Call on the UI thread.</summary>
    private Task<Bitmap?> GetPictureAsync(string uri)
    {
        // Shared while loading or loaded
        if (pictures.TryGetValue(uri, out Task<Bitmap?>? known)) return known;
        Task<Bitmap?> loading = LoadThumbnailAsync(uri);
        pictures[uri] = loading;
        _ = loading.ContinueWith(done => { if (done.Result is null) ui.Post(() => pictures.Remove(uri)); }, TaskScheduler.Default);
        // Return the load
        return loading;
    }

    /// <summary>Return a small picture of <paramref name="uri"/> from the media cache, decoded off the UI thread; null when it cannot be had.</summary>
    private async Task<Bitmap?> LoadThumbnailAsync(string uri)
    {
        // Cached file, decoded off the UI thread
        try
        {
            MatrixClient client = await connection.Session.WhenReady;
            StoredMedia media = await connection.Media.GetThumbnailAsync(client, MxcUri.Parse(uri), 96, 96);
            string path = connection.Media.GetFullPath(media);
            // Return picture
            return await Task.Run(() =>
            {
                using FileStream stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 96);
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // On error none
            connection.Logger.LogInformation("Avatar {Uri} failed: {Error}", uri, exception.Message);
            return null;
        }
    }

    /// <summary>Show the engine's connection state.</summary>
    private void OnConnectionChanged(ConnectionState state, string? message) => ui.Post(() =>
    {
        // Status, and the security check once connected the first time
        StatusText = Describe(state, message);
        if (state == ConnectionState.Connected && !securityChecked)
        {
            securityChecked = true;
            _ = CheckSecurityAsync();
        }
    });

    /// <summary>Show the open room's outbox again when a message there was sent or failed.</summary>
    private void OnOutboxChanged(string roomId) => ui.Post(() => { if (SelectedRoom?.RoomId == roomId) _ = RefreshPendingAsync(roomId); });

    /// <summary>Show the room list and the open room again once late keys decrypted their messages.</summary>
    private void OnEventsDecrypted(string roomId) => ui.Post(() =>
    {
        // The list shows latest messages; the open room shows them all
        RefreshRooms();
        if (SelectedRoom?.RoomId == roomId) _ = OpenRoomAsync(roomId);
    });

    /// <summary>Run a server call with the logged-in client; failures become the notice unless <paramref name="report"/> is false. Return whether it succeeded.</summary>
    private async Task<bool> RunAsync(string what, Func<MatrixClient, Task> action, bool report = true)
    {
        // Logged in only
        if (!connection.Session.WhenReady.IsCompletedSuccessfully) return false;
        try
        {
            await action(await connection.Session.WhenReady);
            return true;
        }
        catch (Exception exception) when (exception is MatrixException or HttpRequestException or TaskCanceledException)
        {
            connection.Logger.LogInformation("{What} failed: {Error}", what, exception.Message);
            if (NoteSuspension(exception.Message)) return false;
            if (report) ui.Post(() => Notice = $"{what} failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Return the status line for a connection state; empty when connected.</summary>
    internal static string Describe(ConnectionState state, string? message) => state switch
    {
        ConnectionState.Connected => "",
        ConnectionState.Connecting => message is null ? "Connecting…" : $"{message}…",
        ConnectionState.Retrying => message is null ? "Connection lost, retrying…" : $"Retrying: {message}",
        ConnectionState.Failed => $"Stopped: {message}",
        _ => message ?? "",
    };
}
