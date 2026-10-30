namespace Uniform.Chat.Terminal.Tests;

using System.Text.Json;
using Uniform.Chat.Terminal.Ui;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for formatted messages in the message log: wrapping and rendering with room member names.</summary>
public class MessageLogTests
{
    private const string Me = "@me:hs.example";
    private const string Bob = "@bob:hs.example";

    /// <summary>Later paragraphs of a message align with the text after the nick.</summary>
    [Fact]
    public void CanIndentLaterParagraphs()
    {
        // Act and assert
        Assert.Equal(["12:00 <bob> first", "            second"], TextWrapper.Wrap("12:00 <bob> first\nsecond", 40, indent: 12));
    }

    /// <summary>The log shows the formatted body, also of edits, with mention names from the room.</summary>
    [Fact]
    public void CanFormatHtmlMessagesInLog()
    {
        // Arrange: room where Bob is "Robert"
        RoomStore store = new(Me);
        ClientEvent member = new() { Type = EventTypes.RoomMember, Sender = Bob, StateKey = Bob, EventId = "$m", OriginServerTs = 1, Content = JsonSerializer.SerializeToElement(new MemberContent { Membership = Memberships.Join, DisplayName = "Robert" }, MatrixJson.Options) };
        store.Apply(new SyncResponse { NextBatch = "b", Rooms = new SyncRooms { Join = new Dictionary<string, JoinedRoom> { ["!r"] = new() { State = new EventList<ClientEvent> { Events = [member] } } } } });
        Room room = store.Rooms["!r"];
        MessageContent formatted = new() { MessageType = MessageTypes.Text, Body = "**hi** bob", Format = MessageFormats.Html, FormattedBody = "<strong>hi</strong> <a href=\"https://matrix.to/#/@bob:hs.example\">bob</a>" };
        ClientEvent message = new() { Type = EventTypes.RoomMessage, Sender = Me, EventId = "$1", OriginServerTs = 2, Content = JsonSerializer.SerializeToElement(formatted, MatrixJson.Options) };
        ClientEvent edit = message with { EventId = "$2", Content = JsonSerializer.SerializeToElement(MatrixClient.CreateEditContent("$1", "**new**", "<strong>new</strong>"), MatrixJson.Options) };

        // Act and assert
        Assert.EndsWith("<me:hs.example> *hi* Robert", MessageFormatter.Format(room, message, Me)?.Text.Replace("@me", "me"));
        Assert.EndsWith("*new* (edited)", MessageFormatter.Format(room, edit, Me)?.Text);
    }
}
