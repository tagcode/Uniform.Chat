namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;

/// <summary>Tests of <see cref="PushRules"/>: the default rules decide which events notify and which are highlighted.</summary>
public class PushRuleTests
{
    private const string Me = "@alice:hs.example";
    private const string Bob = "@bob:hs.example";
    private const string RoomId = "!room:hs.example";
    private static readonly JsonObject Rules = PushRules.Defaults(Me);

    /// <summary>Return a message event from Bob with <paramref name="content"/>.</summary>
    private static JsonObject Message(JsonObject content, string type = "m.room.message") => new() { ["type"] = type, ["sender"] = Bob, ["room_id"] = RoomId, ["content"] = content };

    /// <summary>Return the decision for Alice in a room of <paramref name="members"/>, where Bob may notify the room when <paramref name="bobMayNotify"/>.</summary>
    private static PushDecision Decide(JsonObject clientEvent, long members = 3, bool bobMayNotify = false, JsonObject? rules = null) =>
        PushRules.Evaluate(rules ?? Rules, clientEvent, new PushContext { UserId = Me, DisplayName = "Alice", MemberCount = members, SenderMayNotify = _ => bobMayNotify });

    /// <summary>Return the decision without its tweaks, for comparing.</summary>
    private static (bool Notify, bool Highlight, string? RuleId) Summary(PushDecision decision) => (decision.Notify, decision.Highlight, decision.RuleId);

    /// <summary>Messages notify; encrypted ones too; mentions are highlighted; notices, reactions and edits are not counted.</summary>
    [Fact]
    public void DecideDefaults()
    {
        // Ordinary and encrypted messages
        Assert.Equal((true, false, ".m.rule.message"), Summary(Decide(Message(new JsonObject { ["msgtype"] = "m.text", ["body"] = "hello" }))));
        Assert.Equal(".m.rule.room_one_to_one", Decide(Message(new JsonObject { ["msgtype"] = "m.text", ["body"] = "hi" }), members: 2).RuleId);
        Assert.True(Decide(Message(new JsonObject { ["algorithm"] = "m.megolm.v1.aes-sha2" }, "m.room.encrypted")).Notify);

        // Mentions: intentional, by display name, and by user name
        PushDecision mention = Decide(Message(new JsonObject { ["body"] = "hey", ["m.mentions"] = new JsonObject { ["user_ids"] = new JsonArray(Me) } }));
        Assert.Equal((true, true, ".m.rule.is_user_mention"), Summary(mention));
        Assert.Equal("default", mention.Tweaks["sound"]?.GetValue<string>());
        Assert.True(mention.Tweaks["highlight"]?.GetValue<bool>());
        Assert.True(Decide(Message(new JsonObject { ["body"] = "ping Alice, are you there?" })).Highlight);
        Assert.True(Decide(Message(new JsonObject { ["body"] = "is alice around" })).Highlight);
        Assert.False(Decide(Message(new JsonObject { ["body"] = "Malice is a word" })).Highlight);

        // With m.mentions, the old name rules no longer apply
        Assert.False(Decide(Message(new JsonObject { ["body"] = "Alice said", ["m.mentions"] = new JsonObject() })).Highlight);

        // Room mentions need permission
        JsonObject everyone = Message(new JsonObject { ["body"] = "all", ["m.mentions"] = new JsonObject { ["room"] = true } });
        Assert.True(Decide(everyone, bobMayNotify: true).Highlight);
        Assert.False(Decide(everyone, bobMayNotify: false).Highlight);

        // Not counted
        Assert.False(Decide(Message(new JsonObject { ["msgtype"] = "m.notice", ["body"] = "bot says" })).Notify);
        Assert.False(Decide(Message(new JsonObject { ["m.relates_to"] = new JsonObject { ["rel_type"] = "m.annotation", ["key"] = "👍" } }, "m.reaction")).Notify);
        Assert.False(Decide(Message(new JsonObject { ["body"] = "* fixed", ["m.relates_to"] = new JsonObject { ["rel_type"] = "m.replace" } })).Notify);
        Assert.False(Decide(new JsonObject { ["type"] = "m.room.member", ["sender"] = Bob, ["state_key"] = Bob, ["content"] = new JsonObject { ["membership"] = "join" } }).Notify);
        Assert.True(Decide(new JsonObject { ["type"] = "m.room.member", ["sender"] = Bob, ["state_key"] = Me, ["content"] = new JsonObject { ["membership"] = "invite" } }).Notify);
    }

    /// <summary>User rules win over the defaults of later kinds; a muted room and a disabled rule are respected; the master rule silences everything.</summary>
    [Fact]
    public void ApplyUserRules()
    {
        // A muted room
        JsonObject rules = PushRules.Defaults(Me);
        rules["room"]!.AsArray().Add(new JsonObject { ["rule_id"] = RoomId, ["default"] = false, ["enabled"] = true, ["actions"] = new JsonArray() });
        Assert.False(Decide(Message(new JsonObject { ["body"] = "hello" }), rules: rules).Notify);
        Assert.True(Decide(Message(new JsonObject { ["body"] = "hello Alice" }), rules: rules).Highlight);

        // The master rule
        rules["override"]![0]!["enabled"] = true;
        Assert.Equal((false, false, ".m.rule.master"), Summary(Decide(Message(new JsonObject { ["body"] = "hello Alice" }), rules: rules)));
    }
}
