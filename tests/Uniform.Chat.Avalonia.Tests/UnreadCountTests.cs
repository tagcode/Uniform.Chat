namespace Uniform.Chat.Avalonia.Tests;

using System.Text.Json;
using Uniform.Chat.Avalonia.ViewModels;
using Uniform.Matrix.Protocol.Core;

/// <summary>The room list's unread count: in encrypted rooms the server counts every event, as it cannot see inside; the app leaves out what would not notify in the clear.</summary>
public class UnreadCountTests
{
    private const string Me = "@me:example.org";
    private const string Peer = "@peer:example.org";

    /// <summary>A message and a call in an encrypted room count two, not the call's every signalling event.</summary>
    [Fact]
    public void LeaveOutCallSignalling()
    {
        // Arrange: what a server counts as six
        ClientEvent[] timeline =
        [
            Event("$1", Peer, EventTypes.RoomMessage, new { msgtype = "m.text", body = "hello" }),
            Event("$2", Peer, EventTypes.CallInvite, new { call_id = "c" }),
            Event("$3", Peer, EventTypes.CallCandidates, new { call_id = "c" }),
            Event("$4", Peer, EventTypes.CallCandidates, new { call_id = "c" }),
            Event("$5", Peer, EventTypes.CallHangup, new { call_id = "c" }),
            Event("$6", Peer, EventTypes.Reaction, new { }),
        ];

        // Act and assert
        Assert.Equal(2, RoomItemViewModel.CountUnread(6, encrypted: true, timeline, readUpTo: null, Me));
        Assert.Equal(6, RoomItemViewModel.CountUnread(6, encrypted: false, timeline, readUpTo: null, Me));
    }

    /// <summary>Only events after the read point are left out: the read receipt or the user's own latest event; and never below zero.</summary>
    [Fact]
    public void CountFromReadPoint()
    {
        // Arrange: older signalling, read; then an edit and a message
        ClientEvent[] timeline =
        [
            Event("$1", Peer, EventTypes.CallCandidates, new { call_id = "c" }),
            Event("$2", Me, EventTypes.RoomMessage, new { msgtype = "m.text", body = "mine" }),
            Event("$3", Peer, EventTypes.RoomMessage, new { msgtype = "m.text", body = "* hello", m_relates_to = 0 }),
            Event("$4", Peer, EventTypes.RoomMessage, new Dictionary<string, object> { ["msgtype"] = "m.text", ["body"] = "* edited", ["m.relates_to"] = new { rel_type = "m.replace", event_id = "$3" } }),
            Event("$5", Peer, EventTypes.RoomMessage, new { msgtype = "m.text", body = "new" }),
        ];

        // Act and assert: after the own message, the edit is left out
        Assert.Equal(2, RoomItemViewModel.CountUnread(3, encrypted: true, timeline, readUpTo: null, Me));
        Assert.Equal(1, RoomItemViewModel.CountUnread(2, encrypted: true, timeline, readUpTo: "$3", Me));
        Assert.Equal(0, RoomItemViewModel.CountUnread(1, encrypted: true, [Event("$9", Peer, EventTypes.CallHangup, new { }), Event("$10", Peer, EventTypes.CallHangup, new { })], readUpTo: null, Me));
    }

    /// <summary>Return a timeline event of <paramref name="sender"/>.</summary>
    private static ClientEvent Event(string eventId, string sender, string type, object content) => new()
    {
        EventId = eventId,
        Sender = sender,
        OriginServerTs = 0,
        Type = type,
        Content = JsonSerializer.SerializeToElement(content),
    };
}
