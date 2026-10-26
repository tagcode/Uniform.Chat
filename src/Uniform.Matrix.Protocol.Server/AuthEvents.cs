namespace Uniform.Matrix.Protocol.Server;

using System.Text.Json.Nodes;

/// <summary>Event types that take part in authorization.</summary>
public static class AuthEventTypes
{
    /// <summary>Room creation.</summary>
    public const string Create = "m.room.create";
    /// <summary>Membership.</summary>
    public const string Member = "m.room.member";
    /// <summary>Join rules.</summary>
    public const string JoinRules = "m.room.join_rules";
    /// <summary>Power levels.</summary>
    public const string PowerLevels = "m.room.power_levels";
    /// <summary>Third-party invite.</summary>
    public const string ThirdPartyInvite = "m.room.third_party_invite";
}

/// <summary>The <see href="https://spec.matrix.org/latest/server-server-api/#auth-events-selection">auth events selection</see>: which state an event's authorization depends on.</summary>
public static class AuthEvents
{
    /// <summary>Return the state keys whose current events must be an event's <c>auth_events</c>; empty for <c>m.room.create</c>.</summary>
    public static IReadOnlyList<(string Type, string StateKey)> Select(string type, string? stateKey, string sender, JsonObject content)
    {
        // The create event has no auth events
        if (type == AuthEventTypes.Create) return [];
        List<(string, string)> keys = [(AuthEventTypes.Create, ""), (AuthEventTypes.PowerLevels, ""), (AuthEventTypes.Member, sender)];
        if (type != AuthEventTypes.Member || stateKey is null) return keys;

        // Membership changes also depend on the target and the join rules
        string? membership = content["membership"]?.GetValue<string>();
        if (stateKey != sender) keys.Add((AuthEventTypes.Member, stateKey));
        if (membership is "join" or "invite" or "knock") keys.Add((AuthEventTypes.JoinRules, ""));
        if (membership == "invite" && content["third_party_invite"]?["signed"]?["token"]?.GetValue<string>() is string token) keys.Add((AuthEventTypes.ThirdPartyInvite, token));
        if (content["join_authorised_via_users_server"]?.GetValue<string>() is string via && via != sender && via != stateKey) keys.Add((AuthEventTypes.Member, via));
        // Return keys
        return keys;
    }
}
