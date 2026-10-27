namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>The rules of <see href="https://spec.matrix.org/latest/rooms/v12/">room version 12</see>: room IDs from the create event, no create event among auth events, and unlimited power for creators.</summary>
public class RoomVersion12Tests
{
    private const string Alice = "@alice:a.org";
    private const string Bob = "@bob:b.org";
    private static readonly RoomVersion Version = RoomVersion.V12;
    private static readonly (string Secret, string PublicKey) Key = Ed25519.GenerateKey();

    /// <summary>Return a signed event with its ID added, as auth state carries it.</summary>
    private static JsonObject Event(string? roomId, string sender, string type, string? stateKey, JsonObject content, string[] prevEvents, string[] authEvents)
    {
        // Fields of the version
        JsonObject pdu = new() { ["type"] = type, ["sender"] = sender, ["content"] = content, ["depth"] = 1, ["origin_server_ts"] = 1, ["prev_events"] = new JsonArray([.. prevEvents.Select(e => (JsonNode)e)]), ["auth_events"] = new JsonArray([.. authEvents.Select(e => (JsonNode)e)]) };
        if (roomId is not null) pdu["room_id"] = roomId;
        if (stateKey is not null) pdu["state_key"] = stateKey;
        EventFormat.HashAndSign(pdu, Version, sender[(sender.IndexOf(':') + 1)..], "ed25519:k", Key.Secret);
        pdu["event_id"] = EventFormat.ComputeEventId(pdu, Version);
        // Return event
        return pdu;
    }

    /// <summary>Return a room's create event, its room ID and a state of the create event alone.</summary>
    private static (JsonObject Create, string RoomId) CreateRoom(JsonObject? content = null)
    {
        // Create without a room ID; the ID follows from it
        JsonObject create = Event(null, Alice, "m.room.create", "", content ?? new JsonObject { ["room_version"] = "12" }, [], []);
        // Return create and ID
        return (create, RoomVersion.RoomIdOf(create["event_id"]!.GetValue<string>()));
    }

    /// <summary>The create event has no room ID; the creator's first join is allowed without the create event among its auth events.</summary>
    [Fact]
    public void CreateAndJoin()
    {
        // Arrange
        (JsonObject create, string roomId) = CreateRoom();
        JsonObject join = Event(roomId, Alice, "m.room.member", Alice, new JsonObject { ["membership"] = "join" }, [create["event_id"]!.GetValue<string>()], []);
        Dictionary<(string, string), JsonObject> state = new() { [("m.room.create", "")] = create };

        // Act and assert
        Assert.Null(AuthRules.Check(create, Version, new Dictionary<(string, string), JsonObject>()));
        Assert.Null(AuthRules.Check(join, Version, state));
        Assert.StartsWith("!", roomId);
        Assert.DoesNotContain(":", roomId);
    }

    /// <summary>A create event with a room ID, an event naming the create event as auth event, and an event of another room ID are rejected.</summary>
    [Fact]
    public void RefuseOldForms()
    {
        // Arrange
        (JsonObject create, string roomId) = CreateRoom();
        string createId = create["event_id"]!.GetValue<string>();
        Dictionary<(string, string), JsonObject> state = new() { [("m.room.create", "")] = create };
        JsonObject withRoomId = Event("!x:a.org", Alice, "m.room.create", "", new JsonObject { ["room_version"] = "12" }, [], []);
        JsonObject createAsAuth = Event(roomId, Alice, "m.room.member", Alice, new JsonObject { ["membership"] = "join" }, [createId], [createId]);
        JsonObject otherRoom = Event("!other", Alice, "m.room.member", Alice, new JsonObject { ["membership"] = "join" }, [createId], []);

        // Act and assert
        Assert.NotNull(AuthRules.Check(withRoomId, Version, new Dictionary<(string, string), JsonObject>()));
        Assert.NotNull(AuthRules.Check(createAsAuth, Version, state));
        Assert.NotNull(AuthRules.Check(otherRoom, Version, state));
        Assert.NotNull(AuthRules.Check(Event(null, Alice, "m.room.create", "", new JsonObject { ["additional_creators"] = new JsonArray("not a user") }, [], []), Version, new Dictionary<(string, string), JsonObject>()));
    }

    /// <summary>Creators, also additional ones, outrank every power level and cannot be listed in power levels.</summary>
    [Fact]
    public void CreatorsAboveLevels()
    {
        // Arrange: power levels that give bob 100, and need 100 for a topic
        (JsonObject create, string roomId) = CreateRoom(new JsonObject { ["room_version"] = "12", ["additional_creators"] = new JsonArray(Bob) });
        string createId = create["event_id"]!.GetValue<string>();
        JsonObject aliceJoin = Event(roomId, Alice, "m.room.member", Alice, new JsonObject { ["membership"] = "join" }, [createId], []);
        JsonObject bobJoin = Event(roomId, Bob, "m.room.member", Bob, new JsonObject { ["membership"] = "join" }, [aliceJoin["event_id"]!.GetValue<string>()], []);
        JsonObject levels = Event(roomId, Alice, "m.room.power_levels", "", new JsonObject { ["users"] = new JsonObject(), ["events"] = new JsonObject { ["m.room.topic"] = 100 }, ["state_default"] = 100 }, [], []);
        Dictionary<(string, string), JsonObject> state = new() { [("m.room.create", "")] = create, [("m.room.member", Alice)] = aliceJoin, [("m.room.member", Bob)] = bobJoin, [("m.room.power_levels", "")] = levels };

        // Act
        string? topic = AuthRules.Check(Event(roomId, Bob, "m.room.topic", "", new JsonObject { ["topic"] = "t" }, [], []), Version, state);
        string? listCreator = AuthRules.Check(Event(roomId, Alice, "m.room.power_levels", "", new JsonObject { ["users"] = new JsonObject { [Bob] = 100 } }, [], []), Version, state);
        string? kickCreator = AuthRules.Check(Event(roomId, Alice, "m.room.member", Bob, new JsonObject { ["membership"] = "leave" }, [], []), Version, state);

        // Assert
        Assert.Null(topic);
        Assert.NotNull(listCreator);
        Assert.NotNull(kickCreator);
        Assert.Equal(new[] { Alice, Bob }, AuthRules.Creators(create));
    }
}
