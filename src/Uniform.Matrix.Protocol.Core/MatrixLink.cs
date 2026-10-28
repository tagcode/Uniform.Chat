namespace Uniform.Matrix.Protocol.Core;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

/// <summary>Link to a room, a user or an event: a <see href="https://spec.matrix.org/latest/appendices/#matrixto-navigation">matrix.to</see> link, a <see href="https://spec.matrix.org/latest/appendices/#matrix-uri-scheme"><c>matrix:</c> URI</see> or a bare identifier.</summary>
public sealed partial record MatrixLink
{
    private const string MatrixTo = "https://matrix.to/#/";
    private const string Scheme = "matrix:";

    /// <summary>Room ID (<c>!</c>), room alias (<c>#</c>) or user ID (<c>@</c>).</summary>
    public string Target { get; init; } = "";
    /// <summary>Event in the room, or null.</summary>
    public string? EventId { get; init; }
    /// <summary>Servers to join the room through, from <c>via</c> parameters.</summary>
    public IReadOnlyList<string> Via { get; init; } = [];

    /// <summary>Whether the link is to a user.</summary>
    public bool IsUser => Target.StartsWith('@');

    /// <summary>Parse <paramref name="value"/>; false when it is not a link or identifier of a room, user or event.</summary>
    /// <param name="value">E.g. <c>https://matrix.to/#/#room:example.org</c>, <c>matrix:roomid/abc:example.org?via=example.org</c> or <c>!abc:example.org</c>.</param>
    /// <param name="link">The link.</param>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out MatrixLink? link)
    {
        // Split off the query
        link = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        int question = text.IndexOf('?');
        string path = question < 0 ? text : text[..question];
        IReadOnlyList<string> via = question < 0 ? [] : ParseVia(text[(question + 1)..]);

        // Each form gives the target and event
        (string Target, string? EventId)? parts;
        if (path.StartsWith(MatrixTo, StringComparison.OrdinalIgnoreCase)) parts = ParseMatrixTo(path[MatrixTo.Length..]);
        else if (path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) parts = ParseScheme(path[Scheme.Length..]);
        else parts = question < 0 && IsIdentifier(text) ? (text, null) : null;
        if (parts is not { } found) return false;

        // Return link
        link = new MatrixLink { Target = found.Target, EventId = found.EventId, Via = via };
        return true;
    }

    /// <summary>Return the matrix.to links and <c>matrix:</c> URIs in <paramref name="text"/>, e.g. a message body or its HTML, once each, in order.</summary>
    public static IReadOnlyList<MatrixLink> FindAll(string? text)
    {
        // Candidates, without the punctuation that ends a sentence
        if (string.IsNullOrEmpty(text)) return [];
        List<MatrixLink> links = [];
        foreach (Match match in LinkPattern().Matches(text))
        {
            string candidate = System.Net.WebUtility.HtmlDecode(match.Value.TrimEnd('.', ',', ';', ':', '!', '?'));
            if (TryParse(candidate, out MatrixLink? link) && !links.Contains(link)) links.Add(link);
        }
        // Return links
        return links;
    }

    /// <summary>Format as a matrix.to link.</summary>
    public override string ToString()
    {
        // Path, then via parameters
        string path = MatrixTo + Uri.EscapeDataString(Target) + (EventId is null ? "" : "/" + Uri.EscapeDataString(EventId));
        // Return link
        return Via.Count == 0 ? path : path + "?" + string.Join('&', Via.Select(v => "via=" + Uri.EscapeDataString(v)));
    }

    /// <summary>Compare by target, event and servers.</summary>
    public bool Equals(MatrixLink? other) => other is not null && Target == other.Target && EventId == other.EventId && Via.SequenceEqual(other.Via);

    /// <summary>Hash of the target and event.</summary>
    public override int GetHashCode() => HashCode.Combine(Target, EventId);

    /// <summary>Return the target and event of a matrix.to path, e.g. <c>!room:example.org/$event</c>, or null.</summary>
    private static (string, string?)? ParseMatrixTo(string path)
    {
        // Target and an optional event, each escaped
        string[] segments = path.Split('/');
        if (segments.Length > 2) return null;
        string target = Uri.UnescapeDataString(segments[0]);
        string? eventId = segments.Length == 2 ? Uri.UnescapeDataString(segments[1]) : null;
        if (!IsIdentifier(target) || eventId is not null && (!eventId.StartsWith('$') || target.StartsWith('@'))) return null;
        // Return parts
        return (target, eventId);
    }

    /// <summary>Return the target and event of a <c>matrix:</c> URI path, e.g. <c>roomid/abc:example.org/e/event</c>, or null.</summary>
    private static (string, string?)? ParseScheme(string path)
    {
        // Kind and identifier without its sigil
        string[] segments = path.Split('/');
        if (segments.Length is not (2 or 4)) return null;
        char? sigil = segments[0] switch { "roomid" => '!', "r" or "room" => '#', "u" or "user" => '@', _ => null };
        if (sigil is null || segments[1].Length == 0) return null;
        string target = sigil + Uri.UnescapeDataString(segments[1]);

        // An event in a room
        string? eventId = null;
        if (segments.Length == 4)
        {
            if (sigil == '@' || segments[2] is not ("e" or "event") || segments[3].Length == 0) return null;
            eventId = "$" + Uri.UnescapeDataString(segments[3]);
        }
        // Return parts
        return IsIdentifier(target) ? (target, eventId) : null;
    }

    /// <summary>Return the <c>via</c> servers of a query string.</summary>
    private static IReadOnlyList<string> ParseVia(string query) =>
        [.. query.Split('&').Where(p => p.StartsWith("via=", StringComparison.Ordinal)).Select(p => Uri.UnescapeDataString(p[4..])).Where(s => s.Length > 0).Distinct()];

    /// <summary>Return whether <paramref name="text"/> looks like a room ID, alias or user ID; room IDs of newer versions have no server part.</summary>
    private static bool IsIdentifier(string text)
    {
        // Sigil, something after it, no spaces
        if (text.Length < 2 || text.Any(char.IsWhiteSpace)) return false;
        // Return by kind
        return text[0] switch { '!' => true, '#' or '@' => text.IndexOf(':') > 1 && !text.EndsWith(':'), _ => false };
    }

    /// <summary>A matrix.to link or <c>matrix:</c> URI in text, up to a space, quote or bracket.</summary>
    [GeneratedRegex(@"(?:https://matrix\.to/#/|matrix:(?:roomid|r|u|room|user)/)[^\s""'<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
