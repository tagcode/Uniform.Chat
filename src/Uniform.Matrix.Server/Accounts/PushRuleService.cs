namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Server;
using Uniform.Matrix.Server.Rooms;
using Uniform.Matrix.Server.Storage;

/// <summary>A user's <see href="https://spec.matrix.org/latest/client-server-api/#push-rules">push rules</see>, kept as the <c>m.push_rules</c> account data that clients sync, and the unread counts they give.</summary>
/// <remarks>The stored rules are merged with the current <see cref="PushRules.Defaults"/> on every read, so changes to the defaults reach users who customised their rules: a stored default rule keeps only its <c>enabled</c> and <c>actions</c>. User rules come before the defaults of their kind, except that <c>.m.rule.master</c> stays first.</remarks>
public sealed class PushRuleService
{
    /// <summary>Global account data type of the push rules.</summary>
    public const string AccountDataType = "m.push_rules";
    /// <summary>Most unread events looked at per room, so a long unread history costs a bounded amount per sync.</summary>
    public const int MaxCounted = 200;
    private readonly AccountDataService accountData;

    /// <summary>Create the service.</summary>
    public PushRuleService(AccountDataService accountData)
    {
        // Store dependencies
        this.accountData = accountData;
    }

    /// <summary>Return the user's rule set by kind: the defaults merged with what the user changed.</summary>
    public async Task<JsonObject> GetRulesetAsync(string userId, CancellationToken cancellationToken)
    {
        // Stored rules under global, if any
        JsonObject? stored = (await accountData.TryGetAsync(userId, null, AccountDataType, cancellationToken))?["global"] as JsonObject;
        // Return merged rules
        return Merge(PushRules.Defaults(userId), stored);
    }

    /// <summary>Return the <c>m.push_rules</c> content to sync: <c>{"global": rules}</c>.</summary>
    public async Task<JsonObject> GetAccountDataContentAsync(string userId, CancellationToken cancellationToken) => new() { ["global"] = await GetRulesetAsync(userId, cancellationToken) };

    /// <summary>Return one rule; throw <c>M_NOT_FOUND</c> when there is none.</summary>
    public async Task<JsonObject> GetRuleAsync(string userId, string kind, string ruleId, CancellationToken cancellationToken)
    {
        // Find by ID within its kind
        JsonObject ruleset = await GetRulesetAsync(userId, cancellationToken);
        // Return rule
        return (JsonObject)FindRule(ruleset, kind, ruleId).DeepClone();
    }

    /// <summary>Add or replace a user rule, placed <paramref name="before"/> or <paramref name="after"/> another user rule of its kind when given.</summary>
    /// <param name="userId">Owner of the rules.</param>
    /// <param name="kind">Rule kind, e.g. <c>override</c>.</param>
    /// <param name="ruleId">Rule ID; for <c>room</c> rules a room ID, for <c>sender</c> rules a user ID.</param>
    /// <param name="body"><c>actions</c>, with <c>conditions</c> for override and underride rules or <c>pattern</c> for content rules.</param>
    /// <param name="before">Rule ID to place the rule before, or null.</param>
    /// <param name="after">Rule ID to place the rule after, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PutRuleAsync(string userId, string kind, string ruleId, JsonObject body, string? before, string? after, CancellationToken cancellationToken)
    {
        // Assert a user rule of a known kind with its parts
        AssertKind(kind);
        if (ruleId.StartsWith('.')) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Rule IDs starting with a dot belong to the server's default rules.");
        if (kind == "room" && !ruleId.StartsWith('!')) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Room rules are named by room ID.");
        if (kind == "sender" && !UserId.TryParse(ruleId, out _)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Sender rules are named by user ID.");
        if (body["actions"] is not JsonArray actions) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "The rule needs actions.");
        JsonObject rule = new() { ["rule_id"] = ruleId, ["default"] = false, ["enabled"] = true, ["actions"] = actions.DeepClone() };
        if (kind is "override" or "underride") rule["conditions"] = body["conditions"] is JsonArray conditions ? conditions.DeepClone() : new JsonArray();
        if (kind == "content")
        {
            if (body["pattern"]?.GetValue<string>() is not { Length: > 0 } pattern) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.MissingParam, "Content rules need a pattern.");
            rule["pattern"] = pattern;
        }

        // Replace in place, or insert where asked, or first of the user's rules
        JsonObject ruleset = await GetRulesetAsync(userId, cancellationToken);
        JsonArray rules = ruleset[kind]!.AsArray();
        int existing = IndexOf(rules, ruleId);
        if (existing >= 0 && before is null && after is null)
        {
            if (rules[existing]!["enabled"] is JsonNode enabled) rule["enabled"] = enabled.DeepClone();
            rules[existing] = rule;
        }
        else
        {
            if (existing >= 0) rules.RemoveAt(existing);
            rules.Insert(InsertionIndex(rules, kind, before, after), rule);
        }
        // Done
        await SaveAsync(userId, ruleset, cancellationToken);
    }

    /// <summary>Delete a user rule; the default rules can only be disabled.</summary>
    public async Task DeleteRuleAsync(string userId, string kind, string ruleId, CancellationToken cancellationToken)
    {
        // Assert a user rule
        JsonObject ruleset = await GetRulesetAsync(userId, cancellationToken);
        JsonObject rule = FindRule(ruleset, kind, ruleId);
        if (rule["default"]?.GetValue<bool>() == true) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, "Default rules cannot be deleted; disable them instead.");
        ruleset[kind]!.AsArray().Remove(rule);
        // Done
        await SaveAsync(userId, ruleset, cancellationToken);
    }

    /// <summary>Change one field, <c>enabled</c> or <c>actions</c>, of any rule.</summary>
    public async Task SetRuleFieldAsync(string userId, string kind, string ruleId, string field, JsonNode value, CancellationToken cancellationToken)
    {
        // Assert the field's type
        if (field == "enabled" && (value is not JsonValue flag || !flag.TryGetValue(out bool _))) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.BadJson, "enabled must be true or false.");
        if (field == "actions" && value is not JsonArray) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.BadJson, "actions must be an array.");

        // Change and save
        JsonObject ruleset = await GetRulesetAsync(userId, cancellationToken);
        FindRule(ruleset, kind, ruleId)[field] = value.DeepClone();
        // Done
        await SaveAsync(userId, ruleset, cancellationToken);
    }

    /// <summary>Return the user's unread notifications and highlights in a room: events by others after their read receipt, their own latest message or their join, as the rules decide.</summary>
    /// <param name="dbContext">Open database.</param>
    /// <param name="ruleset">The user's rules, from <see cref="GetRulesetAsync"/>.</param>
    /// <param name="userId">The user.</param>
    /// <param name="roomId">A room the user is joined to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<(int Notifications, int Highlights)> CountUnreadAsync(ServerDbContext dbContext, JsonObject ruleset, string userId, string roomId, CancellationToken cancellationToken)
    {
        // Read up to: the receipt, the user's own latest event, or the join
        List<string> receiptIds = await dbContext.Receipts.AsNoTracking().Where(r => r.RoomId == roomId && r.UserId == userId && (r.Type == ReceiptService.Read || r.Type == ReceiptService.ReadPrivate)).Select(r => r.EventId).ToListAsync(cancellationToken);
        long receipt = await dbContext.Events.AsNoTracking().Where(e => receiptIds.Contains(e.EventId)).MaxAsync(e => (long?)e.Stream, cancellationToken) ?? 0;
        long own = await dbContext.Events.AsNoTracking().Where(e => e.RoomId == roomId && e.Sender == userId).MaxAsync(e => (long?)e.Stream, cancellationToken) ?? 0;
        long since = Math.Max(receipt, own);

        // Newest unread events by others, not deleted
        List<string> unread = await dbContext.Events.AsNoTracking()
            .Where(e => e.RoomId == roomId && e.Stream > since && e.Sender != userId && e.RedactedBy == null)
            .OrderByDescending(e => e.Stream).Take(MaxCounted).Select(e => e.Json).ToListAsync(cancellationToken);
        if (unread.Count == 0) return (0, 0);

        // Facts of the room for the conditions, then the rules per event
        PushContext context = await ContextAsync(dbContext, userId, roomId, cancellationToken);
        JsonObject? levelsContent = await StateContentAsync(dbContext, roomId, EventTypes.RoomPowerLevels, "", cancellationToken);
        string? creator = await dbContext.Rooms.AsNoTracking().Where(r => r.RoomId == roomId).Select(r => r.Creator).SingleOrDefaultAsync(cancellationToken);
        PowerLevels levels = new(levelsContent, creator);
        int notifications = 0, highlights = 0;
        foreach (string json in unread)
        {
            if (JsonNode.Parse(json) is not JsonObject clientEvent) continue;
            string sender = clientEvent["sender"]?.GetValue<string>() ?? "";
            PushDecision decision = PushRules.Evaluate(ruleset, clientEvent, context with { SenderMayNotify = key => levels.UserLevel(sender) >= NotificationLevel(levelsContent, key) });
            if (decision.Notify) notifications++;
            if (decision.Highlight) highlights++;
        }
        // Return counts
        return (notifications, highlights);
    }

    /// <summary>Return what the user's rules decide for one event of a room, with the room's current members and power levels.</summary>
    /// <param name="dbContext">Open database.</param>
    /// <param name="ruleset">The user's rules, from <see cref="GetRulesetAsync"/>.</param>
    /// <param name="userId">The user.</param>
    /// <param name="roomId">The event's room.</param>
    /// <param name="clientEvent">The event, with <c>type</c>, <c>sender</c>, <c>room_id</c> and <c>content</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PushDecision> DecideAsync(ServerDbContext dbContext, JsonObject ruleset, string userId, string roomId, JsonObject clientEvent, CancellationToken cancellationToken)
    {
        // Facts of the room, then the rules
        PushContext context = await ContextAsync(dbContext, userId, roomId, cancellationToken);
        JsonObject? levelsContent = await StateContentAsync(dbContext, roomId, EventTypes.RoomPowerLevels, "", cancellationToken);
        string? creator = await dbContext.Rooms.AsNoTracking().Where(r => r.RoomId == roomId).Select(r => r.Creator).SingleOrDefaultAsync(cancellationToken);
        PowerLevels levels = new(levelsContent, creator);
        string sender = clientEvent["sender"]?.GetValue<string>() ?? "";
        // Return decision
        return PushRules.Evaluate(ruleset, clientEvent, context with { SenderMayNotify = key => levels.UserLevel(sender) >= NotificationLevel(levelsContent, key) });
    }

    /// <summary>Return the user's unread notifications across all their joined rooms, e.g. for a push gateway's badge.</summary>
    public static async Task<int> CountAllUnreadAsync(ServerDbContext dbContext, JsonObject ruleset, string userId, CancellationToken cancellationToken)
    {
        // Room by room
        List<string> rooms = await dbContext.CurrentState.AsNoTracking().Where(s => s.Type == EventTypes.RoomMember && s.StateKey == userId && s.Membership == Memberships.Join).Select(s => s.RoomId).ToListAsync(cancellationToken);
        int total = 0;
        foreach (string roomId in rooms) total += (await CountUnreadAsync(dbContext, ruleset, userId, roomId, cancellationToken)).Notifications;
        // Return total
        return total;
    }

    /// <summary>Return the room facts the conditions need: the user's display name and the joined member count.</summary>
    private static async Task<PushContext> ContextAsync(ServerDbContext dbContext, string userId, string roomId, CancellationToken cancellationToken)
    {
        // Member count and own member event from the current state
        long members = await dbContext.CurrentState.CountAsync(s => s.RoomId == roomId && s.Type == EventTypes.RoomMember && s.Membership == Memberships.Join, cancellationToken);
        JsonObject? member = await StateContentAsync(dbContext, roomId, EventTypes.RoomMember, userId, cancellationToken);
        // Return context
        return new PushContext { UserId = userId, DisplayName = member?["displayname"]?.GetValue<string>(), MemberCount = members };
    }

    /// <summary>Return the level needed to notify the room for a key such as <c>room</c>: from <c>notifications</c> in the power levels, 50 by default.</summary>
    private static long NotificationLevel(JsonObject? levelsContent, string key) => levelsContent?["notifications"]?[key] is JsonNode needed && PowerLevels.TryGetInteger(needed, out long value) ? value : 50;

    /// <summary>Return the content of a current state event, or null when unset.</summary>
    private static async Task<JsonObject?> StateContentAsync(ServerDbContext dbContext, string roomId, string type, string stateKey, CancellationToken cancellationToken)
    {
        // Current state row, then its event
        string? eventId = await dbContext.CurrentState.AsNoTracking().Where(s => s.RoomId == roomId && s.Type == type && s.StateKey == stateKey).Select(s => s.EventId).SingleOrDefaultAsync(cancellationToken);
        string? json = eventId is null ? null : await dbContext.Events.AsNoTracking().Where(e => e.EventId == eventId).Select(e => e.Json).SingleOrDefaultAsync(cancellationToken);
        // Return content
        return json is null ? null : JsonNode.Parse(json)?["content"] as JsonObject;
    }

    /// <summary>Return the defaults with the stored changes: enabled and actions of default rules, and the user's own rules first in each kind.</summary>
    private static JsonObject Merge(JsonObject defaults, JsonObject? stored)
    {
        // Kind by kind
        JsonObject merged = [];
        foreach (string kind in PushRules.Kinds)
        {
            JsonArray defaultRules = defaults[kind] as JsonArray ?? [];
            JsonArray storedRules = stored?[kind] as JsonArray ?? [];
            List<JsonNode> userRules = [.. storedRules.OfType<JsonObject>().Where(r => r["default"]?.GetValue<bool>() != true).Select(r => r.DeepClone())];
            List<JsonNode> defaultCopies = [];
            foreach (JsonObject rule in defaultRules.OfType<JsonObject>())
            {
                JsonObject copy = (JsonObject)rule.DeepClone();
                if (storedRules.OfType<JsonObject>().FirstOrDefault(r => r["default"]?.GetValue<bool>() == true && r["rule_id"]?.GetValue<string>() == rule["rule_id"]?.GetValue<string>()) is JsonObject changed)
                {
                    if (changed["enabled"] is JsonNode enabled) copy["enabled"] = enabled.DeepClone();
                    if (changed["actions"] is JsonNode actions) copy["actions"] = actions.DeepClone();
                }
                defaultCopies.Add(copy);
            }
            // The master rule stays first; otherwise user rules go before the defaults
            int keepFirst = kind == "override" && defaultCopies.Count > 0 ? 1 : 0;
            merged[kind] = new JsonArray([.. defaultCopies.Take(keepFirst), .. userRules, .. defaultCopies.Skip(keepFirst)]);
        }
        // Return merged rules
        return merged;
    }

    /// <summary>Store the whole rule set; syncs deliver it as account data.</summary>
    private Task SaveAsync(string userId, JsonObject ruleset, CancellationToken cancellationToken) => accountData.SetAsync(userId, null, AccountDataType, new JsonObject { ["global"] = ruleset.DeepClone() }, cancellationToken);

    /// <summary>Return a rule of the rule set; throw <c>M_NOT_FOUND</c> for unknown kinds and rules.</summary>
    private static JsonObject FindRule(JsonObject ruleset, string kind, string ruleId)
    {
        // Within its kind
        AssertKind(kind);
        JsonArray rules = ruleset[kind]!.AsArray();
        int index = IndexOf(rules, ruleId);
        // Return rule
        return index >= 0 ? rules[index]!.AsObject() : throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"No push rule {kind}/{ruleId}.");
    }

    /// <summary>Return the index of a rule ID, or -1.</summary>
    private static int IndexOf(JsonArray rules, string ruleId)
    {
        // Linear search; rule sets are short
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i]?["rule_id"]?.GetValue<string>() == ruleId) return i;
        }
        // Return not found
        return -1;
    }

    /// <summary>Return where a new user rule goes: before or after the named rule, or at the start of the user's rules.</summary>
    private static int InsertionIndex(JsonArray rules, string kind, string? before, string? after)
    {
        // A named neighbour must be a user rule of this kind
        string? neighbour = before ?? after;
        if (neighbour is not null)
        {
            int index = IndexOf(rules, neighbour);
            if (index < 0 || rules[index]!["default"]?.GetValue<bool>() == true) throw new MatrixException(HttpStatusCode.NotFound, ErrorCodes.NotFound, $"No user push rule {kind}/{neighbour} to place it next to.");
            return before is not null ? index : index + 1;
        }
        // Return the first place after the master rule
        return kind == "override" && rules.Count > 0 && rules[0]?["rule_id"]?.GetValue<string>() == ".m.rule.master" ? 1 : 0;
    }

    /// <summary>Throw <c>M_INVALID_PARAM</c> unless <paramref name="kind"/> is a rule kind.</summary>
    private static void AssertKind(string kind)
    {
        // Known kinds only
        if (!PushRules.Kinds.Contains(kind)) throw new MatrixException(HttpStatusCode.BadRequest, ErrorCodes.InvalidParam, $"{kind} is not a push rule kind.");
    }
}
