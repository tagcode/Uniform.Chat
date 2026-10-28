namespace Uniform.Matrix.Protocol.Server;

using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>The <see href="https://spec.matrix.org/latest/rooms/v11/#authorization-rules">authorization rules</see> of room versions 10, 11 and <see href="https://spec.matrix.org/latest/rooms/v12/#authorization-rules">12</see>: whether an event is allowed given the state it depends on.</summary>
/// <remarks>The caller supplies the state: an event's <c>auth_events</c> when receiving it, or the room's current state when creating it. State events carry their <c>event_id</c>, which v4+ event formats otherwise leave out, as the creator's first join is checked against the create event's ID. Rule numbers follow the spec.</remarks>
public static class AuthRules
{
    private static readonly string[] IntegerFields = ["users_default", "events_default", "state_default", "ban", "redact", "kick", "invite"];

    /// <summary>Return null when <paramref name="pdu"/> is allowed, or why it is rejected.</summary>
    /// <param name="pdu">The event.</param>
    /// <param name="version">Room version.</param>
    /// <param name="state">Auth state by type and state key.</param>
    /// <param name="isSignedByServer">Returns whether the event is validly signed by a server, for restricted joins.</param>
    public static string? Check(JsonObject pdu, RoomVersion version, IReadOnlyDictionary<(string Type, string StateKey), JsonObject> state, Func<JsonObject, string, bool>? isSignedByServer = null)
    {
        // Fields every rule needs
        string type = pdu["type"]?.GetValue<string>() ?? "";
        string sender = pdu["sender"]?.GetValue<string>() ?? "";
        string? stateKey = pdu["state_key"]?.GetValue<string>();
        JsonObject content = pdu["content"] as JsonObject ?? [];

        // 1. Create
        if (type == AuthEventTypes.Create) return CheckCreate(pdu, version, sender, content);

        // 2. Auth events must include the create event of this room; from version 12 the room ID names it instead
        if (!state.TryGetValue((AuthEventTypes.Create, ""), out JsonObject? create)) return "No create event.";
        string? roomId = pdu["room_id"]?.GetValue<string>();
        if (version.RoomIdIsCreateHash)
        {
            string? createId = create["event_id"]?.GetValue<string>();
            if (createId is null || roomId != RoomVersion.RoomIdOf(createId)) return "The room ID is not of the create event.";
            if ((pdu["auth_events"] as JsonArray ?? []).Any(e => e?.GetValue<string>() == createId)) return "The create event must not be an auth event.";
        }
        if (state.Values.Any(e => e != create && e["room_id"]?.GetValue<string>() != roomId) || !version.RoomIdIsCreateHash && create["room_id"]?.GetValue<string>() != roomId) return "An auth event is from another room.";
        string creator = version.CreateHasCreator ? create["content"]?["creator"]?.GetValue<string>() ?? "" : create["sender"]?.GetValue<string>() ?? "";
        IReadOnlyCollection<string>? creators = version.RoomIdIsCreateHash ? Creators(create) : null;

        // 3. Unfederated rooms stay on the creator's server
        if (create["content"]?["m.federate"]?.GetValueKind() == JsonValueKind.False && Domain(sender) != Domain(create["sender"]?.GetValue<string>())) return "The room does not federate.";

        // 4. Membership
        PowerLevels levels = new(state.GetValueOrDefault((AuthEventTypes.PowerLevels, ""))?["content"] as JsonObject, creator, creators);
        if (type == AuthEventTypes.Member) return CheckMember(pdu, sender, stateKey, content, state, levels, create, creator, isSignedByServer);

        // 5. Everything else needs a joined sender
        if (Membership(state, sender) != "join") return "The sender is not in the room.";

        // 6. Third-party invites
        if (type == AuthEventTypes.ThirdPartyInvite) return levels.UserLevel(sender) >= levels.Invite ? null : "Not allowed to invite.";

        // 7. Event level
        if (levels.EventLevel(type, stateKey is not null) > levels.UserLevel(sender)) return $"Power level too low for {type}.";

        // 8. State keys of other users
        if (stateKey is not null && stateKey.StartsWith('@') && stateKey != sender) return "Cannot set state of another user.";

        // 9. Power levels
        if (type == AuthEventTypes.PowerLevels && creators is not null && content["users"] is JsonObject listed && listed.Any(u => creators.Contains(u.Key))) return "Creators cannot be given a power level.";
        if (type == AuthEventTypes.PowerLevels) return CheckPowerLevels(content, sender, levels, state.GetValueOrDefault((AuthEventTypes.PowerLevels, ""))?["content"] as JsonObject);

        // 10. Allowed
        return null;
    }

    /// <summary>Return the IDs of <paramref name="events"/> that the rules allow against their own <c>auth_events</c>, each of which must be among them and allowed too; for state and auth chains received from another server, whose signatures were checked.</summary>
    /// <param name="version">Room version.</param>
    /// <param name="events">Events by ID, without <c>event_id</c>.</param>
    public static HashSet<string> CheckAuthChains(RoomVersion version, IReadOnlyDictionary<string, JsonObject> events)
    {
        // Auth events before the events they authorize
        HashSet<string> allowed = [];
        HashSet<string> visited = [];
        string? createId = events.FirstOrDefault(e => e.Value["type"]?.GetValue<string>() == AuthEventTypes.Create).Key;
        // The create event first, as version 12 events do not list it
        IEnumerable<string> roots = createId is null ? events.Keys : events.Keys.Where(id => id != createId).Prepend(createId);
        foreach (string root in roots)
        {
            // Depth first without recursion: an event is checked once its auth events are
            Stack<(string Id, bool Ready)> pending = new([(root, false)]);
            while (pending.TryPop(out (string Id, bool Ready) step))
            {
                if (!step.Ready)
                {
                    if (!visited.Add(step.Id)) continue;
                    pending.Push((step.Id, true));
                    foreach (string authId in AuthIds(events[step.Id]).Where(events.ContainsKey)) pending.Push((authId, false));
                    continue;
                }
                if (IsAllowed(version, events, step.Id, createId, allowed)) allowed.Add(step.Id);
            }
        }
        // Return allowed events
        return allowed;
    }

    /// <summary>Return whether event <paramref name="id"/> is allowed against its auth events, all of which must be in <paramref name="allowed"/>.</summary>
    private static bool IsAllowed(RoomVersion version, IReadOnlyDictionary<string, JsonObject> events, string id, string? createId, HashSet<string> allowed)
    {
        // Auth state of allowed auth events, keyed once each; version 12 names the create event by room ID instead
        JsonObject pdu = events[id];
        Dictionary<(string Type, string StateKey), JsonObject> state = [];
        foreach (string authId in AuthIds(pdu))
        {
            if (!allowed.Contains(authId)) return false;
            JsonObject authEvent = events[authId].DeepClone().AsObject();
            authEvent["event_id"] = authId;
            if (authEvent["type"]?.GetValue<string>() is not string type || authEvent["state_key"]?.GetValue<string>() is not string stateKey || !state.TryAdd((type, stateKey), authEvent)) return false;
        }
        if (version.RoomIdIsCreateHash && createId is not null && createId != id && allowed.Contains(createId))
        {
            JsonObject create = events[createId].DeepClone().AsObject();
            create["event_id"] = createId;
            state.TryAdd((AuthEventTypes.Create, ""), create);
        }
        // Return verdict; signatures were checked on arrival
        return Check(pdu, version, state, (_, _) => true) is null;
    }

    /// <summary>Return the <c>auth_events</c> of a PDU.</summary>
    private static IEnumerable<string> AuthIds(JsonObject pdu) => (pdu["auth_events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>();

    /// <summary>Rule 1: the create event.</summary>
    private static string? CheckCreate(JsonObject pdu, RoomVersion version, string sender, JsonObject content)
    {
        // No history, same server as the room ID or from version 12 no room ID, known version
        if (pdu["prev_events"] is JsonArray { Count: > 0 }) return "A create event has no previous events.";
        if (version.RoomIdIsCreateHash && pdu.ContainsKey("room_id")) return "A version 12 create event has no room ID.";
        if (!version.RoomIdIsCreateHash && Domain(pdu["room_id"]?.GetValue<string>()) != Domain(sender)) return "The room ID is not of the creator's server.";
        if (version.RoomIdIsCreateHash && content["additional_creators"] is JsonNode additional && (additional is not JsonArray list || list.Any(c => c is not JsonValue v || !v.TryGetValue(out string? id) || !UserId.TryParse(id, out _)))) return "additional_creators must be user IDs.";
        if (content["room_version"]?.GetValue<string>() is string id && RoomVersion.Find(id) is null) return $"Unknown room version {id}.";
        if (version.CreateHasCreator && content["creator"]?.GetValue<string>() is null) return "The create event has no creator.";
        // Done
        return null;
    }

    /// <summary>Rule 4: membership events.</summary>
    private static string? CheckMember(JsonObject pdu, string sender, string? target, JsonObject content, IReadOnlyDictionary<(string, string), JsonObject> state, PowerLevels levels, JsonObject create, string creator, Func<JsonObject, string, bool>? isSignedByServer)
    {
        // a. Target and membership
        if (target is null || content["membership"]?.GetValue<string>() is not string membership) return "Membership events need a state key and membership.";

        // b. Restricted joins vouched for by a server
        string? via = content["join_authorised_via_users_server"]?.GetValue<string>();
        if (via is not null && isSignedByServer?.Invoke(pdu, Domain(via) ?? "") != true) return "Not signed by the authorising server.";

        // Current memberships and join rule
        string? senderMembership = Membership(state, sender);
        string? targetMembership = Membership(state, target);
        string joinRule = state.GetValueOrDefault((AuthEventTypes.JoinRules, ""))?["content"]?["join_rule"]?.GetValue<string>() ?? "invite";
        long senderLevel = levels.UserLevel(sender);
        long targetLevel = levels.UserLevel(target);

        // By membership
        switch (membership)
        {
            case "join":
                // The creator's first join
                JsonArray previous = pdu["prev_events"] as JsonArray ?? [];
                if (previous.Count == 1 && previous[0]?.GetValue<string>() == create["event_id"]?.GetValue<string>() && target == creator) return null;
                if (sender != target) return "Cannot join for someone else.";
                if (senderMembership == "ban") return "The user is banned.";
                if (joinRule is "invite" or "knock") return targetMembership is "invite" or "join" ? null : "The room is invite-only.";
                if (joinRule is "restricted" or "knock_restricted")
                {
                    if (targetMembership is "invite" or "join") return null;
                    if (via is null || Membership(state, via) != "join" || levels.UserLevel(via) < levels.Invite) return "The join is not authorised by a room member.";
                    return null;
                }
                return joinRule == "public" ? null : "The room is not public.";

            case "invite":
                if (content["third_party_invite"] is JsonObject thirdParty) return CheckThirdPartyInvite(thirdParty, target, targetMembership, state);
                if (senderMembership != "join") return "The inviter is not in the room.";
                if (targetMembership is "join" or "ban") return "The user is already joined or banned.";
                return senderLevel >= levels.Invite ? null : "Not allowed to invite.";

            case "leave":
                if (sender == target) return senderMembership is "invite" or "join" or "knock" ? null : "Not in the room.";
                if (senderMembership != "join") return "The sender is not in the room.";
                if (targetMembership == "ban" && senderLevel < levels.Ban) return "Not allowed to unban.";
                return senderLevel >= levels.Kick && targetLevel < senderLevel ? null : "Not allowed to kick.";

            case "ban":
                if (senderMembership != "join") return "The sender is not in the room.";
                return senderLevel >= levels.Ban && targetLevel < senderLevel ? null : "Not allowed to ban.";

            case "knock":
                if (joinRule is not ("knock" or "knock_restricted")) return "The room does not allow knocking.";
                if (sender != target) return "Cannot knock for someone else.";
                return senderMembership is not ("ban" or "invite" or "join") ? null : "Cannot knock now.";

            default:
                return $"Unknown membership {membership}.";
        }
    }

    /// <summary>Rule 4.d.i: an invite for a third-party identifier, proven by a signature of the identity server's key.</summary>
    private static string? CheckThirdPartyInvite(JsonObject thirdParty, string target, string? targetMembership, IReadOnlyDictionary<(string, string), JsonObject> state)
    {
        // Signed object naming the target and a token
        if (targetMembership == "ban") return "The user is banned.";
        if (thirdParty["signed"] is not JsonObject signed || signed["mxid"]?.GetValue<string>() is not string mxid || signed["token"]?.GetValue<string>() is not string token) return "Invalid third-party invite.";
        if (mxid != target) return "The third-party invite is for another user.";
        if (!state.TryGetValue((AuthEventTypes.ThirdPartyInvite, token), out JsonObject? invite)) return "No matching third-party invite.";

        // Any signature by one of the invite's public keys
        JsonObject inviteContent = invite["content"] as JsonObject ?? [];
        List<string> keys = [.. (inviteContent["public_keys"] as JsonArray ?? []).Select(k => k?["public_key"]?.GetValue<string>()).OfType<string>()];
        if (inviteContent["public_key"]?.GetValue<string>() is string single) keys.Add(single);
        string signingForm = CanonicalJson.SigningForm(signed);
        bool valid = (signed["signatures"] as JsonObject ?? []).SelectMany(s => s.Value as JsonObject ?? []).Any(sig => sig.Value?.GetValue<string>() is string value && keys.Any(k => Ed25519.Verify(k, signingForm, value)));
        // Return result
        return valid ? null : "The third-party invite signature is invalid.";
    }

    /// <summary>Rule 9: power level changes stay within the sender's own level.</summary>
    private static string? CheckPowerLevels(JsonObject content, string sender, PowerLevels current, JsonObject? old)
    {
        // a. Integer values only
        foreach (string field in IntegerFields)
        {
            if (content[field] is JsonNode node && !IsInteger(node)) return $"{field} is not an integer.";
        }
        foreach (string map in new[] { "events", "notifications", "users" })
        {
            if (content[map] is null) continue;
            if (content[map] is not JsonObject values || values.Any(v => v.Value is null || !IsInteger(v.Value))) return $"{map} must map to integers.";
        }
        if (content["users"] is JsonObject users && users.Any(u => !UserId.TryParse(u.Key, out _))) return "users must be keyed by user IDs.";

        // b. First power levels
        if (old is null) return null;
        long senderLevel = current.UserLevel(sender);

        // c. Top-level fields
        foreach (string field in IntegerFields)
        {
            long? before = old[field] is JsonNode b ? PowerLevels.AsLong(b) : null;
            long? after = content[field] is JsonNode a ? PowerLevels.AsLong(a) : null;
            if (before == after) continue;
            if (before > senderLevel || after > senderLevel) return $"Cannot change {field} beyond own level.";
        }

        // d. Event and notification levels
        foreach (string map in new[] { "events", "notifications" })
        {
            string? error = CheckMapChanges(old[map] as JsonObject, content[map] as JsonObject, (_, before, after) =>
                before is long b && b > senderLevel || after is long a && a > senderLevel ? $"Cannot change {map} beyond own level." : null);
            if (error is not null) return error;
        }

        // e. User levels; others at or above own level are untouchable
        // Return result
        return CheckMapChanges(old["users"] as JsonObject, content["users"] as JsonObject, (user, before, after) =>
        {
            if (before is long b && user != sender && b >= senderLevel) return "Cannot change the level of a user at or above own level.";
            return after is long a && a > senderLevel ? "Cannot raise a user above own level." : null;
        });
    }

    /// <summary>Run <paramref name="check"/> for each key added, changed or removed between two maps; return the first error.</summary>
    private static string? CheckMapChanges(JsonObject? before, JsonObject? after, Func<string, long?, long?, string?> check)
    {
        // Union of keys
        IEnumerable<string> keys = (before ?? []).Select(k => k.Key).Union((after ?? []).Select(k => k.Key));
        foreach (string key in keys)
        {
            long? old = before?[key] is JsonNode o ? PowerLevels.AsLong(o) : null;
            long? updated = after?[key] is JsonNode n ? PowerLevels.AsLong(n) : null;
            if (old == updated) continue;
            if (check(key, old, updated) is string error) return error;
        }
        // Done
        return null;
    }

    /// <summary>Return the creators of a version 12 room: the create event's sender and its <c>additional_creators</c>.</summary>
    public static IReadOnlyCollection<string> Creators(JsonObject create) =>
        [.. new[] { create["sender"]?.GetValue<string>() }.Concat((create["content"]?["additional_creators"] as JsonArray ?? []).Select(c => c is JsonValue v && v.TryGetValue(out string? id) ? id : null)).OfType<string>().Distinct()];

    /// <summary>Return the current membership of <paramref name="userId"/>, or null.</summary>
    private static string? Membership(IReadOnlyDictionary<(string, string), JsonObject> state, string userId) => state.GetValueOrDefault((AuthEventTypes.Member, userId))?["content"]?["membership"]?.GetValue<string>();

    /// <summary>Return whether a JSON value is an integer.</summary>
    private static bool IsInteger(JsonNode node) => PowerLevels.TryGetInteger(node, out _);

    /// <summary>Return the server part of a user or room ID.</summary>
    private static string? Domain(string? id) => id is null ? null : id.IndexOf(':') is int i and >= 0 ? id[(i + 1)..] : null;
}
