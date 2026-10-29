namespace Uniform.Matrix.WebRtc.Sdp;

using System.Text;

/// <summary>A media section of an SDP: its <c>m=</c> line and the attributes under it.</summary>
public sealed record MediaDescription
{
    /// <summary>The media kind, e.g. <c>audio</c> or <c>video</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>The port; 0 rejects the section.</summary>
    public int Port { get; init; } = 9;
    /// <summary>The protocol, e.g. <c>UDP/TLS/RTP/SAVPF</c>.</summary>
    public string Protocol { get; init; } = "UDP/TLS/RTP/SAVPF";
    /// <summary>The formats: RTP payload types.</summary>
    public List<string> Formats { get; init; } = [];
    /// <summary>Lines other than <c>a=</c>, such as <c>c=</c>, without their type letter's <c>c=</c>, in order.</summary>
    public List<string> Lines { get; init; } = [];
    /// <summary>The attributes, without <c>a=</c>, e.g. <c>mid:0</c> or <c>rtcp-mux</c>, in order.</summary>
    public List<string> Attributes { get; init; } = [];

    /// <summary>Return the value of the first attribute named <paramref name="name"/>, or null.</summary>
    public string? Get(string name) => GetAll(name).FirstOrDefault();

    /// <summary>Return the values of the attributes named <paramref name="name"/>; a flag such as <c>rtcp-mux</c> has the empty value.</summary>
    public IEnumerable<string> GetAll(string name) => SessionDescription.Values(Attributes, name);
}

/// <summary>An <see href="https://www.rfc-editor.org/rfc/rfc8866">SDP</see> session description, as offers and answers carry it: the session's lines and attributes, then the media sections.</summary>
public sealed class SessionDescription
{
    /// <summary>The session-level lines other than attributes, in order, e.g. <c>v=0</c>.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>The session-level attributes, without <c>a=</c>.</summary>
    public List<string> Attributes { get; } = [];

    /// <summary>The media sections.</summary>
    public List<MediaDescription> Media { get; } = [];

    /// <summary>Return the value of the first session attribute named <paramref name="name"/>, or null.</summary>
    public string? Get(string name) => Values(Attributes, name).FirstOrDefault();

    /// <summary>Return the value of an attribute of a media section, or of the session when the section has none, as ICE and DTLS attributes may be either.</summary>
    public string? Get(MediaDescription media, string name) => media.Get(name) ?? Get(name);

    /// <summary>Parse SDP text, with any line endings.</summary>
    public static SessionDescription Parse(string text)
    {
        // Session lines until the first m=, then sections
        SessionDescription sdp = new();
        MediaDescription? media = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '=') continue;
            string value = line[2..];
            if (line[0] == 'm')
            {
                string[] fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3) continue;
                media = new MediaDescription { Kind = fields[0], Port = int.TryParse(fields[1].Split('/')[0], out int port) ? port : 0, Protocol = fields[2], Formats = [.. fields[3..]] };
                sdp.Media.Add(media);
            }
            else if (line[0] == 'a') (media?.Attributes ?? sdp.Attributes).Add(value);
            else (media?.Lines ?? sdp.Lines).Add(line);
        }
        // Return description
        return sdp;
    }

    /// <summary>Return the SDP text, with CRLF line endings.</summary>
    public override string ToString()
    {
        // Session, then each section
        StringBuilder text = new();
        foreach (string line in Lines) text.Append(line).Append("\r\n");
        foreach (string attribute in Attributes) text.Append("a=").Append(attribute).Append("\r\n");
        foreach (MediaDescription media in Media)
        {
            text.Append($"m={media.Kind} {media.Port} {media.Protocol} {string.Join(' ', media.Formats)}\r\n");
            foreach (string line in media.Lines) text.Append(line).Append("\r\n");
            foreach (string attribute in media.Attributes) text.Append("a=").Append(attribute).Append("\r\n");
        }
        // Return text
        return text.ToString();
    }

    /// <summary>Return the values of the attributes named <paramref name="name"/>.</summary>
    internal static IEnumerable<string> Values(IEnumerable<string> attributes, string name)
    {
        // name:value, or name alone
        foreach (string attribute in attributes)
        {
            if (attribute == name) yield return "";
            else if (attribute.Length > name.Length && attribute.StartsWith(name, StringComparison.Ordinal) && attribute[name.Length] == ':') yield return attribute[(name.Length + 1)..];
        }
    }
}
