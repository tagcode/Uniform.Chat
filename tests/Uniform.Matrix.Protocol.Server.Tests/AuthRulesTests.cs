namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;

/// <summary>Tests of the authorization rules of room versions 10 and 11.</summary>
public class AuthRulesTests
{
    private const string Room = "!room:a.example";
    private const string Alice = "@alice:a.example";
    private const string Bob = "@bob:a.example";
    private const string Carol = "@carol:b.example";

    /// <summary>A room's state for checking events, built event by event.</summary>
    private sealed class TestRoom(RoomVersion version)
    {
        private int counter;

        /// <summary>Current state with event IDs, as the rules expect.</summary>
        public Dictionary<(string Type, string StateKey), JsonObject> State { get; } = [];
        /// <summary>The latest event's ID, the next event's only previous event.</summary>
        public string? Last { get; private set; }

        /// <summary>Build an event on top of the state.</summary>
        public JsonObject Event(string type, string sender, JsonObject content, string? stateKey = null)
        {
            // Minimal PDU with an ID
            JsonObject pdu = new() { ["type"] = type, ["room_id"] = Room, ["sender"] = sender, ["content"] = content, ["event_id"] = $"$e{++counter}", ["prev_events"] = Last is null ? new JsonArray() : new JsonArray(Last) };
            if (stateKey is not null) pdu["state_key"] = stateKey;
            // Return event
            return pdu;
        }

        /// <summary>Return the rules' verdict on an event.</summary>
        public string? Check(JsonObject pdu) => AuthRules.Check(pdu, version, State);

        /// <summary>Check an event and apply it to the state when allowed; return the verdict.</summary>
        public string? Send(string type, string sender, JsonObject content, string? stateKey = null)
        {
            // Check, then apply
            JsonObject pdu = Event(type, sender, content, stateKey);
            string? error = Check(pdu);
            if (error is not null) return error;
            Last = pdu["event_id"]!.GetValue<string>();
            if (stateKey is not null) State[(type, stateKey)] = pdu;
            // Return allowed
            return null;
        }

        /// <summary>Send a membership change.</summary>
        public string? Member(string sender, string target, string membership) => Send("m.room.member", sender, new JsonObject { ["membership"] = membership }, target);

        /// <summary>Create the room as Alice with power levels and the given join rule.</summary>
        public TestRoom Created(string joinRule)
        {
            // Create, join, power, join rules
            JsonObject create = new() { ["room_version"] = version.Id };
            if (version.CreateHasCreator) create["creator"] = Alice;
            Assert.Null(Send("m.room.create", Alice, create, ""));
            Assert.Null(Member(Alice, Alice, "join"));
            Assert.Null(Send("m.room.power_levels", Alice, new JsonObject { ["users"] = new JsonObject { [Alice] = 100 }, ["invite"] = 0, ["kick"] = 50, ["ban"] = 50 }, ""));
            Assert.Null(Send("m.room.join_rules", Alice, new JsonObject { ["join_rule"] = joinRule }, ""));
            // Return room
            return this;
        }
    }

    /// <summary>Create events need a matching room ID, a known version, and in version 10 a creator.</summary>
    [Fact]
    public void CheckCreate()
    {
        // Arrange
        TestRoom v10 = new(RoomVersion.V10), v11 = new(RoomVersion.V11);

        // Act and assert
        Assert.NotNull(v10.Check(v10.Event("m.room.create", Alice, new JsonObject { ["room_version"] = "10" }, "")));
        Assert.Null(v11.Check(v11.Event("m.room.create", Alice, new JsonObject { ["room_version"] = "11" }, "")));
        Assert.NotNull(v11.Check(v11.Event("m.room.create", Carol, new JsonObject(), "")));
        Assert.NotNull(v11.Check(v11.Event("m.room.create", Alice, new JsonObject { ["room_version"] = "99" }, "")));
        JsonObject withHistory = v11.Event("m.room.create", Alice, new JsonObject(), "");
        withHistory["prev_events"] = new JsonArray("$x");
        Assert.NotNull(v11.Check(withHistory));
    }

    /// <summary>Invite-only rooms need an invite; invited users may join or decline; others cannot join or speak.</summary>
    [Theory]
    [InlineData("10")]
    [InlineData("11")]
    public void JoinByInvite(string version)
    {
        // Arrange
        TestRoom room = new TestRoom(RoomVersion.Find(version)!).Created("invite");

        // Act and assert
        Assert.NotNull(room.Member(Bob, Bob, "join"));
        Assert.NotNull(room.Send("m.room.message", Bob, new JsonObject { ["body"] = "hi" }));
        Assert.NotNull(room.Member(Bob, Carol, "invite"));
        Assert.Null(room.Member(Alice, Bob, "invite"));
        Assert.Null(room.Member(Bob, Bob, "join"));
        Assert.Null(room.Send("m.room.message", Bob, new JsonObject { ["body"] = "hi" }));
        Assert.NotNull(room.Member(Bob, Alice, "join"));
    }

    /// <summary>Public rooms are open to anyone not banned; bans need power above the target.</summary>
    [Fact]
    public void BanFromPublicRoom()
    {
        // Arrange
        TestRoom room = new TestRoom(RoomVersion.V11).Created("public");
        Assert.Null(room.Member(Bob, Bob, "join"));

        // Act and assert: Bob has no power to ban, Alice does
        Assert.NotNull(room.Member(Bob, Alice, "ban"));
        Assert.Null(room.Member(Alice, Bob, "ban"));
        Assert.NotNull(room.Member(Bob, Bob, "join"));
        Assert.NotNull(room.Send("m.room.message", Bob, new JsonObject()));

        // Act and assert: unban, then Bob may come back
        Assert.Null(room.Member(Alice, Bob, "leave"));
        Assert.Null(room.Member(Bob, Bob, "join"));
    }

    /// <summary>Kicking needs the kick level and a higher level than the target; leaving is always possible.</summary>
    [Fact]
    public void KickAndLeave()
    {
        // Arrange
        TestRoom room = new TestRoom(RoomVersion.V11).Created("public");
        Assert.Null(room.Member(Bob, Bob, "join"));

        // Act and assert
        Assert.NotNull(room.Member(Bob, Alice, "leave"));
        Assert.Null(room.Member(Alice, Bob, "leave"));
        Assert.NotNull(room.Member(Bob, Bob, "leave"));
        Assert.Null(room.Member(Bob, Bob, "join"));
        Assert.Null(room.Member(Bob, Bob, "leave"));
    }

    /// <summary>Power level changes stay within the sender's level, and users at or above it cannot be changed.</summary>
    [Fact]
    public void LimitPowerChanges()
    {
        // Arrange: Bob at 50
        TestRoom room = new TestRoom(RoomVersion.V11).Created("public");
        Assert.Null(room.Member(Bob, Bob, "join"));
        Assert.Null(room.Send("m.room.power_levels", Alice, new JsonObject { ["users"] = new JsonObject { [Alice] = 100, [Bob] = 50 }, ["state_default"] = 50 }, ""));

        // Act and assert
        Assert.NotNull(room.Send("m.room.power_levels", Bob, new JsonObject { ["users"] = new JsonObject { [Alice] = 100, [Bob] = 60 }, ["state_default"] = 50 }, ""));
        Assert.NotNull(room.Send("m.room.power_levels", Bob, new JsonObject { ["users"] = new JsonObject { [Alice] = 0, [Bob] = 50 }, ["state_default"] = 50 }, ""));
        Assert.NotNull(room.Send("m.room.power_levels", Bob, new JsonObject { ["users"] = new JsonObject { [Alice] = 100, [Bob] = 50 }, ["state_default"] = 75 }, ""));
        Assert.NotNull(room.Send("m.room.power_levels", Alice, new JsonObject { ["users"] = new JsonObject { [Alice] = "100" } }, ""));
        Assert.Null(room.Send("m.room.power_levels", Bob, new JsonObject { ["users"] = new JsonObject { [Alice] = 100, [Bob] = 50, [Carol] = 50 }, ["state_default"] = 50 }, ""));
    }

    /// <summary>State needs the state level, and user-keyed state belongs to that user.</summary>
    [Fact]
    public void ProtectState()
    {
        // Arrange
        TestRoom room = new TestRoom(RoomVersion.V11).Created("public");
        Assert.Null(room.Member(Bob, Bob, "join"));

        // Act and assert
        Assert.NotNull(room.Send("m.room.name", Bob, new JsonObject { ["name"] = "x" }, ""));
        Assert.Null(room.Send("m.room.name", Alice, new JsonObject { ["name"] = "x" }, ""));
        Assert.NotNull(room.Send("org.example.status", Alice, new JsonObject(), Bob));
        Assert.Null(room.Send("m.room.message", Bob, new JsonObject { ["body"] = "ok" }));
    }

    /// <summary>Knocking works only in knock rooms and not for members; an unknown membership is rejected.</summary>
    [Fact]
    public void Knock()
    {
        // Arrange
        TestRoom invite = new TestRoom(RoomVersion.V11).Created("invite");
        TestRoom knock = new TestRoom(RoomVersion.V11).Created("knock");

        // Act and assert
        Assert.NotNull(invite.Member(Bob, Bob, "knock"));
        Assert.Null(knock.Member(Bob, Bob, "knock"));
        Assert.NotNull(knock.Member(Alice, Alice, "knock"));
        Assert.NotNull(knock.Member(Bob, Bob, "dance"));
    }
}
