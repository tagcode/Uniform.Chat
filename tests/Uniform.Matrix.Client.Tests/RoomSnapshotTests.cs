namespace Uniform.Matrix.Client.Tests;

using System.Text.Json;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Rooms saved after a sync and listed again at the next start before any sync, as without network.</summary>
public class RoomSnapshotTests
{
    private const string Me = "@me:hs.example";
    private const string Bob = "@bob:hs.example";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A named room, a direct chat with its unread count and an invite come back with their names and kinds, without a sync and without their events being published again.</summary>
    [Fact]
    public async Task ListRoomsWithoutSync()
    {
        // A session that synced a named room, a direct chat and an invite
        await using TestDatabase db = new();
        ClientStore store = await db.CreateStoreAsync();
        await store.SaveSessionAsync(new MatrixSession { Homeserver = new Uri("https://hs.example"), UserId = Me, DeviceId = "D", AccessToken = "t" }, Ct);
        SyncProcessor first = new(store);
        await first.ProcessAsync(Sync(), Me, Ct);

        // The next start, before any sync
        SyncProcessor next = new(store);
        Assert.True(await next.RestoreRoomsAsync(Me, Ct));
        Assert.False(await next.RestoreRoomsAsync(Me, Ct));
        Assert.True(next.Changes.TryRead(out SyncChanges? restored));
        Assert.Empty(restored.Timeline);

        // Done: rooms as they were
        next.Read(rooms =>
        {
            Assert.Equal("Planning", rooms!.Rooms["!named:hs.example"].DisplayName);
            Room direct = rooms.Rooms["!direct:hs.example"];
            Assert.Equal(Bob, direct.DirectUserId);
            Assert.Equal(2, direct.NotificationCount);
            Assert.Equal(Memberships.Invite, rooms.Rooms["!invite:hs.example"].Membership);
            Assert.Single(direct.Timeline);
            return 0;
        });
    }

    /// <summary>A sync with a named room, a direct chat with Bob marked in account data and one message, and an invite.</summary>
    private static SyncResponse Sync()
    {
        // Rooms by membership
        ClientEvent Member(string user) => State(EventTypes.RoomMember, user, new MemberContent { Membership = Memberships.Join });
        ClientEvent named = State(EventTypes.RoomName, "", new NameContent { Name = "Planning" });
        ClientEvent message = new() { Type = EventTypes.RoomMessage, Sender = Bob, EventId = "$m", OriginServerTs = 1000, Content = JsonSerializer.SerializeToElement(new MessageContent { MessageType = MessageTypes.Text, Body = "hi" }, MatrixJson.Options) };
        MatrixEvent direct = new() { Type = EventTypes.Direct, Content = JsonSerializer.SerializeToElement(new Dictionary<string, string[]> { [Bob] = ["!direct:hs.example"] }) };
        StrippedStateEvent invite = new() { Type = EventTypes.RoomMember, Sender = Bob, StateKey = Me, Content = JsonSerializer.SerializeToElement(new MemberContent { Membership = Memberships.Invite }, MatrixJson.Options) };
        // Return sync
        return new SyncResponse
        {
            NextBatch = "b1",
            AccountData = new EventList<MatrixEvent> { Events = [direct] },
            Rooms = new SyncRooms
            {
                Join = new Dictionary<string, JoinedRoom>
                {
                    ["!named:hs.example"] = new() { State = new EventList<ClientEvent> { Events = [named, Member(Me)] } },
                    ["!direct:hs.example"] = new() { State = new EventList<ClientEvent> { Events = [Member(Me), Member(Bob)] }, Timeline = new Timeline { Events = [message] }, UnreadNotifications = new UnreadNotificationCounts { NotificationCount = 2 } },
                },
                Invite = new Dictionary<string, InvitedRoom> { ["!invite:hs.example"] = new() { InviteState = new EventList<StrippedStateEvent> { Events = [invite] } } },
            },
        };
    }

    /// <summary>A state event.</summary>
    private static ClientEvent State(string type, string stateKey, object content) => new() { Type = type, Sender = Me, StateKey = stateKey, EventId = $"${type}{stateKey}", OriginServerTs = 1, Content = JsonSerializer.SerializeToElement(content, content.GetType(), MatrixJson.Options) };
}
