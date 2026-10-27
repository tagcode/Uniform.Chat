namespace Durum.Matrix.Client.Gui.ViewModels;

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Uniform.Matrix.Protocol.Core;

/// <summary>How a timeline item is shown.</summary>
public enum MessageKind
{
    /// <summary>Text message.</summary>
    Message,
    /// <summary>Action, like <c>* alice waves</c>.</summary>
    Emote,
    /// <summary>Notice, usually from a bot.</summary>
    Notice,
    /// <summary>Membership and state changes, deleted and undecryptable messages.</summary>
    Info,
    /// <summary>Image, file, audio or video.</summary>
    Attachment,
}

/// <summary>One item of the open room's timeline.</summary>
public sealed partial class MessageViewModel : ViewModelBase
{
    /// <summary>Event ID; null for messages still in the outbox.</summary>
    public string? EventId { get; init; }
    /// <summary>Sender's user ID.</summary>
    public required string SenderId { get; init; }
    /// <summary>Sender's display name in the room.</summary>
    public required string SenderName { get; init; }
    /// <summary>When it was sent.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>How it is shown.</summary>
    public required MessageKind Kind { get; init; }
    /// <summary>Whether the signed-in user sent it.</summary>
    public bool IsOwn { get; init; }
    /// <summary>Whether it mentions the signed-in user.</summary>
    public bool IsHighlight { get; init; }
    /// <summary>The message this one answers, as <c>Bob: snippet</c>; null when it is not a reply.</summary>
    public string? ReplyQuote { get; init; }
    /// <summary><c>mxc://</c> URI of an image to show a thumbnail of.</summary>
    public string? ImageUri { get; init; }
    /// <summary>Outbox row of a message not sent yet.</summary>
    public long? OutboxId { get; init; }
    /// <summary>File name to save an attachment as.</summary>
    public string? FileName { get; init; }
    /// <summary>Whether it is an image, file, audio or video that can be saved.</summary>
    public bool IsAttachment => Kind == MessageKind.Attachment;
    /// <summary>Content of an attachment, for downloading it.</summary>
    internal MessageContent? Content { get; init; }
    /// <summary>The message event, for replying to it.</summary>
    internal ClientEvent? Event { get; init; }
    /// <summary>Plain body as written, e.g. with its Markdown, for editing it.</summary>
    internal string Source { get; set; } = "";
    /// <summary>Whether the message was deleted.</summary>
    internal bool IsDeleted { get; set; }
    /// <summary>Whether it is a sent message that can be answered and reacted to.</summary>
    internal bool CanAnswer => EventId is not null && Event is not null && !IsDeleted;
    /// <summary>Whether it is an own sent text message that can be edited.</summary>
    internal bool CanEdit => CanAnswer && IsOwn && Kind is MessageKind.Message or MessageKind.Emote;
    /// <summary>Whether it is an own sent message that can be deleted.</summary>
    internal bool CanDelete => CanAnswer && IsOwn;

    /// <summary>Text as shown; changes when the message is edited or deleted.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>HTML <c>formatted_body</c> of the text, or null for plain text.</summary>
    [ObservableProperty]
    public partial string? Html { get; set; }

    /// <summary>Whether the text was edited.</summary>
    [ObservableProperty]
    public partial bool IsEdited { get; set; }

    /// <summary>Reactions, like <c>👍 2  ❤️ 1</c>; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    public partial string Reactions { get; set; } = "";

    /// <summary>Day shown above it when it is the first of its day, e.g. <c>Today</c>; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDay))]
    public partial string? Day { get; set; }

    /// <summary>Get whether <see cref="Day"/> is shown.</summary>
    public bool HasDay => Day is not null;

    /// <summary>Whether the sender and time are shown above it; consecutive messages of one sender share them.</summary>
    [ObservableProperty]
    public partial bool ShowHeader { get; set; } = true;

    /// <summary>Thumbnail of <see cref="ImageUri"/> once loaded.</summary>
    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>State of an outbox message: <c>Sending…</c>, or why it failed; empty once sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial string SendState { get; set; } = "";

    /// <summary>Whether the message is still in the outbox.</summary>
    public bool IsPending => SendState.Length > 0;

    /// <summary>Whether there are reactions.</summary>
    public bool HasReactions => Reactions.Length > 0;

    /// <summary>Whether it is a membership or state change, shown smaller.</summary>
    public bool IsInfo => Kind == MessageKind.Info;

    /// <summary>Time as shown, e.g. <c>14:05</c>.</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    /// <summary>Reaction keys and their senders, aggregated into <see cref="Reactions"/>.</summary>
    internal Dictionary<string, HashSet<string>> ReactionSenders { get; } = [];
}
