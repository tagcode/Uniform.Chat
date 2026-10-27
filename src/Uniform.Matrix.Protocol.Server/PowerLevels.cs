namespace Uniform.Matrix.Protocol.Server;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>A room's <see href="https://spec.matrix.org/latest/client-server-api/#mroompower_levels">power levels</see> with the spec's defaults filled in.</summary>
public sealed class PowerLevels
{
    private readonly JsonObject? content;
    private readonly string? creator;

    /// <summary>Wrap the content of <c>m.room.power_levels</c>, or null when the room has none, in which case only <paramref name="creator"/> has power.</summary>
    public PowerLevels(JsonObject? content, string? creator)
    {
        // Store sources
        this.content = content;
        this.creator = creator;
    }

    /// <summary>Whether the room has a power levels event.</summary>
    public bool Exists => content is not null;
    /// <summary>Level needed to ban; 50 by default.</summary>
    public long Ban => Level("ban", 50);
    /// <summary>Level needed to kick; 50 by default.</summary>
    public long Kick => Level("kick", 50);
    /// <summary>Level needed to redact others' events; 50 by default.</summary>
    public long Redact => Level("redact", 50);
    /// <summary>Level needed to invite; 0 by default.</summary>
    public long Invite => Level("invite", 0);

    /// <summary>Return a user's level: from <c>users</c>, else <c>users_default</c>; without power levels, 100 for the creator and 0 for others.</summary>
    public long UserLevel(string userId)
    {
        // No event: only the creator has power
        if (content is null) return userId == creator ? 100 : 0;
        // Return explicit or default level
        return content["users"]?[userId] is JsonNode level ? AsLong(level) : Level("users_default", 0);
    }

    /// <summary>Return the level needed to send <paramref name="type"/>: from <c>events</c>, else <c>state_default</c> (50) or <c>events_default</c> (0); everything is 0 without power levels.</summary>
    public long EventLevel(string type, bool isState)
    {
        // No event: anyone may send anything
        if (content is null) return 0;
        // Return explicit or default level
        return content["events"]?[type] is JsonNode level ? AsLong(level) : isState ? Level("state_default", 50) : Level("events_default", 0);
    }

    /// <summary>Return an integer field, or <paramref name="fallback"/> when missing; without an event, <paramref name="fallback"/> too.</summary>
    private long Level(string name, long fallback) => content?[name] is JsonNode node ? AsLong(node) : fallback;

    /// <summary>Read an integer level; room versions 10 and later allow only integers, which the auth rules check first.</summary>
    internal static long AsLong(JsonNode node) => TryGetInteger(node, out long value) ? value : 0;

    /// <summary>Read a JSON integer, whether parsed or built from a .NET number; fractions and other kinds are not integers.</summary>
    public static bool TryGetInteger(JsonNode node, out long value)
    {
        // Numbers only, through their JSON form
        value = 0;
        if (node.GetValueKind() != JsonValueKind.Number) return false;
        JsonElement element = node.AsValue().TryGetValue(out JsonElement parsed) ? parsed : JsonSerializer.SerializeToElement(node);
        // Return whether it is a whole 64-bit number
        return element.TryGetInt64(out value);
    }
}
