namespace Uniform.Chat.Avalonia.ViewModels;

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Core;

// The details and actions of a message or a room, for the action sheet and the context menu
public sealed partial class ChatViewModel
{
    /// <summary>Reactions offered first, in the sheet's row and the menu.</summary>
    public static IReadOnlyList<string> QuickReactions { get; } = ["👍", "❤️", "😂", "🎉", "😮", "😢"];

    /// <summary>The action sheet shown over the screen, or null.</summary>
    [ObservableProperty]
    public partial ActionSheetViewModel? ActionSheet { get; set; }

    /// <summary>Copies text to the clipboard; set by the view, which has the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Copy the image file at a path, under a file name, to the clipboard; set by the view.</summary>
    public Func<string, string, Task>? CopyImageToClipboard { get; set; }

    /// <summary>Raised when an action wants the message box focused, such as Reply and Edit.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>Return the details and actions of <paramref name="message"/> in the open room: reply, react, edit, copy, delete, report and block, as they apply.</summary>
    public ActionSheetViewModel CreateMessageActions(MessageViewModel message)
    {
        // Who sent it and when, and how it went
        RoomItemViewModel? room = SelectedRoom;
        List<SheetDetail> details = [new("From", message.SenderName == message.SenderId ? message.SenderId : $"{message.SenderName} ({message.SenderId})")];
        DateTimeOffset sent = message.Timestamp.ToLocalTime();
        details.Add(new("Sent", sent.ToString("d", CultureInfo.CurrentCulture) + " " + sent.ToString("HH:mm:ss", CultureInfo.CurrentCulture)));
        if (message.IsPending) details.Add(new("State", message.SendState));
        if (message.IsEdited) details.Add(new("Edited", "Yes"));
        if (room?.IsEncrypted == true) details.Add(new("Encryption", "End-to-end encrypted"));
        if (message.EventId is not null) details.Add(new("Event ID", message.EventId));

        // What applies to this message
        List<SheetAction> actions = [];
        if (message.CanAnswer) actions.Add(new("Reply", () => { StartReply(message); ComposerFocusRequested?.Invoke(); }));
        if (message.CanEdit) actions.Add(new("Edit", () => { StartEdit(message); ComposerFocusRequested?.Invoke(); }));
        if (message.ImageUri is not null) actions.Add(new("Copy image", () => _ = CopyImageAsync(message)));
        if (message.Text.Length > 0) actions.Add(new("Copy text", () => Copy(message.Text)));
        if (room is not null && message.EventId is not null && !message.IsPending) actions.Add(new("Copy link", () => Copy(Link(room.RoomId, message.EventId))));
        if (message.CanDelete) actions.Add(new("Delete", () => _ = DeleteAsync(message), IsDanger: true));

        // Others' messages can be reported, and their sender reported or blocked
        if (!message.IsOwn && !message.IsInfo && message.EventId is not null && !message.IsPending)
        {
            string senderId = message.SenderId;
            actions.Add(new("Report message", null, Reasons(reason => _ = ReportMessageAsync(message, reason))));
            actions.Add(new("Report sender", null, Reasons(reason => _ = ReportUserAsync(senderId, reason))));
            actions.Add(new("Block sender", () => _ = SetBlockedAsync(senderId, blocked: true), IsDanger: true));
        }
        // Return sheet, with reactions on what can be answered
        return new ActionSheetViewModel(message.SenderName, details, actions, message.CanAnswer ? QuickReactions : [], message.CanAnswer ? key => _ = ReactAsync(message, key) : null, CloseActionSheet);
    }

    /// <summary>Return the details and actions of <paramref name="room"/> in the list: join or decline an invite; else mark read, favourite, low priority, copy its link, report and leave.</summary>
    public ActionSheetViewModel CreateRoomActions(RoomItemViewModel room)
    {
        // Its address, members and encryption, from the stored state
        (string? alias, int members, int invited) = connection.Processor.Read(store => store?.TryGetRoom(room.RoomId, out Room? state) == true ? (state.CanonicalAlias, state.JoinedMemberCount, state.InvitedMemberCount) : (null, 0, 0));
        List<SheetDetail> details = [];
        if (alias is not null) details.Add(new("Address", alias));
        if (!string.IsNullOrWhiteSpace(room.Topic)) details.Add(new("Topic", room.Topic));
        if (!room.IsInvite) details.Add(new("Members", invited > 0 ? $"{members}, {invited} invited" : members.ToString(CultureInfo.CurrentCulture)));
        if (!room.IsInvite) details.Add(new("Encryption", room.IsEncrypted ? "End-to-end encrypted" : "Not encrypted"));
        if (room.Unread > 0) details.Add(new("Unread", room.Unread.ToString(CultureInfo.CurrentCulture)));
        details.Add(new("Room ID", room.RoomId));

        // An invite is answered first
        List<SheetAction> actions = [];
        if (room.IsInvite)
        {
            actions.Add(new("Join", () => _ = AcceptAsync(room)));
            actions.Add(new("Decline", () => _ = DeclineAsync(room), IsDanger: true));
            actions.Add(new("Report room", null, Reasons(reason => _ = ReportRoomAsync(room, reason))));
            return new ActionSheetViewModel(room.Name, details, actions, [], null, CloseActionSheet);
        }

        // Joined rooms: reading, sorting, sharing, reporting and leaving
        if (room.Unread > 0) actions.Add(new("Mark as read", () => MarkRoomRead(room)));
        actions.Add(room.IsFavourite ? new("Remove from favourites", () => _ = SetTagAsync(room, RoomTags.Favourite, on: false)) : new("Favourite", () => _ = SetTagAsync(room, RoomTags.Favourite, on: true)));
        actions.Add(room.IsLowPriority ? new("Remove low priority", () => _ = SetTagAsync(room, RoomTags.LowPriority, on: false)) : new("Low priority", () => _ = SetTagAsync(room, RoomTags.LowPriority, on: true)));
        actions.Add(new("Copy link", () => Copy(Link(alias ?? room.RoomId, eventId: null))));
        if (!room.IsServerNotice) actions.Add(new("Report room", null, Reasons(reason => _ = ReportRoomAsync(room, reason))));
        actions.Add(new("Leave room", () => _ = LeaveAsync(room), IsDanger: true));
        // Return sheet
        return new ActionSheetViewModel(room.Name, details, actions, [], null, CloseActionSheet);
    }

    /// <summary>Mark everything in <paramref name="room"/> read, without opening it.</summary>
    public void MarkRoomRead(RoomItemViewModel room)
    {
        // Shown read at once; the server's count follows with the next sync
        room.Unread = 0;
        room.IsHighlighted = false;
        MarkRead(room.RoomId);
    }

    /// <summary>Add or remove the tag <paramref name="tag"/> of <paramref name="room"/>; favourite and low priority exclude each other, as in other clients.</summary>
    public Task SetTagAsync(RoomItemViewModel room, string tag, bool on) => RunAsync(on ? "Tagging the room" : "Untagging the room", async client =>
    {
        // The other of the two goes when one is set
        if (!on)
        {
            await client.DeleteTagAsync(room.RoomId, tag);
            return;
        }
        string other = tag == RoomTags.Favourite ? RoomTags.LowPriority : tag == RoomTags.LowPriority ? RoomTags.Favourite : "";
        if (other.Length > 0 && (other == RoomTags.Favourite ? room.IsFavourite : room.IsLowPriority)) await client.DeleteTagAsync(room.RoomId, other);
        await client.SetTagAsync(room.RoomId, tag);
    });

    /// <summary>Close the action sheet.</summary>
    private void CloseActionSheet() => ActionSheet = null;

    /// <summary>Copy <paramref name="text"/> to the clipboard, when the view gave one.</summary>
    private void Copy(string text)
    {
        // Through the view
        if (CopyToClipboard is not Func<string, Task> copy) return;
        _ = copy(text);
    }

    /// <summary>Copy an image message's picture to the clipboard, downloaded and decrypted first when needed.</summary>
    private async Task CopyImageAsync(MessageViewModel message)
    {
        // The picture's file, through the view
        if (CopyImageToClipboard is not Func<string, string, Task> copy) return;
        try
        {
            if (await DownloadAsync(message) is not string path) return;
            await copy(path, message.FileName ?? "image.png");
            Notice = "Image copied";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // On a picture that could not be loaded or copied
            Notice = $"Copying the image failed: {exception.Message}";
        }
    }

    /// <summary>Return a matrix.to link to <paramref name="target"/>, a room's alias or ID, and to its event <paramref name="eventId"/>; an ID gets the user's server to join through.</summary>
    private string Link(string target, string? eventId)
    {
        // Servers only for IDs, which do not tell where the room is
        IReadOnlyList<string> via = target.StartsWith('!') && Uniform.Matrix.Protocol.Core.UserId.TryParse(connection.UserId, out UserId user) ? [user.ServerName] : [];
        // Return link
        return new MatrixLink { Target = target, EventId = eventId, Via = via }.ToString();
    }

    /// <summary>Return the report reasons as choices that run <paramref name="report"/>.</summary>
    private static IReadOnlyList<SheetAction> Reasons(Action<string> report) => [.. ReportReasons.Select(reason => new SheetAction(reason, () => report(reason)))];
}
