namespace Uniform.Matrix.Protocol.Core;

using System.Diagnostics.CodeAnalysis;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#matrix-content-mxc-uris">Content URI</see> of the form <c>mxc://server/mediaId</c>.</summary>
/// <param name="ServerName">Server that stores the media.</param>
/// <param name="MediaId">Opaque media ID.</param>
public readonly record struct MxcUri(string ServerName, string MediaId)
{
    private const string Scheme = "mxc://";

    /// <summary>Parse <paramref name="value"/>; false when it is not of the form <c>mxc://server/mediaId</c>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out MxcUri uri)
    {
        // Assert scheme and separator
        uri = default;
        if (value is null || !value.StartsWith(Scheme, StringComparison.Ordinal)) return false;
        int slash = value.IndexOf('/', Scheme.Length);
        if (slash <= Scheme.Length || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0) return false;

        // Split
        uri = new MxcUri(value[Scheme.Length..slash], value[(slash + 1)..]);
        // Done
        return true;
    }

    /// <summary>Parse <paramref name="value"/>; throw <see cref="FormatException"/> when invalid.</summary>
    public static MxcUri Parse(string value) => TryParse(value, out MxcUri uri) ? uri : throw new FormatException($"Invalid content URI '{value}', expected mxc://server/mediaId");

    /// <summary>Format as <c>mxc://server/mediaId</c>.</summary>
    public override string ToString() => $"{Scheme}{ServerName}/{MediaId}";
}
