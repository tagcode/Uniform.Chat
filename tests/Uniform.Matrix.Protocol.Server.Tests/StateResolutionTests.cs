namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;

/// <summary>Tests of <see cref="StateResolution"/>: rooms whose history forks, resolved as other servers would.</summary>
public class StateResolutionTests
{
    private const string Alice = "@alice:a.example";
    private const string Bob = "@bob:a.example";
    private const string Carol = "@carol:b.example";

    /// <summary>Events of one room, sent on branches that fork from each other.</summary>
    private sealed class TestRoom(RoomVersion version)
    {
        private int counter;
        private long clock = 1000;
        private string? last;

        /// <summary>Every event by ID.</summary>
        public Dictionary<string, JsonObject> Events { get; } = [];
        /// <summary>Room ID; from version 12 the create event's hash.</summary>
        public string RoomId { get; private set; } = "!room:a.example";
        /// <summary>The room's version.</summary>
        public RoomVersion Version => version;
        /// <summary>Whether to send events the rules do not allow, as a misbehaving server could.</summary>
        public bool Forging { get; set; }

        /// <summary>Send an event on <paramref name="branch"/> with auth events from its state, applying it when it is state; return its ID.</summary>
        public string Send(Dictionary<(string, string), string> branch, string type, string sender, JsonObject content, string? stateKey = null)
        {
            // Auth events from the branch's state; version 12 leaves the create event out. The previous event is the last one sent, as only the creator's first join reads it
            List<string> authIds = [.. AuthEvents.Select(type, stateKey, sender, content).Where(branch.ContainsKey).Select(k => branch[k])];
            if (version.RoomIdIsCreateHash) authIds.RemoveAll(id => Events[id]["type"]!.GetValue<string>() == AuthEventTypes.Create);
            string id = $"$e{++counter:D3}";
            JsonObject pdu = new() { ["type"] = type, ["room_id"] = RoomId, ["sender"] = sender, ["content"] = content, ["origin_server_ts"] = ++clock, ["auth_events"] = new JsonArray([.. authIds.Select(a => (JsonNode)a)]), ["prev_events"] = last is null ? new JsonArray() : new JsonArray(last) };
            if (stateKey is not null) pdu["state_key"] = stateKey;
            if (type == AuthEventTypes.Create && version.RoomIdIsCreateHash)
            {
                pdu.Remove("room_id");
                RoomId = RoomVersion.RoomIdOf(id);
            }

            // Check against the branch, as a server would before sending it, unless forging
            Dictionary<(string, string), JsonObject> state = branch.ToDictionary(e => e.Key, e => WithId(e.Value));
            if (!Forging) Assert.Null(AuthRules.Check(WithId(id, pdu), version, state));
            Events[id] = pdu;
            last = id;
            if (stateKey is not null) branch[(type, stateKey)] = id;
            // Return ID
            return id;
        }

        /// <summary>Send a membership change.</summary>
        public string Member(Dictionary<(string, string), string> branch, string sender, string target, string membership) => Send(branch, "m.room.member", sender, new JsonObject { ["membership"] = membership }, target);

        /// <summary>Create the room as Alice, public, with Bob as moderator and Carol joined.</summary>
        public Dictionary<(string, string), string> Created()
        {
            // Create, join, power, join rules, more members
            Dictionary<(string, string), string> state = [];
            JsonObject create = new() { ["room_version"] = version.Id };
            if (version.CreateHasCreator) create["creator"] = Alice;
            Send(state, AuthEventTypes.Create, Alice, create, "");
            Member(state, Alice, Alice, "join");
            JsonObject users = version.RoomIdIsCreateHash ? new JsonObject { [Bob] = 50 } : new JsonObject { [Alice] = 100, [Bob] = 50 };
            Send(state, AuthEventTypes.PowerLevels, Alice, new JsonObject { ["users"] = users, ["ban"] = 50, ["kick"] = 50, ["state_default"] = 50, ["events"] = new JsonObject { ["m.room.topic"] = 0 } }, "");
            Send(state, AuthEventTypes.JoinRules, Alice, new JsonObject { ["join_rule"] = "public" }, "");
            Member(state, Bob, Bob, "join");
            Member(state, Carol, Carol, "join");
            // Return state
            return state;
        }

        /// <summary>Return the state of two branches merged.</summary>
        public Dictionary<(string Type, string StateKey), string> Resolve(params Dictionary<(string, string), string>[] branches) =>
            StateResolution.Resolve(version, [.. branches.Select(b => (IReadOnlyDictionary<(string, string), string>)b)], Events);

        /// <summary>Return a copy of an event with its ID.</summary>
        private JsonObject WithId(string id, JsonObject? pdu = null)
        {
            // Copy with the ID
            JsonObject copy = (pdu ?? Events[id]).DeepClone().AsObject();
            copy["event_id"] = id;
            // Return copy
            return copy;
        }
    }

    /// <summary>A moderator who bans someone while an admin takes away their power loses the ban: the admin's change comes first, and the ban is no longer allowed.</summary>
    [Theory]
    [InlineData("10")]
    [InlineData("11")]
    [InlineData("12")]
    public void PreferStrongerSender(string versionId)
    {
        // Arrange: Alice demotes Bob on one branch, Bob bans Carol on the other
        TestRoom room = new(RoomVersion.Find(versionId)!);
        Dictionary<(string, string), string> start = room.Created();
        Dictionary<(string, string), string> alices = new(start), bobs = new(start);
        JsonObject users = room.Version.RoomIdIsCreateHash ? new JsonObject() : new JsonObject { [Alice] = 100 };
        string demotion = room.Send(alices, AuthEventTypes.PowerLevels, Alice, new JsonObject { ["users"] = users, ["ban"] = 50, ["kick"] = 50, ["state_default"] = 50, ["events"] = new JsonObject { ["m.room.topic"] = 0 } }, "");
        room.Member(bobs, Bob, Carol, "ban");

        // Act
        Dictionary<(string Type, string StateKey), string> resolved = room.Resolve(alices, bobs);

        // Assert
        Assert.Equal(demotion, resolved[(AuthEventTypes.PowerLevels, "")]);
        Assert.Equal(start[(AuthEventTypes.Member, Carol)], resolved[(AuthEventTypes.Member, Carol)]);
    }

    /// <summary>A member's topic change made while they were banned elsewhere is dropped, as they were no longer in the room.</summary>
    [Theory]
    [InlineData("10")]
    [InlineData("12")]
    public void DropEventsOfRemovedSenders(string versionId)
    {
        // Arrange: Bob bans Carol on one branch, Carol sets the topic on the other
        TestRoom room = new(RoomVersion.Find(versionId)!);
        Dictionary<(string, string), string> start = room.Created();
        Dictionary<(string, string), string> bobs = new(start), carols = new(start);
        string ban = room.Member(bobs, Bob, Carol, "ban");
        room.Send(carols, "m.room.topic", Carol, new JsonObject { ["topic"] = "Carol was here" }, "");

        // Act
        Dictionary<(string Type, string StateKey), string> resolved = room.Resolve(bobs, carols);

        // Assert
        Assert.Equal(ban, resolved[(AuthEventTypes.Member, Carol)]);
        Assert.False(resolved.ContainsKey(("m.room.topic", "")));
    }

    /// <summary>Received state is checked against its own auth chain: a power levels event its sender could not send is dropped, and so is what was authorized by it.</summary>
    [Theory]
    [InlineData("11")]
    [InlineData("12")]
    public void CheckReceivedAuthChains(string versionId)
    {
        // Arrange: Carol, who has no power, makes herself admin and then renames the room
        TestRoom room = new(RoomVersion.Find(versionId)!);
        Dictionary<(string, string), string> state = room.Created();
        room.Forging = true;
        string forged = room.Send(state, AuthEventTypes.PowerLevels, Carol, new JsonObject { ["users"] = new JsonObject { [Carol] = 100 } }, "");
        string renamed = room.Send(state, "m.room.name", Carol, new JsonObject { ["name"] = "Mine" }, "");

        // Act
        HashSet<string> allowed = AuthRules.CheckAuthChains(room.Version, room.Events);

        // Assert
        Assert.Equal(room.Events.Count - 2, allowed.Count);
        Assert.DoesNotContain(forged, allowed);
        Assert.DoesNotContain(renamed, allowed);
    }

    /// <summary>Of two topics set at the same time under the same power levels, the later one wins, and either order of the branches gives the same state.</summary>
    [Fact]
    public void PreferLaterOfEquals()
    {
        // Arrange: Bob and Carol each set the topic
        TestRoom room = new(RoomVersion.V11);
        Dictionary<(string, string), string> start = room.Created();
        Dictionary<(string, string), string> bobs = new(start), carols = new(start);
        room.Send(bobs, "m.room.topic", Bob, new JsonObject { ["topic"] = "Bob's" }, "");
        string later = room.Send(carols, "m.room.topic", Carol, new JsonObject { ["topic"] = "Carol's" }, "");

        // Act
        Dictionary<(string Type, string StateKey), string> forward = room.Resolve(bobs, carols);
        Dictionary<(string Type, string StateKey), string> backward = room.Resolve(carols, bobs);

        // Assert
        Assert.Equal(later, forward[("m.room.topic", "")]);
        Assert.Equal(forward, backward);
    }

    /// <summary>State only one branch changed is kept as that branch has it, and equal branches resolve to themselves.</summary>
    [Fact]
    public void KeepUnconflictedState()
    {
        // Arrange: a name on one branch only
        TestRoom room = new(RoomVersion.V11);
        Dictionary<(string, string), string> start = room.Created();
        Dictionary<(string, string), string> named = new(start);
        string name = room.Send(named, "m.room.name", Alice, new JsonObject { ["name"] = "Kitchen" }, "");

        // Act
        Dictionary<(string Type, string StateKey), string> resolved = room.Resolve(named, start);
        Dictionary<(string Type, string StateKey), string> same = room.Resolve(start, new Dictionary<(string, string), string>(start));

        // Assert
        Assert.Equal(name, resolved[("m.room.name", "")]);
        Assert.Equal(start, same);
    }
}
