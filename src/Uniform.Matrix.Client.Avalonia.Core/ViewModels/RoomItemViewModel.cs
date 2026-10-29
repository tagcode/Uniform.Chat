namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using System.Globalization;
using global::Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;

/// <summary>One room in the room list: name, latest message, unread count, and whether it is an invite, a direct chat or encrypted.</summary>
public sealed partial class RoomItemViewModel(string roomId) : ViewModelBase
{
    /// <summary>Room ID.</summary>
    public string RoomId { get; } = roomId;

    /// <summary>Display name, computed as the spec says.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = roomId;

    /// <summary>Unread notifying messages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))]
    public partial int Unread { get; set; }

    /// <summary>Whether a message mentions the user.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    /// <summary>Whether the user is invited and has not answered.</summary>
    [ObservableProperty]
    public partial bool IsInvite { get; set; }

    /// <summary>Whether it is a direct chat.</summary>
    [ObservableProperty]
    public partial bool IsDirect { get; set; }

    /// <summary>Whether the room has two members, joined or invited: a direct chat even when not marked as one, which can be voice called.</summary>
    [ObservableProperty]
    public partial bool IsOneToOne { get; set; }

    /// <summary>Whether messages are end-to-end encrypted.</summary>
    [ObservableProperty]
    public partial bool IsEncrypted { get; set; }

    /// <summary>Topic, or null.</summary>
    [ObservableProperty]
    public partial string? Topic { get; set; }

    /// <summary>Initial of the name, shown as the room's avatar.</summary>
    [ObservableProperty]
    public partial string Initial { get; set; } = "#";

    /// <summary>Latest message as one line, e.g. <c>Bob: lunch?</c>, or empty when none is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastMessage))]
    public partial string LastMessage { get; set; } = "";

    /// <summary>Time of the latest message: the time today, the weekday this week, else the date.</summary>
    [ObservableProperty]
    public partial string LastTime { get; set; } = "";

    /// <summary>Time of the latest message, for ordering; the minimum when none is loaded.</summary>
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Whether <see cref="LastMessage"/> is shown in place of the topic.</summary>
    public bool HasLastMessage => LastMessage.Length > 0;

    /// <summary><c>mxc://</c> URI of the room's avatar, or null.</summary>
    public string? AvatarUri { get; private set; }

    /// <summary>Avatar image once loaded; the initial shows until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial Bitmap? Avatar { get; set; }

    /// <summary>Get whether <see cref="Avatar"/> is shown.</summary>
    public bool HasAvatar => Avatar is not null;

    /// <summary>Whether there are unread messages.</summary>
    public bool HasUnread => Unread > 0;

    /// <summary>Unread count as shown, e.g. <c>99+</c>.</summary>
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();

    /// <summary>Copy the room's current state; call under the room store's lock.</summary>
    /// <param name="room">The room.</param>
    /// <param name="isOpen">Whether it is the open room, which has nothing unread.</param>
    /// <param name="ownUserId">The user, whose messages show as from <c>You</c>.</param>
    /// <param name="now">Current time, for <see cref="LastTime"/>.</param>
    public void Update(Room room, bool isOpen, string ownUserId, DateTimeOffset now)
    {
        // State shown in the list; the open room has nothing unread
        Name = room.DisplayName;
        Topic = room.Topic;
        IsInvite = room.Membership == Memberships.Invite;
        IsDirect = room.IsDirect;
        IsOneToOne = room.JoinedMemberCount + room.InvitedMemberCount == 2;
        IsEncrypted = room.IsEncrypted;
        Unread = isOpen ? 0 : room.NotificationCount;
        IsHighlighted = !isOpen && room.HighlightCount > 0;
        if (room.AvatarUrl != AvatarUri) Avatar = null;
        AvatarUri = room.AvatarUrl;
        string letters = Name.TrimStart('#', '!', '@', ' ');
        Initial = letters.Length > 0 ? char.ToUpperInvariant(letters[0]).ToString() : "#";

        // Latest message: text, or a note for one that cannot be read yet
        for (int i = room.Timeline.Count - 1; i >= 0; i--)
        {
            ClientEvent latest = room.Timeline[i];
            string? text = latest.Type == EventTypes.RoomEncrypted ? "Encrypted message" : latest.Type == EventTypes.RoomMessage ? EventText.GetText(latest, room.GetMemberDisplayName) : null;
            if (text is null) continue;
            string sender = latest.Sender == ownUserId ? "You" : room.GetMemberDisplayName(latest.Sender);
            string line = text.ReplaceLineEndings(" ");
            LastMessage = room.IsDirect && latest.Sender != ownUserId ? line : $"{sender}: {line}";
            LastActivity = DateTimeOffset.FromUnixTimeMilliseconds(latest.OriginServerTs);
            LastTime = FormatTime(LastActivity.ToLocalTime(), now.ToLocalTime());
            return;
        }
        // Done without messages
        LastMessage = "";
        LastTime = "";
    }

    /// <summary>Return a message time as a room list shows it: the time today, in 24 hours as messages show it, the weekday within a week, else the date.</summary>
    internal static string FormatTime(DateTimeOffset time, DateTimeOffset now)
    {
        // Closer is shorter
        if (time.Date == now.Date) return time.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (now.Date - time.Date < TimeSpan.FromDays(7)) return time.ToString("ddd", CultureInfo.CurrentCulture);
        // Return the date
        return time.ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>A member of the open room, for its member list.</summary>
/// <param name="UserId">User ID.</param>
/// <param name="Name">Display name in the room.</param>
/// <param name="IsInvited">Whether the user is invited but not joined.</param>
/// <param name="CanVerify">Whether they can be verified from here: someone else, with encryption on.</param>
/// <param name="IsVerified">Whether this device has verified them.</param>
public sealed record MemberItem(string UserId, string Name, bool IsInvited, bool CanVerify, bool IsVerified = false)
{
    /// <summary>Get the second line, e.g. <c>@bob:example.org · invited</c> or <c>@bob:example.org · verified</c>.</summary>
    public string Details => string.Join(" · ", new[] { UserId, IsInvited ? "invited" : null, IsVerified ? "verified" : null }.OfType<string>());

    /// <summary>Whether to offer verifying: possible, and not done yet.</summary>
    public bool ShowVerify => CanVerify && !IsVerified;
}
