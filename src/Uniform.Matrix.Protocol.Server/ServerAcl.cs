namespace Uniform.Matrix.Protocol.Server;

using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>A room's <see href="https://spec.matrix.org/latest/client-server-api/#server-access-control-lists-acls-for-rooms">server access control list</see>, <c>m.room.server_acl</c>: which servers may take part in the room.</summary>
/// <remarks>A server is denied when it is an IP address and <c>allow_ip_literals</c> is false, or when it matches a <c>deny</c> pattern; otherwise it must match an <c>allow</c> pattern. Patterns match the server name without its port, case-insensitively, with <c>*</c> for any characters and <c>?</c> for one.</remarks>
public sealed class ServerAcl
{
    private readonly List<Regex> allow;
    private readonly List<Regex> deny;
    private readonly bool allowIpLiterals;

    /// <summary>Create the list from the content of an <c>m.room.server_acl</c> event.</summary>
    public ServerAcl(JsonObject content)
    {
        // Patterns, ignoring entries that are not strings; IP literals allowed unless set false
        allow = Patterns(content["allow"]);
        deny = Patterns(content["deny"]);
        allowIpLiterals = content["allow_ip_literals"] is not JsonValue value || !value.TryGetValue(out bool allowed) || allowed;
    }

    /// <summary>Return whether <paramref name="serverName"/> may take part in the room.</summary>
    /// <param name="serverName">Server name, with or without a port.</param>
    public bool IsAllowed(string serverName)
    {
        // Host without port
        string host = ServerNames.TryParse(serverName, out string? parsed, out _) ? parsed : serverName;
        if (!allowIpLiterals && (host.StartsWith('[') || IPAddress.TryParse(host, out _))) return false;
        if (deny.Any(p => p.IsMatch(host))) return false;
        // Return whether allowed
        return allow.Any(p => p.IsMatch(host));
    }

    /// <summary>Return the glob patterns of a JSON array as regular expressions.</summary>
    private static List<Regex> Patterns(JsonNode? array) =>
        [.. (array as JsonArray ?? []).Select(p => p is JsonValue v && v.TryGetValue(out string? glob) ? glob : null).OfType<string>()
            .Select(glob => new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))];
}
