namespace Uniform.Matrix.Client.State;

using System.Diagnostics.CodeAnalysis;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Client-side state of all rooms, built by applying sync responses; not thread-safe.</summary>
/// <example><code><![CDATA[
/// RoomStore store = new(client.UserId!);
/// await foreach (SyncResponse sync in client.SyncLoopAsync(ct: ct))
/// {
///     SyncChanges changes = store.Apply(sync);
///     foreach (TimelineBatch batch in changes.Timeline)
///         foreach (ClientEvent ev in batch.Events)
///             if (ev.TryGetContent(out MessageContent? message)) Console.WriteLine($"[{batch.Room.DisplayName}] {batch.Room.GetMemberDisplayName(ev.Sender)}: {message.Body}");
/// }
/// ]]></code></example>
public sealed class RoomStore
{
    private readonly Dictionary<string, Room> rooms = [];
    private readonly Dictionary<string, MatrixEvent> accountData = [];
    private readonly Dictionary<string, PresenceContent> presence = [];
    private HashSet<string> ignoredUsers = [];

    /// <summary>Create an empty store for the logged-in <paramref name="ownUserId"/>.</summary>
    public RoomStore(string ownUserId)
    {
        // Store own user
        OwnUserId = ownUserId;
    }

    /// <summary>Logged-in user, excluded from computed room names.</summary>
    public string OwnUserId { get; }
    /// <summary>Most events kept per room by sync; backfilled events come on top.</summary>
    public int MaxTimelineLength { get; init; } = 500;
    /// <summary><see cref="SyncResponse.NextBatch"/> of the last applied sync; stores it to resume syncing.</summary>
    public string? NextBatch { get; private set; }
    /// <summary>Known rooms of every membership keyed by room ID.</summary>
    public IReadOnlyDictionary<string, Room> Rooms => rooms;
    /// <summary>Global account data keyed by type.</summary>
    public IReadOnlyDictionary<string, MatrixEvent> AccountData => accountData;
    /// <summary>Latest presence keyed by user ID.</summary>
    public IReadOnlyDictionary<string, PresenceContent> Presence => presence;
    /// <summary>Ignored user IDs from <see cref="EventTypes.IgnoredUserList"/>.</summary>
    public IReadOnlySet<string> IgnoredUsers => ignoredUsers;

    /// <summary>Return a known room by ID.</summary>
    public bool TryGetRoom(string roomId, [NotNullWhen(true)] out Room? room) => rooms.TryGetValue(roomId, out room);

    /// <summary>Apply <paramref name="sync"/> and return what changed.</summary>
    public SyncChanges Apply(SyncResponse sync)
    {
        // Collect changes
        List<Room> joined = [], invited = [], left = [], typing = [], unread = [];
        List<TimelineBatch> timelines = [];

        // Global data first; direct rooms affect rooms below
        foreach (MatrixEvent ev in sync.AccountData.Events) ApplyAccountData(ev);
        foreach (PresenceEvent ev in sync.Presence.Events) if (ev.TryGetContent(out PresenceContent? content)) presence[ev.Sender] = content;

        // Joined rooms
        foreach ((string roomId, JoinedRoom update) in sync.Rooms.Join)
        {
            Room room = GetOrCreate(roomId);
            if (room.Membership != Memberships.Join) joined.Add(room);
            if (room.Membership == Memberships.Invite || room.Membership == Memberships.Knock) room.ClearState();
            room.Membership = Memberships.Join;
            room.ApplySummary(update.Summary);
            ApplyTimeline(room, update.State, update.Timeline, timelines);
            foreach (MatrixEvent ev in update.Ephemeral.Events) if (ApplyEphemeral(room, ev)) typing.Add(room);
            foreach (MatrixEvent ev in update.AccountData.Events) room.SetAccountData(ev);
            if (ApplyUnread(room, update.UnreadNotifications)) unread.Add(room);
        }

        // Invites
        foreach ((string roomId, InvitedRoom update) in sync.Rooms.Invite)
        {
            Room room = GetOrCreate(roomId);
            if (room.Membership != Memberships.Invite) invited.Add(room);
            room.ClearState();
            room.Membership = Memberships.Invite;
            foreach (StrippedStateEvent ev in update.InviteState.Events) room.SetState(ev, ev.StateKey);
        }

        // Knocks
        foreach ((string roomId, KnockedRoom update) in sync.Rooms.Knock)
        {
            Room room = GetOrCreate(roomId);
            room.ClearState();
            room.Membership = Memberships.Knock;
            foreach (StrippedStateEvent ev in update.KnockState.Events) room.SetState(ev, ev.StateKey);
        }

        // Left rooms
        foreach ((string roomId, LeftRoom update) in sync.Rooms.Leave)
        {
            Room room = GetOrCreate(roomId);
            if (room.Membership != Memberships.Leave) left.Add(room);
            room.Membership = Memberships.Leave;
            ApplyTimeline(room, update.State, update.Timeline, timelines);
            foreach (MatrixEvent ev in update.AccountData.Events) room.SetAccountData(ev);
        }

        // Remember position
        NextBatch = sync.NextBatch;
        // Return changes
        return new SyncChanges { Joined = joined, Invited = invited, Left = left, Timeline = timelines, TypingChanged = typing, UnreadChanged = unread };
    }

    /// <summary>Load up to <paramref name="limit"/> events older than <see cref="Room.Timeline"/> and prepend them; return them oldest first.</summary>
    /// <remarks>Fetches and applies in one call; when other threads apply syncs, fetch with <see cref="MatrixClient.GetMessagesAsync"/> and apply with <see cref="ApplyHistory"/> under their lock instead.</remarks>
    public async Task<IReadOnlyList<ClientEvent>> BackfillAsync(MatrixClient client, Room room, int limit = 50, CancellationToken cancellationToken = default)
    {
        // Assert history left; without a token there is no way back
        if (!CanBackfill(room)) return [];
        // Fetch older page, then apply
        MessagesResponse page = await client.GetMessagesAsync(room.RoomId, room.HistoryToken, Direction.Backward, limit, cancellationToken: cancellationToken);
        // Return loaded events
        return ApplyHistory(room, page);
    }

    /// <summary>Whether <paramref name="room"/> has a token for older history; clears <see cref="Room.HasMoreHistory"/> when it never will.</summary>
    public static bool CanBackfill(Room room)
    {
        // Without a token there is no way back
        if (room.HistoryToken is null) room.HasMoreHistory = false;
        // Return whether to fetch
        return room.HasMoreHistory && room.HistoryToken is not null;
    }

    /// <summary>Prepend a backward <paramref name="page"/> fetched from <see cref="Room.HistoryToken"/>; return its events oldest first.</summary>
    public IReadOnlyList<ClientEvent> ApplyHistory(Room room, MessagesResponse page)
    {
        // Learn senders the server lazy-loaded, without overriding current state
        foreach (ClientEvent ev in page.State) room.SetMemberIfUnknown(ev);
        foreach (ClientEvent ev in page.Chunk) room.SetMemberIfUnknown(ev);

        // Prepend oldest first
        List<ClientEvent> older = [.. page.Chunk.Reverse()];
        room.Prepend(older);
        room.HistoryToken = page.End;
        room.HasMoreHistory = page.End is not null && page.Chunk.Count > 0;
        // Return loaded events
        return older;
    }

    /// <summary>Load the full member list, which sync omits when lazy-loading members.</summary>
    public async Task LoadMembersAsync(MatrixClient client, Room room, CancellationToken cancellationToken = default)
    {
        // Fetch, then apply
        IReadOnlyList<ClientEvent> memberEvents = await client.GetMembersAsync(room.RoomId, cancellationToken: cancellationToken);
        ApplyMembers(room, memberEvents);
    }

    /// <summary>Apply current member events, e.g. from <see cref="MatrixClient.GetMembersAsync"/>.</summary>
    public void ApplyMembers(Room room, IReadOnlyList<ClientEvent> memberEvents)
    {
        // Apply as current state
        foreach (ClientEvent ev in memberEvents) if (ev.StateKey is not null) room.SetState(ev, ev.StateKey);
    }

    /// <summary>Return the room with <paramref name="roomId"/>, creating it when unknown.</summary>
    private Room GetOrCreate(string roomId)
    {
        // Existing room
        if (rooms.TryGetValue(roomId, out Room? room)) return room;

        // New room
        room = new Room(roomId, OwnUserId) { DirectUserId = FindDirectUser(roomId) };
        rooms[roomId] = room;
        // Return room
        return room;
    }

    /// <summary>Apply state before the timeline, then the timeline; clear history after a gap.</summary>
    private void ApplyTimeline(Room room, EventList<ClientEvent> stateBefore, Timeline timeline, List<TimelineBatch> batches)
    {
        // Gap: older local events no longer connect
        if (timeline.Limited) room.ClearTimeline();
        if (timeline.Limited || (room.Timeline.Count == 0 && room.HistoryToken is null)) room.HistoryToken = timeline.PrevBatch;
        if (timeline.Limited) room.HasMoreHistory = true;

        // State, then events which may change state again
        foreach (ClientEvent ev in stateBefore.Events) if (ev.StateKey is not null) room.SetState(ev, ev.StateKey);
        foreach (ClientEvent ev in timeline.Events) room.Append(ev, MaxTimelineLength);

        // Report
        if (timeline.Events.Count == 0 && !timeline.Limited) return;
        batches.Add(new TimelineBatch { Room = room, Events = timeline.Events, Limited = timeline.Limited });
    }

    /// <summary>Apply a typing or receipt event; return whether typing users changed.</summary>
    private static bool ApplyEphemeral(Room room, MatrixEvent matrixEvent)
    {
        // Receipts
        if (matrixEvent.Type == EventTypes.Receipt) return ApplyReceipts(room, matrixEvent);

        // Typing
        if (matrixEvent.Type != EventTypes.Typing || !matrixEvent.TryGetContent(out TypingContent? typing)) return false;
        if (room.TypingUserIds.SequenceEqual(typing.UserIds)) return false;
        room.TypingUserIds = typing.UserIds;
        // Done
        return true;
    }

    /// <summary>Apply read receipts of <paramref name="matrixEvent"/>; return false as typing is unaffected.</summary>
    private static bool ApplyReceipts(Room room, MatrixEvent matrixEvent)
    {
        // Assert content
        if (!matrixEvent.TryGetContent(out ReceiptContent? receipts)) return false;
        // Keep read receipts only
        foreach ((string eventId, string type, string userId, ReceiptInfo _) in receipts.Flatten())
        {
            if (type is not (ReceiptTypes.Read or ReceiptTypes.ReadPrivate)) continue;
            room.SetReadReceipt(userId, eventId);
        }
        // Done
        return false;
    }

    /// <summary>Apply unread counts; return whether they changed.</summary>
    private static bool ApplyUnread(Room room, UnreadNotificationCounts? counts)
    {
        // Assert counts given and changed
        if (counts is null) return false;
        int notifications = counts.NotificationCount ?? room.NotificationCount;
        int highlights = counts.HighlightCount ?? room.HighlightCount;
        if (notifications == room.NotificationCount && highlights == room.HighlightCount) return false;

        // Update
        room.NotificationCount = notifications;
        room.HighlightCount = highlights;
        // Done
        return true;
    }

    /// <summary>Store global account data and update direct rooms and ignored users.</summary>
    private void ApplyAccountData(MatrixEvent matrixEvent)
    {
        // Store
        accountData[matrixEvent.Type] = matrixEvent;

        // Ignored users
        if (matrixEvent.Type == EventTypes.IgnoredUserList && matrixEvent.TryGetContent(out IgnoredUserListContent? ignored)) ignoredUsers = [.. ignored.IgnoredUsers.Keys];

        // Direct rooms
        if (matrixEvent.Type != EventTypes.Direct) return;
        foreach (Room room in rooms.Values) room.DirectUserId = FindDirectUser(room.RoomId);
    }

    /// <summary>Return the user <paramref name="roomId"/> is a direct room with per <see cref="EventTypes.Direct"/>, or null.</summary>
    private string? FindDirectUser(string roomId)
    {
        // Read direct map
        if (!accountData.TryGetValue(EventTypes.Direct, out MatrixEvent? ev) || !ev.TryGetContent(out DirectContent? direct)) return null;
        // Return first user listing the room
        return direct.FirstOrDefault(d => d.Value.Contains(roomId)).Key;
    }
}
