namespace Uniform.Matrix.Protocol.Server;

using System.Text.Json.Nodes;

/// <summary><see href="https://spec.matrix.org/latest/rooms/v2/#state-resolution">State resolution v2</see>, and its <see href="https://spec.matrix.org/latest/rooms/v12/#state-resolution">v2.1</see> form in room version 12: the room's state where the room's history has forks, from the states of each branch.</summary>
/// <remarks>Conflicted power events (power levels, join rules, kicks and bans) are applied first, in an order that favours senders with more power; the other conflicted events follow, ordered by the power level events they were sent under. Every event is checked with <see cref="AuthRules"/> against what has been resolved so far, so events that lose their authorization drop out.</remarks>
public static class StateResolution
{
    /// <summary>Return the state of <paramref name="stateSets"/> merged.</summary>
    /// <param name="version">Room version.</param>
    /// <param name="stateSets">State of each branch: event IDs by type and state key.</param>
    /// <param name="events">Events by ID: those of the state sets and their auth chains; missing ones are left out.</param>
    public static Dictionary<(string Type, string StateKey), string> Resolve(RoomVersion version, IReadOnlyList<IReadOnlyDictionary<(string Type, string StateKey), string>> stateSets, IReadOnlyDictionary<string, JsonObject> events)
    {
        // Nothing to merge with one state
        if (stateSets.Count == 0) return [];
        if (stateSets.Count == 1) return new Dictionary<(string, string), string>(stateSets[0]);

        // Unconflicted keys have one value in every set; others are conflicted, also when absent from some
        Dictionary<(string, string), string> unconflicted = [];
        HashSet<string> conflicted = [];
        foreach ((string, string) key in stateSets.SelectMany(s => s.Keys).Distinct())
        {
            List<string?> values = [.. stateSets.Select(s => s.GetValueOrDefault(key))];
            if (values.All(v => v is not null && v == values[0])) unconflicted[key] = values[0]!;
            else conflicted.UnionWith(values.OfType<string>());
        }
        if (conflicted.Count == 0) return unconflicted;

        // Full conflicted set: the conflicted events and the auth difference, and in v2.1 the events between conflicted ones
        Graph graph = new(events);
        HashSet<string> full = [.. conflicted.Concat(graph.AuthDifference(stateSets)).Where(events.ContainsKey)];
        if (version.RoomIdIsCreateHash) full.UnionWith(graph.ConflictedSubgraph(conflicted));

        // Power events and the conflicted events behind them, strongest senders first, checked from the unconflicted state (v2.1: from nothing)
        HashSet<string> power = [.. full.Where(id => IsPowerEvent(events[id]))];
        power.UnionWith([.. power.SelectMany(graph.AuthChain).Where(full.Contains)]);
        Dictionary<(string, string), string> resolved = version.RoomIdIsCreateHash ? [] : new(unconflicted);
        AuthCheck(version, graph, resolved, graph.PowerOrder(version, power));

        // The rest by the power levels they were sent under
        List<string> mainline = graph.Mainline(resolved.GetValueOrDefault((AuthEventTypes.PowerLevels, "")));
        List<string> others = [.. full.Except(power).OrderBy(id => graph.MainlinePosition(id, mainline)).ThenBy(Timestamp(events)).ThenBy(id => id, StringComparer.Ordinal)];
        AuthCheck(version, graph, resolved, others);

        // Unconflicted state on top
        foreach (((string, string) key, string id) in unconflicted) resolved[key] = id;
        // Return state
        return resolved;
    }

    /// <summary>Return whether an event is a power event: power levels, join rules, or a kick or ban.</summary>
    private static bool IsPowerEvent(JsonObject pdu)
    {
        // By type, and memberships that remove someone else
        string? type = pdu["type"]?.GetValue<string>();
        if (pdu["state_key"]?.GetValue<string>() is not string stateKey) return false;
        if (type is AuthEventTypes.PowerLevels or AuthEventTypes.JoinRules) return stateKey.Length == 0;
        // Return kicks and bans
        return type == AuthEventTypes.Member && pdu["content"]?["membership"]?.GetValue<string>() is "leave" or "ban" && pdu["sender"]?.GetValue<string>() != stateKey;
    }

    /// <summary>Apply <paramref name="ordered"/> to <paramref name="state"/> one by one, each when the auth rules allow it against its auth events overridden by the state so far.</summary>
    private static void AuthCheck(RoomVersion version, Graph graph, Dictionary<(string, string), string> state, IEnumerable<string> ordered)
    {
        // Iterative auth checks
        foreach (string id in ordered)
        {
            JsonObject pdu = graph.Events[id];
            if (pdu["state_key"]?.GetValue<string>() is not string stateKey || pdu["type"]?.GetValue<string>() is not string type) continue;
            Dictionary<(string Type, string StateKey), JsonObject> authState = graph.AuthState(version, id);
            foreach ((string, string) key in AuthEvents.Select(type, stateKey, pdu["sender"]?.GetValue<string>() ?? "", pdu["content"] as JsonObject ?? []))
            {
                if (state.TryGetValue(key, out string? current) && graph.WithId(current) is JsonObject currentEvent) authState[key] = currentEvent;
            }
            // Signatures were checked when the events arrived
            if (AuthRules.Check(pdu, version, authState, (_, _) => true) is null) state[(type, stateKey)] = id;
        }
    }

    /// <summary>Return a key that sorts events by their <c>origin_server_ts</c>.</summary>
    private static Func<string, long> Timestamp(IReadOnlyDictionary<string, JsonObject> events) => id => events[id]["origin_server_ts"]?.GetValue<long>() ?? 0;

    /// <summary>The auth graph of the events: auth chains, power levels and orderings over it.</summary>
    private sealed class Graph(IReadOnlyDictionary<string, JsonObject> events)
    {
        private readonly Dictionary<string, HashSet<string>> chains = [];
        private readonly Dictionary<string, string?> creates = [];

        /// <summary>Events by ID.</summary>
        public IReadOnlyDictionary<string, JsonObject> Events => events;

        /// <summary>Return the known <c>auth_events</c> of an event.</summary>
        public IEnumerable<string> AuthIds(string id) => (events.GetValueOrDefault(id)?["auth_events"] as JsonArray ?? []).Select(e => e?.GetValue<string>()).OfType<string>().Where(events.ContainsKey);

        /// <summary>Return the auth chain of an event: its auth events and theirs, without the event itself.</summary>
        public HashSet<string> AuthChain(string id)
        {
            // Memoized, walking without recursion
            if (chains.TryGetValue(id, out HashSet<string>? known)) return known;
            HashSet<string> chain = [];
            Stack<string> next = new(AuthIds(id));
            while (next.TryPop(out string? current))
            {
                if (!chain.Add(current)) continue;
                if (chains.TryGetValue(current, out HashSet<string>? below)) chain.UnionWith(below);
                else foreach (string parent in AuthIds(current)) next.Push(parent);
            }
            chains[id] = chain;
            // Return chain
            return chain;
        }

        /// <summary>Return the events in the auth chains of some state sets but not all.</summary>
        public HashSet<string> AuthDifference(IReadOnlyList<IReadOnlyDictionary<(string Type, string StateKey), string>> stateSets)
        {
            // Union minus intersection of each set's combined chain
            List<HashSet<string>> setChains = [.. stateSets.Select(s => s.Values.Where(events.ContainsKey).SelectMany(AuthChain).ToHashSet())];
            HashSet<string> union = [.. setChains.SelectMany(c => c)];
            HashSet<string> intersection = new(setChains[0]);
            foreach (HashSet<string> chain in setChains.Skip(1)) intersection.IntersectWith(chain);
            union.ExceptWith(intersection);
            // Return difference
            return union;
        }

        /// <summary>Return the events that are ancestors of a conflicted event and descendants of another, along auth events.</summary>
        public IEnumerable<string> ConflictedSubgraph(HashSet<string> conflicted) =>
            conflicted.Where(events.ContainsKey).SelectMany(AuthChain).Distinct().Where(id => AuthChain(id).Overlaps(conflicted));

        /// <summary>Return the state an event's auth events give, with the create event in version 12, which names it by room ID.</summary>
        public Dictionary<(string Type, string StateKey), JsonObject> AuthState(RoomVersion version, string id)
        {
            // By type and state key
            Dictionary<(string, string), JsonObject> state = [];
            foreach (string authId in AuthIds(id))
            {
                JsonObject authEvent = WithId(authId)!;
                if (authEvent["type"]?.GetValue<string>() is string type && authEvent["state_key"]?.GetValue<string>() is string stateKey) state[(type, stateKey)] = authEvent;
            }
            if (version.RoomIdIsCreateHash && !state.ContainsKey((AuthEventTypes.Create, "")) && CreateOf(id) is string createId) state[(AuthEventTypes.Create, "")] = WithId(createId)!;
            // Return state
            return state;
        }

        /// <summary>Return a copy of an event with its <c>event_id</c>, which the auth rules read, or null when unknown.</summary>
        public JsonObject? WithId(string id)
        {
            // Copy, as the caller's events stay as they are
            if (!events.TryGetValue(id, out JsonObject? pdu)) return null;
            JsonObject copy = pdu.DeepClone().AsObject();
            copy["event_id"] = id;
            // Return copy
            return copy;
        }

        /// <summary>Return the power level of an event's sender under the event's auth events.</summary>
        public long SenderLevel(RoomVersion version, string id)
        {
            // Power levels and creators from the auth events
            Dictionary<(string Type, string StateKey), JsonObject> state = AuthState(version, id);
            JsonObject? create = state.GetValueOrDefault((AuthEventTypes.Create, ""));
            string? creator = version.CreateHasCreator ? create?["content"]?["creator"]?.GetValue<string>() : create?["sender"]?.GetValue<string>();
            PowerLevels levels = new(state.GetValueOrDefault((AuthEventTypes.PowerLevels, ""))?["content"] as JsonObject, creator, version.RoomIdIsCreateHash && create is not null ? AuthRules.Creators(create) : null);
            // Return level
            return levels.UserLevel(events[id]["sender"]?.GetValue<string>() ?? "");
        }

        /// <summary>Return <paramref name="power"/> in reverse topological power order: auth events before the events they authorize, and among those ready, the strongest sender, then the earliest, then the lowest ID.</summary>
        public List<string> PowerOrder(RoomVersion version, HashSet<string> power)
        {
            // Edges within the set, and each event's ordering key
            Dictionary<string, int> waiting = power.ToDictionary(id => id, id => AuthIds(id).Count(power.Contains));
            Dictionary<string, List<string>> children = power.ToDictionary(id => id, _ => new List<string>());
            foreach (string id in power) foreach (string parent in AuthIds(id).Where(power.Contains)) children[parent].Add(id);
            Dictionary<string, (long Level, long Ts)> keys = power.ToDictionary(id => id, id => (SenderLevel(version, id), events[id]["origin_server_ts"]?.GetValue<long>() ?? 0));
            Comparer<string> comparer = Comparer<string>.Create((x, y) =>
            {
                int byLevel = keys[y].Level.CompareTo(keys[x].Level);
                if (byLevel != 0) return byLevel;
                int byTs = keys[x].Ts.CompareTo(keys[y].Ts);
                return byTs != 0 ? byTs : string.CompareOrdinal(x, y);
            });

            // Kahn's algorithm, taking the first ready event each time
            SortedSet<string> ready = new(power.Where(id => waiting[id] == 0), comparer);
            List<string> order = [];
            while (ready.Min is string next)
            {
                ready.Remove(next);
                order.Add(next);
                foreach (string child in children[next]) if (--waiting[child] == 0) ready.Add(child);
            }
            // Return order
            return order;
        }

        /// <summary>Return the mainline of a power levels event: it and the power levels events before it, each from the previous one's auth events, oldest first.</summary>
        public List<string> Mainline(string? powerLevelsId)
        {
            // Walk back through each power levels event's own auth events
            List<string> mainline = [];
            for (string? current = powerLevelsId; current is not null && events.ContainsKey(current) && !mainline.Contains(current); current = AuthPowerLevels(current)) mainline.Add(current);
            mainline.Reverse();
            // Return oldest first
            return mainline;
        }

        /// <summary>Return the position on <paramref name="mainline"/> of the closest power levels event an event was sent under, counting from 1; 0 when none.</summary>
        public int MainlinePosition(string id, List<string> mainline)
        {
            // The event, then the power levels of each step's auth events
            HashSet<string> seen = [];
            for (string? current = id; current is not null && seen.Add(current); current = AuthPowerLevels(current))
            {
                int index = mainline.IndexOf(current);
                if (index >= 0) return index + 1;
            }
            // Return none
            return 0;
        }

        /// <summary>Return the power levels event among an event's auth events, or null.</summary>
        private string? AuthPowerLevels(string id) => AuthIds(id).FirstOrDefault(a => events[a]["type"]?.GetValue<string>() == AuthEventTypes.PowerLevels && events[a]["state_key"]?.GetValue<string>() == "");

        /// <summary>Return the ID of the create event of an event's room, for version 12 where auth events leave it out.</summary>
        private string? CreateOf(string id)
        {
            // The room ID names it; looked up once per room
            string roomId = events[id]["room_id"]?.GetValue<string>() ?? "";
            if (creates.TryGetValue(roomId, out string? known)) return known;
            string? create = events.Keys.FirstOrDefault(e => events[e]["type"]?.GetValue<string>() == AuthEventTypes.Create && RoomVersion.RoomIdOf(e) == roomId);
            creates[roomId] = create;
            // Return the known create event of that room
            return create;
        }
    }
}
