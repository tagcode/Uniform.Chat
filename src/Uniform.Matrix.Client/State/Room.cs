namespace Uniform.Matrix.Client.State;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Uniform.Matrix.Protocol.Core;

/// <summary>Client-side state of one room, kept up to date by <see cref="RoomStore"/>; not thread-safe.</summary>
/// <remarks>A mutable class rather than a record, as identity matters and value equality over live state would be misleading.</remarks>
public sealed class Room
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Dictionary<(string Type, string StateKey), MatrixEvent> state = [];
    private readonly Dictionary<string, RoomMemberInfo> members = [];
    private readonly Dictionary<string, int> activeNameCounts = [];
    private readonly List<ClientEvent> timeline = [];
    private readonly Dictionary<string, MatrixEvent> accountData = [];
    private readonly Dictionary<string, string> readReceipts = [];
    private readonly string ownUserId;

    /// <summary>Create an empty room; <paramref name="ownUserId"/> is excluded from computed names.</summary>
    internal Room(string roomId, string ownUserId)
    {
        // Store identity
        RoomId = roomId;
        this.ownUserId = ownUserId;
    }

    /// <summary>Room ID.</summary>
    public string RoomId { get; }
    /// <summary>Own membership, see <see cref="Memberships"/>.</summary>
    public string Membership { get; internal set; } = Memberships.Leave;
    /// <summary>Name from <see cref="EventTypes.RoomName"/>; null when unset.</summary>
    public string? Name => GetStateContent<NameContent>(EventTypes.RoomName)?.Name is { Length: > 0 } name ? name : null;
    /// <summary>Power levels from <see cref="EventTypes.RoomPowerLevels"/>; null when the room has none.</summary>
    public PowerLevelsContent? PowerLevels => GetStateContent<PowerLevelsContent>(EventTypes.RoomPowerLevels);
    /// <summary>Topic from <see cref="EventTypes.RoomTopic"/>.</summary>
    public string? Topic => GetStateContent<TopicContent>(EventTypes.RoomTopic)?.Topic;
    /// <summary>Avatar <c>mxc://</c> URI: the room's own from <see cref="EventTypes.RoomAvatar"/>, or the other person's in a direct chat; null when none.</summary>
    public string? AvatarUrl => GetStateContent<RoomAvatarContent>(EventTypes.RoomAvatar)?.Url is { Length: > 0 } url ? url
        : DirectUserId is string other && members.TryGetValue(other, out RoomMemberInfo? member) && member.AvatarUrl is { Length: > 0 } memberUrl ? memberUrl : null;
    /// <summary>Main alias from <see cref="EventTypes.RoomCanonicalAlias"/>.</summary>
    public string? CanonicalAlias => GetStateContent<CanonicalAliasContent>(EventTypes.RoomCanonicalAlias)?.Alias is { Length: > 0 } alias ? alias : null;
    /// <summary>Whether the room is end-to-end encrypted; such messages arrive as <see cref="EventTypes.RoomEncrypted"/>.</summary>
    public bool IsEncrypted => state.ContainsKey((EventTypes.RoomEncryption, ""));
    /// <summary>Other user of a direct-message room per <see cref="EventTypes.Direct"/>; null for other rooms.</summary>
    public string? DirectUserId { get; internal set; }
    /// <summary>Whether this is a direct-message room, including direct invites.</summary>
    public bool IsDirect => DirectUserId is not null || (Membership == Memberships.Invite && GetStateContent<MemberContent>(EventTypes.RoomMember, ownUserId)?.IsDirect == true);
    /// <summary>Users who invited us, i.e. sender of our invite; null unless invited.</summary>
    public string? InviterUserId => Membership == Memberships.Invite ? GetSender(GetState(EventTypes.RoomMember, ownUserId)) : null;

    /// <summary>Heroes from the latest <see cref="RoomSummary"/>.</summary>
    public IReadOnlyList<string>? Heroes { get; private set; }
    /// <summary>Joined members per summary, or counted from known members.</summary>
    public int JoinedMemberCount => summaryJoined ?? members.Values.Count(m => m.Membership == Memberships.Join);
    /// <summary>Invited members per summary, or counted from known members.</summary>
    public int InvitedMemberCount => summaryInvited ?? members.Values.Count(m => m.Membership == Memberships.Invite);
    private int? summaryJoined;
    private int? summaryInvited;

    /// <summary>Known members keyed by user ID; partial when the server lazy-loads members, see <see cref="RoomStore.LoadMembersAsync"/>.</summary>
    public IReadOnlyDictionary<string, RoomMemberInfo> Members => members;
    /// <summary>Recent events in chronological order, at most <see cref="RoomStore.MaxTimelineLength"/> plus backfilled ones.</summary>
    public IReadOnlyList<ClientEvent> Timeline => timeline;
    /// <summary>Token for loading events older than <see cref="Timeline"/>; null when not known yet or history is exhausted.</summary>
    public string? HistoryToken { get; internal set; }
    /// <summary>Whether older history may exist beyond <see cref="Timeline"/>.</summary>
    public bool HasMoreHistory { get; internal set; } = true;

    /// <summary>Unread notifying messages per server.</summary>
    public int NotificationCount { get; internal set; }
    /// <summary>Unread highlights such as mentions per server.</summary>
    public int HighlightCount { get; internal set; }
    /// <summary>Users currently typing.</summary>
    public IReadOnlyList<string> TypingUserIds { get; internal set; } = [];
    /// <summary>Latest read receipt event ID per user.</summary>
    public IReadOnlyDictionary<string, string> ReadReceipts => readReceipts;
    /// <summary>Room account data keyed by type.</summary>
    public IReadOnlyDictionary<string, MatrixEvent> AccountData => accountData;
    /// <summary>Event up to which the user has read everything, from <see cref="EventTypes.FullyRead"/>.</summary>
    public string? FullyReadEventId => GetAccountDataContent<FullyReadContent>(EventTypes.FullyRead)?.EventId;
    /// <summary>Tags from <see cref="EventTypes.Tag"/>, see <see cref="RoomTags"/>.</summary>
    public IReadOnlyDictionary<string, TagInfo> Tags => GetAccountDataContent<TagContent>(EventTypes.Tag)?.Tags ?? new Dictionary<string, TagInfo>();

    /// <summary>Name to show for the room, computed per <see href="https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-room">spec</see>: name, alias, then member names.</summary>
    public string DisplayName
    {
        get
        {
            // Explicit name or alias
            if (Name is string name) return name;
            if (CanonicalAlias is string alias) return alias;

            // Member names
            List<string> names = GetHeroIds().Select(GetMemberDisplayName).ToList();
            int total = JoinedMemberCount + InvitedMemberCount;
            if (names.Count == 0) return "Empty Room";
            if (total <= 1) return $"Empty Room (was {JoinNames(names)})";
            int others = total - 1 - names.Count;
            // Return names
            return others > 0 ? $"{string.Join(", ", names)} and {others} others" : JoinNames(names);
        }
    }

    /// <summary>Return the current state event of <paramref name="type"/> and <paramref name="stateKey"/>, or null.</summary>
    public MatrixEvent? GetState(string type, string stateKey = "") => state.GetValueOrDefault((type, stateKey));

    /// <summary>Return current state events of <paramref name="type"/> keyed by state key.</summary>
    public IReadOnlyDictionary<string, MatrixEvent> GetStates(string type) => state.Where(s => s.Key.Type == type).ToDictionary(s => s.Key.StateKey, s => s.Value);

    /// <summary>Return the content of a state event as <typeparamref name="T"/>, or null when unset or malformed.</summary>
    public T? GetStateContent<T>(string type, string stateKey = "") where T : class => GetState(type, stateKey) is MatrixEvent ev && ev.TryGetContent(out T? content) ? content : null;

    /// <summary>Return the content of room account data as <typeparamref name="T"/>, or null.</summary>
    public T? GetAccountDataContent<T>(string type) where T : class => accountData.GetValueOrDefault(type) is MatrixEvent ev && ev.TryGetContent(out T? content) ? content : null;

    /// <summary>Return a timeline event by ID.</summary>
    public bool TryGetEvent(string eventId, [NotNullWhen(true)] out ClientEvent? clientEvent)
    {
        // Search from newest, where lookups usually hit
        clientEvent = null;
        for (int i = timeline.Count - 1; i >= 0; i--)
        {
            if (timeline[i].EventId != eventId) continue;
            clientEvent = timeline[i];
            return true;
        }
        // Not found
        return false;
    }

    /// <summary>Creators with unlimited power, from room version 12: the create event's sender and its additional creators; empty in older rooms.</summary>
    public IReadOnlyCollection<string> Creators
    {
        get
        {
            // Only rooms whose version gives creators power
            if (GetStateContent<CreateContent>(EventTypes.RoomCreate) is not { HasPowerfulCreators: true } create) return [];
            // Return the sender and the additional creators
            return [.. new[] { GetSender(GetState(EventTypes.RoomCreate)) }.Concat(create.AdditionalCreators ?? []).OfType<string>().Distinct()];
        }
    }

    /// <summary>Return the power level of <paramref name="userId"/>: <see cref="PowerLevelsContent.Creator"/> for <see cref="Creators"/>; without power levels, 100 for the room's creator and 0 for others.</summary>
    public long GetPowerLevel(string userId)
    {
        // Creators above all levels
        if (Creators.Contains(userId)) return PowerLevelsContent.Creator;
        // Return the listed level
        return PowerLevels is PowerLevelsContent levels ? levels.GetUserLevel(userId) : userId == GetSender(GetState(EventTypes.RoomCreate)) ? PowerLevelsContent.Admin : 0;
    }

    /// <summary>Name to show for <paramref name="userId"/> per <see href="https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-user">spec</see>: display name, with the user ID appended when another member shares it.</summary>
    public string GetMemberDisplayName(string userId)
    {
        // Fall back to user ID
        if (!members.TryGetValue(userId, out RoomMemberInfo? member) || string.IsNullOrWhiteSpace(member.DisplayName)) return userId;
        // Disambiguate shared names
        if (activeNameCounts.GetValueOrDefault(member.DisplayName) > 1) return $"{member.DisplayName} ({userId})";
        // Return display name
        return member.DisplayName;
    }

    /// <summary>Set a state event, updating <see cref="Members"/> for member events.</summary>
    internal void SetState(MatrixEvent matrixEvent, string stateKey)
    {
        // Store
        state[(matrixEvent.Type, stateKey)] = matrixEvent;
        // Track members
        if (matrixEvent.Type == EventTypes.RoomMember) SetMember(stateKey, matrixEvent);
    }

    /// <summary>Set a member only when not known yet; used for historical member events, which are older than current state.</summary>
    internal void SetMemberIfUnknown(ClientEvent clientEvent)
    {
        // Assert unknown member event
        if (clientEvent.Type != EventTypes.RoomMember || clientEvent.StateKey is null || members.ContainsKey(clientEvent.StateKey)) return;
        // Set
        SetState(clientEvent, clientEvent.StateKey);
    }

    /// <summary>Replace <paramref name="userId"/>'s member info and keep <see cref="activeNameCounts"/> in step.</summary>
    private void SetMember(string userId, MatrixEvent matrixEvent)
    {
        // Remove previous name from counts
        if (members.TryGetValue(userId, out RoomMemberInfo? old)) CountName(old, -1);

        // Store new info
        matrixEvent.TryGetContent(out MemberContent? content);
        RoomMemberInfo member = new() { UserId = userId, Membership = content?.Membership ?? Memberships.Leave, DisplayName = content?.DisplayName, AvatarUrl = content?.AvatarUrl };
        members[userId] = member;
        CountName(member, +1);
    }

    /// <summary>Add <paramref name="delta"/> to the count of <paramref name="member"/>'s display name among active members.</summary>
    private void CountName(RoomMemberInfo member, int delta)
    {
        // Only active members with a name count
        if (!member.IsActive || string.IsNullOrWhiteSpace(member.DisplayName)) return;
        // Update count
        int count = activeNameCounts.GetValueOrDefault(member.DisplayName) + delta;
        if (count <= 0) activeNameCounts.Remove(member.DisplayName);
        else activeNameCounts[member.DisplayName] = count;
    }

    /// <summary>Append <paramref name="clientEvent"/>, applying its state or redaction, and trim to <paramref name="maxLength"/>.</summary>
    internal void Append(ClientEvent clientEvent, int maxLength)
    {
        // Apply effects
        if (clientEvent.StateKey is not null) SetState(clientEvent, clientEvent.StateKey);
        if (clientEvent.Type == EventTypes.RoomRedaction) Redact(clientEvent);

        // Append and trim oldest
        timeline.Add(clientEvent);
        if (timeline.Count > maxLength) timeline.RemoveRange(0, timeline.Count - maxLength);
    }

    /// <summary>Insert older events before <see cref="Timeline"/>; no state is applied as it is historical.</summary>
    internal void Prepend(IReadOnlyList<ClientEvent> olderFirst) => timeline.InsertRange(0, olderFirst);

    /// <summary>Clear <see cref="Timeline"/> after a gap.</summary>
    internal void ClearTimeline() => timeline.Clear();

    /// <summary>Clear all state, e.g. when stripped invite state is replaced by full state on join.</summary>
    internal void ClearState()
    {
        // Clear state and derived member data
        state.Clear();
        members.Clear();
        activeNameCounts.Clear();
    }

    /// <summary>Strip the content of the event targeted by <paramref name="redaction"/>, if it is in <see cref="Timeline"/>.</summary>
    private void Redact(ClientEvent redaction)
    {
        // Find target; room version 11 moved redacts into content
        string? targetId = redaction.Redacts ?? (redaction.TryGetContent(out RedactionContent? content) ? content.Redacts : null);
        if (targetId is null) return;
        int index = timeline.FindIndex(e => e.EventId == targetId);
        if (index < 0) return;

        // Replace with redacted copy
        ClientEvent target = timeline[index];
        timeline[index] = target with { Content = EmptyObject, Unsigned = (target.Unsigned ?? new UnsignedData()) with { RedactedBecause = redaction } };
    }

    /// <summary>Apply a <see cref="RoomSummary"/>; servers send only changed fields, so missing ones keep their value.</summary>
    internal void ApplySummary(RoomSummary? summary)
    {
        // Merge fields
        if (summary is null) return;
        if (summary.Heroes is not null) Heroes = summary.Heroes;
        if (summary.JoinedMemberCount is int joined) summaryJoined = joined;
        if (summary.InvitedMemberCount is int invited) summaryInvited = invited;
    }

    /// <summary>Set room account data.</summary>
    internal void SetAccountData(MatrixEvent matrixEvent) => accountData[matrixEvent.Type] = matrixEvent;

    /// <summary>Set the latest read receipt of <paramref name="userId"/>.</summary>
    internal void SetReadReceipt(string userId, string eventId) => readReceipts[userId] = eventId;

    /// <summary>Return users to name the room after: summary heroes, or up to five other active members by user ID.</summary>
    private IEnumerable<string> GetHeroIds()
    {
        // Prefer server heroes
        if (Heroes is { Count: > 0 } heroes) return heroes.Where(h => h != ownUserId);
        // Return computed heroes
        return members.Values.Where(m => m.IsActive && m.UserId != ownUserId).Select(m => m.UserId).Order(StringComparer.Ordinal).Take(5);
    }

    /// <summary>Join names as <c>A</c>, <c>A and B</c> or <c>A, B and C</c>.</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>Return the sender of a state event of either shape.</summary>
    private static string? GetSender(MatrixEvent? matrixEvent) => matrixEvent switch
    {
        ClientEvent c => c.Sender,
        StrippedStateEvent s => s.Sender,
        _ => null,
    };
}
