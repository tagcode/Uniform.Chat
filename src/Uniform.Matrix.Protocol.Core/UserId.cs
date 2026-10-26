namespace Uniform.Matrix.Protocol.Core;

using System.Diagnostics.CodeAnalysis;

/// <summary><see href="https://spec.matrix.org/latest/appendices/#user-identifiers">User ID</see> of the form <c>@localpart:server</c>.</summary>
/// <param name="Localpart">Part between <c>@</c> and the first <c>:</c>.</param>
/// <param name="ServerName">Homeserver name, possibly with a port.</param>
/// <example><code><![CDATA[
/// UserId id = UserId.Parse("@alice:matrix.org");
/// Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, id.ServerName);
/// ]]></code></example>
public readonly record struct UserId(string Localpart, string ServerName)
{
    /// <summary>Parse <paramref name="value"/>; false when it is not of the form <c>@localpart:server</c>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out UserId userId)
    {
        // Assert sigil and separator; localpart cannot contain ':'
        userId = default;
        if (value is null || value.Length < 4 || value[0] != '@') return false;
        int colon = value.IndexOf(':');
        if (colon < 2 || colon == value.Length - 1) return false;

        // Split
        userId = new UserId(value[1..colon], value[(colon + 1)..]);
        // Done
        return true;
    }

    /// <summary>Parse <paramref name="value"/>; throw <see cref="FormatException"/> when invalid.</summary>
    public static UserId Parse(string value) => TryParse(value, out UserId userId) ? userId : throw new FormatException($"Invalid user ID '{value}', expected @localpart:server");

    /// <summary>Format as <c>@localpart:server</c>.</summary>
    public override string ToString() => $"@{Localpart}:{ServerName}";
}
