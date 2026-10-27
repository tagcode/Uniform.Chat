namespace Uniform.Matrix.Protocol.Server;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>What the push rules say about one event for one user: whether it counts as a notification, and whether it is highlighted, e.g. as a mention.</summary>
/// <param name="Notify">Whether the event notifies, counting in <c>notification_count</c>.</param>
/// <param name="Highlight">Whether it is highlighted, counting in <c>highlight_count</c> too.</param>
/// <param name="RuleId">The rule that decided, or null when none matched.</param>
public sealed record PushDecision(bool Notify, bool Highlight, string? RuleId)
{
    /// <summary>No rule matched: nothing happens.</summary>
    public static PushDecision None { get; } = new(false, false, null);

    /// <summary>Tweaks of the deciding rule's actions by name, e.g. <c>sound</c>: <c>default</c>, as push gateways receive them.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Tweaks { get; init; } = new Dictionary<string, JsonNode?>();
}

/// <summary>Facts about the room and the user that push rule conditions need, besides the event itself.</summary>
public sealed record PushContext
{
    /// <summary>The user the rules belong to.</summary>
    public required string UserId { get; init; }
    /// <summary>The user's display name in the room, for <c>contains_display_name</c>; null when none.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Joined members of the room, for <c>room_member_count</c>.</summary>
    public long MemberCount { get; init; }
    /// <summary>Whether the event's sender may notify the whole room for a key such as <c>room</c>, for <c>sender_notification_permission</c>.</summary>
    public Func<string, bool> SenderMayNotify { get; init; } = _ => false;
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#push-rules">Push rules</see>: the server-default rule set and the evaluation of a rule set against an event.</summary>
/// <remarks>Rules are JSON as clients see them in <c>m.push_rules</c>: kinds <c>override</c>, <c>content</c>, <c>room</c>, <c>sender</c> and <c>underride</c>, tried in that order; the first enabled rule that matches decides.</remarks>
public static partial class PushRules
{
    /// <summary>Kinds in evaluation order.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["override", "content", "room", "sender", "underride"];

    /// <summary>Rule IDs of the deprecated mention rules, which do not apply to events that carry <c>m.mentions</c>.</summary>
    private static readonly HashSet<string> LegacyMentionRules = [".m.rule.contains_display_name", ".m.rule.roomnotif", ".m.rule.contains_user_name"];

    /// <summary>Return the server-default rule set of <paramref name="userId"/>, as in the spec's predefined rules.</summary>
    public static JsonObject Defaults(string userId)
    {
        // Common actions
        JsonArray Notify() => ["notify"];
        JsonArray NotifySound(string sound = "default") => ["notify", new JsonObject { ["set_tweak"] = "sound", ["value"] = sound }];
        JsonArray Highlight(bool sound) => sound ? ["notify", new JsonObject { ["set_tweak"] = "sound", ["value"] = "default" }, new JsonObject { ["set_tweak"] = "highlight" }] : ["notify", new JsonObject { ["set_tweak"] = "highlight" }];
        JsonObject Match(string key, string pattern) => new() { ["kind"] = "event_match", ["key"] = key, ["pattern"] = pattern };
        JsonObject Is(string key, JsonNode? value) => new() { ["kind"] = "event_property_is", ["key"] = key, ["value"] = value };
        JsonObject Rule(string id, JsonArray actions, params JsonObject[] conditions) => new() { ["rule_id"] = id, ["default"] = true, ["enabled"] = true, ["conditions"] = new JsonArray([.. conditions]), ["actions"] = actions };
        string localpart = userId.TrimStart('@').Split(':')[0];

        // Return rules by kind
        JsonObject master = Rule(".m.rule.master", []);
        master["enabled"] = false;
        return new JsonObject
        {
            ["override"] = new JsonArray(
                master,
                Rule(".m.rule.suppress_notices", [], Match("content.msgtype", "m.notice")),
                Rule(".m.rule.invite_for_me", NotifySound(), Match("type", "m.room.member"), Match("content.membership", "invite"), Match("state_key", userId)),
                Rule(".m.rule.member_event", [], Match("type", "m.room.member")),
                Rule(".m.rule.is_user_mention", Highlight(sound: true), new JsonObject { ["kind"] = "event_property_contains", ["key"] = @"content.m\.mentions.user_ids", ["value"] = userId }),
                Rule(".m.rule.contains_display_name", Highlight(sound: true), new JsonObject { ["kind"] = "contains_display_name" }),
                Rule(".m.rule.is_room_mention", Highlight(sound: false), Is(@"content.m\.mentions.room", true), new JsonObject { ["kind"] = "sender_notification_permission", ["key"] = "room" }),
                Rule(".m.rule.roomnotif", Highlight(sound: false), new JsonObject { ["kind"] = "sender_notification_permission", ["key"] = "room" }, Match("content.body", "@room")),
                Rule(".m.rule.tombstone", Highlight(sound: false), Match("type", "m.room.tombstone"), Match("state_key", "")),
                Rule(".m.rule.reaction", [], Match("type", "m.reaction")),
                Rule(".m.rule.room.server_acl", [], Match("type", "m.room.server_acl"), Match("state_key", "")),
                Rule(".m.rule.suppress_edits", [], Is(@"content.m\.relates_to.rel_type", "m.replace"))),
            ["content"] = new JsonArray(new JsonObject { ["rule_id"] = ".m.rule.contains_user_name", ["default"] = true, ["enabled"] = true, ["pattern"] = localpart, ["actions"] = Highlight(sound: true) }),
            ["room"] = new JsonArray(),
            ["sender"] = new JsonArray(),
            ["underride"] = new JsonArray(
                Rule(".m.rule.call", NotifySound("ring"), Match("type", "m.call.invite")),
                Rule(".m.rule.encrypted_room_one_to_one", NotifySound(), new JsonObject { ["kind"] = "room_member_count", ["is"] = "2" }, Match("type", "m.room.encrypted")),
                Rule(".m.rule.room_one_to_one", NotifySound(), new JsonObject { ["kind"] = "room_member_count", ["is"] = "2" }, Match("type", "m.room.message")),
                Rule(".m.rule.message", Notify(), Match("type", "m.room.message")),
                Rule(".m.rule.encrypted", Notify(), Match("type", "m.room.encrypted"))),
        };
    }

    /// <summary>Return what <paramref name="ruleset"/> decides for <paramref name="clientEvent"/>: the actions of the first enabled rule that matches.</summary>
    /// <param name="ruleset">Rules by kind, as in <c>m.push_rules</c> under <c>global</c>.</param>
    /// <param name="clientEvent">The event in client format, with <c>type</c>, <c>sender</c>, <c>room_id</c> and <c>content</c>.</param>
    /// <param name="context">The user and the room.</param>
    public static PushDecision Evaluate(JsonObject ruleset, JsonObject clientEvent, PushContext context)
    {
        // Kinds in order; legacy mention rules skip events with intentional mentions
        bool hasMentions = clientEvent["content"] is JsonObject content && content.ContainsKey("m.mentions");
        foreach (string kind in Kinds)
        {
            if (ruleset[kind] is not JsonArray rules) continue;
            foreach (JsonObject rule in rules.OfType<JsonObject>())
            {
                if (rule["enabled"]?.GetValue<bool>() == false) continue;
                string ruleId = rule["rule_id"]?.GetValue<string>() ?? "";
                if (hasMentions && LegacyMentionRules.Contains(ruleId)) continue;
                if (!Matches(kind, ruleId, rule, clientEvent, context)) continue;
                return Decide(rule["actions"] as JsonArray, ruleId);
            }
        }
        // Return no match
        return PushDecision.None;
    }

    /// <summary>Return whether a rule of <paramref name="kind"/> matches the event.</summary>
    private static bool Matches(string kind, string ruleId, JsonObject rule, JsonObject clientEvent, PushContext context) => kind switch
    {
        "content" => rule["pattern"]?.GetValue<string>() is string pattern && GetString(clientEvent, "content.body") is string body && GlobMatches(pattern, body, wordBoundary: true),
        "room" => GetString(clientEvent, "room_id") == ruleId,
        "sender" => GetString(clientEvent, "sender") == ruleId,
        _ => (rule["conditions"] as JsonArray ?? []).OfType<JsonObject>().All(condition => ConditionMatches(condition, clientEvent, context)),
    };

    /// <summary>Return whether one condition holds; unknown kinds never hold, as the spec asks.</summary>
    private static bool ConditionMatches(JsonObject condition, JsonObject clientEvent, PushContext context)
    {
        // By kind
        string key = condition["key"]?.GetValue<string>() ?? "";
        switch (condition["kind"]?.GetValue<string>())
        {
            case "event_match":
                return condition["pattern"]?.GetValue<string>() is string pattern && GetString(clientEvent, key) is string value && GlobMatches(pattern, value, wordBoundary: key == "content.body");
            case "event_property_is":
                return GetProperty(clientEvent, key) is JsonNode property && property is JsonValue && JsonNode.DeepEquals(property, condition["value"]);
            case "event_property_contains":
                return GetProperty(clientEvent, key) is JsonArray array && array.Any(item => item is JsonValue && JsonNode.DeepEquals(item, condition["value"]));
            case "contains_display_name":
                return context.DisplayName is { Length: > 0 } name && GetString(clientEvent, "content.body") is string body && GlobMatches(name, body, wordBoundary: true, literal: true);
            case "room_member_count":
                return condition["is"]?.GetValue<string>() is string comparison && CompareCount(comparison, context.MemberCount);
            case "sender_notification_permission":
                return key.Length > 0 && context.SenderMayNotify(key);
            default:
                return false;
        }
    }

    /// <summary>Return the decision of a rule's actions: <c>notify</c>, and the <c>highlight</c> tweak, which is true unless its value says otherwise.</summary>
    private static PushDecision Decide(JsonArray? actions, string ruleId)
    {
        // Legacy actions such as dont_notify and coalesce mean nothing more
        bool notify = false, highlight = false;
        Dictionary<string, JsonNode?> tweaks = [];
        foreach (JsonNode? action in actions ?? [])
        {
            if (action is JsonValue value && value.TryGetValue(out string? name) && name == "notify") notify = true;
            if (action is not JsonObject tweak || tweak["set_tweak"]?.GetValue<string>() is not string tweakName) continue;
            if (tweakName == "highlight") highlight = tweak["value"] is not JsonValue flag || !flag.TryGetValue(out bool on) || on;
            tweaks[tweakName] = tweak["value"]?.DeepClone() ?? (tweakName == "highlight" ? JsonValue.Create(true) : null);
        }
        // Return decision
        return new PushDecision(notify, notify && highlight, ruleId) { Tweaks = tweaks };
    }

    /// <summary>Return the property at a dotted <paramref name="key"/>, where <c>\.</c> is a dot inside a name and <c>\\</c> a backslash; null when missing.</summary>
    private static JsonNode? GetProperty(JsonObject clientEvent, string key)
    {
        // Walk the path
        JsonNode? node = clientEvent;
        foreach (string part in SplitKey(key))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node)) return null;
        }
        // Return property
        return node;
    }

    /// <summary>Return the string at <paramref name="key"/>, or null when it is missing or not a string.</summary>
    private static string? GetString(JsonObject clientEvent, string key) => GetProperty(clientEvent, key) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Split a dotted key at the dots that are not escaped.</summary>
    private static IEnumerable<string> SplitKey(string key)
    {
        // One pass, unescaping \. and \\
        StringBuilder part = new();
        for (int i = 0; i < key.Length; i++)
        {
            if (key[i] == '\\' && i + 1 < key.Length && key[i + 1] is '.' or '\\')
            {
                part.Append(key[++i]);
                continue;
            }
            if (key[i] == '.')
            {
                yield return part.ToString();
                part.Clear();
                continue;
            }
            part.Append(key[i]);
        }
        // Done
        yield return part.ToString();
    }

    /// <summary>Return whether a glob matches, case-insensitively: <c>*</c> any text, <c>?</c> one character; with <paramref name="wordBoundary"/>, anywhere in the text between word boundaries.</summary>
    /// <param name="pattern">Glob, or plain text when <paramref name="literal"/>.</param>
    /// <param name="text">Value of the event.</param>
    /// <param name="wordBoundary">Whether to match a whole word or words inside the text, as for <c>content.body</c>, instead of the whole text.</param>
    /// <param name="literal">Whether <paramref name="pattern"/> has no wildcards, as for display names.</param>
    private static bool GlobMatches(string pattern, string text, bool wordBoundary, bool literal = false)
    {
        // Glob to regex
        string body = literal ? Regex.Escape(pattern) : Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        string regex = wordBoundary ? $@"(^|\W){body}($|\W)" : $"^{body}$";
        // Return match
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Return whether <paramref name="count"/> satisfies <c>==2</c>, <c>&lt;3</c>, <c>&gt;=10</c> or a bare number.</summary>
    private static bool CompareCount(string comparison, long count)
    {
        // Operator, then number
        Match match = CountComparison().Match(comparison.Trim());
        if (!match.Success || !long.TryParse(match.Groups[2].Value, out long number)) return false;
        // Return comparison
        return match.Groups[1].Value switch
        {
            "<" => count < number,
            ">" => count > number,
            "<=" => count <= number,
            ">=" => count >= number,
            _ => count == number,
        };
    }

    /// <summary>Match the operator and number of a member count condition.</summary>
    [GeneratedRegex(@"^(==|<=|>=|<|>)?\s*(\d+)$")]
    private static partial Regex CountComparison();
}
