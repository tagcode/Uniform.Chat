namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using global::Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;

/// <summary>One room in the room list: name, unread count, and whether it is an invite, a direct chat or encrypted.</summary>
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

    /// <summary>Whether messages are end-to-end encrypted.</summary>
    [ObservableProperty]
    public partial bool IsEncrypted { get; set; }

    /// <summary>Topic, or null.</summary>
    [ObservableProperty]
    public partial string? Topic { get; set; }

    /// <summary>Initial of the name, shown as the room's avatar.</summary>
    [ObservableProperty]
    public partial string Initial { get; set; } = "#";

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
    public void Update(Room room, bool isOpen)
    {
        // State shown in the list; the open room has nothing unread
        Name = room.DisplayName;
        Topic = room.Topic;
        IsInvite = room.Membership == Memberships.Invite;
        IsDirect = room.IsDirect;
        IsEncrypted = room.IsEncrypted;
        Unread = isOpen ? 0 : room.NotificationCount;
        IsHighlighted = !isOpen && room.HighlightCount > 0;
        if (room.AvatarUrl != AvatarUri) Avatar = null;
        AvatarUri = room.AvatarUrl;
        string letters = Name.TrimStart('#', '!', '@', ' ');
        Initial = letters.Length > 0 ? char.ToUpperInvariant(letters[0]).ToString() : "#";
    }
}

/// <summary>A member of the open room, for its member list.</summary>
/// <param name="UserId">User ID.</param>
/// <param name="Name">Display name in the room.</param>
/// <param name="IsInvited">Whether the user is invited but not joined.</param>
/// <param name="CanVerify">Whether they can be verified from here: someone else, with encryption on.</param>
public sealed record MemberItem(string UserId, string Name, bool IsInvited, bool CanVerify)
{
    /// <summary>Get the second line, e.g. <c>@bob:example.org · invited</c>.</summary>
    public string Details => IsInvited ? $"{UserId} · invited" : UserId;
}
