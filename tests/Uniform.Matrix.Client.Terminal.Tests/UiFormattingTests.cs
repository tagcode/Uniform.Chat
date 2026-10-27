namespace Durum.Matrix.Client.Terminal.Tests;

using System.Text.Json;
using Durum.Matrix.Client.Terminal.Ui;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="TextWrapper"/> and <see cref="MessageFormatter"/>.</summary>
public class UiFormattingTests
{
    private const string Me = "@me:hs.example";
    private const string Bob = "@bob:hs.example";

    /// <summary>Create an event with <paramref name="content"/>.</summary>
    private static ClientEvent Event(string type, string sender, object content, string? stateKey = null, object? previous = null) => new()
    {
        Type = type,
        Sender = sender,
        EventId = $"$e{Guid.NewGuid():N}",
        OriginServerTs = 1_700_000_000_000,
        StateKey = stateKey,
        Content = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options),
        Unsigned = previous is null ? null : new UnsignedData { PrevContent = JsonSerializer.SerializeToElement(previous, previous.GetType(), MatrixJson.Options) },
    };

    /// <summary>Create a room where Bob is joined as "Bob".</summary>
    private static Room CreateRoom()
    {
        // Apply a sync with members
        RoomStore store = new(Me);
        ClientEvent[] members = [Event(EventTypes.RoomMember, Me, new MemberContent { Membership = Memberships.Join, DisplayName = "Me" }, Me), Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" }, Bob)];
        store.Apply(new SyncResponse { NextBatch = "b", Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { ["!r:hs.example"] = new() { State = new EventList<ClientEvent> { Events = members } } } } });
        // Return room
        return store.Rooms["!r:hs.example"];
    }

    /// <summary>Text wraps at spaces with continuation lines indented.</summary>
    [Fact]
    public void CanWrapAtSpacesWithIndent()
    {
        // Act
        IReadOnlyList<string> lines = TextWrapper.Wrap("12:00 <bob> one two three", 24, indent: 12);

        // Assert
        Assert.Equal(["12:00 <bob> one two", "            three"], lines);
    }

    /// <summary>An indent over half the width is dropped, so narrow views stay readable.</summary>
    [Fact]
    public void CanDropIndentOnNarrowWidth()
    {
        // Act and assert
        Assert.Equal(["12:00 <bob> one", "two three"], TextWrapper.Wrap("12:00 <bob> one two three", 18, indent: 12));
    }

    /// <summary>Words fit on continuation lines when the indent leaves room.</summary>
    [Fact]
    public void CanWrapWords()
    {
        // Act
        IReadOnlyList<string> lines = TextWrapper.Wrap("aaa bbb ccc ddd", 8, indent: 2);

        // Assert
        Assert.Equal(["aaa bbb", "  ccc", "  ddd"], lines);
    }

    /// <summary>Wide characters count as two columns, so lines never overflow.</summary>
    [Fact]
    public void CanWrapWideCharacters()
    {
        // Act
        IReadOnlyList<string> lines = TextWrapper.Wrap("👍👍👍👍👍 ää", 6);

        // Assert
        Assert.Equal(["👍👍👍", "👍👍", "ää"], lines);
        Assert.All(lines, l => Assert.True(TextWrapper.Columns(l) <= 6));
        Assert.Equal(2, TextWrapper.Columns("👍"));
    }

    /// <summary>Long words without spaces are hard-broken; line breaks are kept.</summary>
    [Fact]
    public void CanHardBreakAndKeepNewlines()
    {
        // Act and assert
        Assert.Equal(["abcde", "fgh", "x"], TextWrapper.Wrap("abcdefgh\nx", 5));
    }

    /// <summary>Messages show the disambiguated nick; own and mentioning messages get their kinds.</summary>
    [Fact]
    public void CanFormatMessages()
    {
        // Arrange
        Room room = CreateRoom();
        ClientEvent text = Event(EventTypes.RoomMessage, Bob, new MessageContent { MessageType = MessageTypes.Text, Body = "hi" });
        ClientEvent own = Event(EventTypes.RoomMessage, Me, new MessageContent { MessageType = MessageTypes.Text, Body = "yo" });
        ClientEvent mention = Event(EventTypes.RoomMessage, Bob, new MessageContent { MessageType = MessageTypes.Text, Body = "Me!", Mentions = new Mentions { UserIds = [Me] } });
        ClientEvent emote = Event(EventTypes.RoomMessage, Bob, new MessageContent { MessageType = MessageTypes.Emote, Body = "waves" });

        // Act
        TimelineEntry? t = MessageFormatter.Format(room, text, Me);
        TimelineEntry? o = MessageFormatter.Format(room, own, Me);
        TimelineEntry? m = MessageFormatter.Format(room, mention, Me);
        TimelineEntry? e = MessageFormatter.Format(room, emote, Me);

        // Assert
        Assert.EndsWith(" <Bob> hi", t?.Text);
        Assert.Equal(EntryKind.Message, t?.Kind);
        Assert.Equal(EntryKind.Own, o?.Kind);
        Assert.Equal(EntryKind.Highlight, m?.Kind);
        Assert.EndsWith(" * Bob waves", e?.Text);
        Assert.Equal(EntryKind.Emote, e?.Kind);
        Assert.Equal(12, t?.Indent);
    }

    /// <summary>Edit show the new text; deleted and encrypted messages are described.</summary>
    [Fact]
    public void CanFormatEditsDeletionsAndEncrypted()
    {
        // Arrange
        Room room = CreateRoom();
        ClientEvent edit = Event(EventTypes.RoomMessage, Bob, new MessageContent { MessageType = MessageTypes.Text, Body = "* new", NewContent = new MessageContent { Body = "new" }, RelatesTo = new Relation { RelType = RelationTypes.Replace, EventId = "$x" } });
        ClientEvent deleted = Event(EventTypes.RoomMessage, Bob, new { }) with { Unsigned = new UnsignedData { RedactedBecause = Event(EventTypes.RoomRedaction, Bob, new { }) } };
        ClientEvent encrypted = Event(EventTypes.RoomEncrypted, Bob, new { algorithm = "m.megolm.v1.aes-sha2" });

        // Act and assert
        Assert.EndsWith("<Bob> new (edited)", MessageFormatter.Format(room, edit, Me)?.Text);
        Assert.EndsWith("<Bob> [message deleted]", MessageFormatter.Format(room, deleted, Me)?.Text);
        Assert.Contains("[encrypted message", MessageFormatter.Format(room, encrypted, Me)?.Text);
    }

    /// <summary>Membership changes read like irssi, comparing with the previous content.</summary>
    [Fact]
    public void CanFormatMembershipChanges()
    {
        // Arrange
        Room room = CreateRoom();
        ClientEvent join = Event(EventTypes.RoomMember, "@carol:hs.example", new MemberContent { Membership = Memberships.Join, DisplayName = "Carol" }, "@carol:hs.example");
        ClientEvent rename = Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Robert" }, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" });
        ClientEvent kick = Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Leave, Reason = "spam" }, "@carol:hs.example", new MemberContent { Membership = Memberships.Join, DisplayName = "Carol" });
        ClientEvent avatar = Event(EventTypes.RoomMember, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob", AvatarUrl = "mxc://hs.example/a" }, Bob, new MemberContent { Membership = Memberships.Join, DisplayName = "Bob" });

        // Act and assert
        Assert.EndsWith("-!- Carol joined", MessageFormatter.Format(room, join, Me)?.Text);
        Assert.EndsWith("-!- Bob is now known as Robert", MessageFormatter.Format(room, rename, Me)?.Text);
        Assert.EndsWith("-!- Bob kicked Carol (spam)", MessageFormatter.Format(room, kick, Me)?.Text);
        Assert.Null(MessageFormatter.Format(room, avatar, Me));
        Assert.Equal(EntryKind.Info, MessageFormatter.Format(room, join, Me)?.Kind);
    }

    /// <summary>Unknown event types are not shown.</summary>
    [Fact]
    public void CanSkipUnknownEvents()
    {
        // Act and assert
        Assert.Null(MessageFormatter.Format(CreateRoom(), Event(EventTypes.RoomPowerLevels, Bob, new { }, ""), Me));
    }

    /// <summary>Outbox messages show as sending or failed.</summary>
    [Fact]
    public void CanFormatOutbox()
    {
        // Arrange
        OutboxMessage pending = new() { RoomId = "!r", EventType = EventTypes.RoomMessage, ContentJson = """{"msgtype":"m.text","body":"hi"}""", TransactionId = "t1", State = OutboxState.Pending, CreatedAt = DateTimeOffset.UnixEpoch };
        OutboxMessage failed = new() { RoomId = "!r", EventType = EventTypes.RoomMessage, ContentJson = """{"msgtype":"m.emote","body":"waves"}""", TransactionId = "t2", State = OutboxState.Failed, LastError = "M_FORBIDDEN", CreatedAt = DateTimeOffset.UnixEpoch };

        // Act
        TimelineEntry p = MessageFormatter.FormatOutbox(pending, "Me");
        TimelineEntry f = MessageFormatter.FormatOutbox(failed, "Me");

        // Assert
        Assert.EndsWith("<Me> hi  [sending…]", p.Text);
        Assert.Equal(EntryKind.Pending, p.Kind);
        Assert.EndsWith(" * Me waves  [not sent: M_FORBIDDEN]", f.Text);
        Assert.Equal(EntryKind.Failed, f.Kind);
    }

    /// <summary>Reply quote the answered message when it is known, and drop old-style quote fallbacks.</summary>
    [Fact]
    public void CanFormatReplies()
    {
        // Arrange
        Room room = CreateRoom();
        ClientEvent original = Event(EventTypes.RoomMessage, Bob, new MessageContent { MessageType = MessageTypes.Text, Body = "lunch at noon? it is a long question" });
        ClientEvent reply = Event(EventTypes.RoomMessage, Me, new MessageContent { MessageType = MessageTypes.Text, Body = "> <@bob:hs.example> lunch at noon?\n\nsure", RelatesTo = new Relation { InReplyTo = new InReplyTo { EventId = original.EventId } } });

        // Act
        string? known = MessageFormatter.Format(room, reply, Me, id => id == original.EventId ? original : null)?.Text;
        string? unknown = MessageFormatter.Format(room, reply, Me)?.Text;

        // Assert
        Assert.EndsWith("<Me> [re Bob: lunch at noon? it is a long…] sure", known);
        Assert.EndsWith("<Me> [re] sure", unknown);
    }

    /// <summary>Attachments show type, name and size.</summary>
    [Fact]
    public void CanFormatAttachments()
    {
        // Arrange
        Room room = CreateRoom();
        ClientEvent image = Event(EventTypes.RoomMessage, Bob, MatrixClient.CreateFileContent(MxcUri.Parse("mxc://hs.example/a"), "cat.png", "image/png", 2048));

        // Act and assert
        Assert.EndsWith("<Bob> [image: cat.png, 2 KB]", MessageFormatter.Format(room, image, Me)?.Text);
    }

    /// <summary>Sizes, snippets and fallback stripping behave at their edges.</summary>
    [Fact]
    public void CanFormatHelpers()
    {
        // Assert sizes
        Assert.Equal("512 B", EventText.FormatSize(512));
        Assert.Equal("1.5 MB", EventText.FormatSize(1_572_864));

        // Assert snippets cut by columns and lines
        Assert.Equal("short", MessageFormatter.Snippet("short\nsecond line", 10));
        Assert.Equal("👍👍…", MessageFormatter.Snippet("👍👍👍👍", 6));

        // Assert fallback stripping only applies to leading quotes
        Assert.Equal("text", EventText.StripReplyFallback("> quoted\n> more\n\ntext"));
        Assert.Equal("a > b", EventText.StripReplyFallback("a > b"));
    }

    /// <summary>Common extensions map to MIME types, others to octet-stream.</summary>
    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("/tmp/song.mp3", "audio/mpeg")]
    [InlineData("notes", "application/octet-stream")]
    public void CanGuessMimeTypes(string path, string expected)
    {
        // Act and assert
        Assert.Equal(expected, MimeTypes.FromPath(path));
    }
}
