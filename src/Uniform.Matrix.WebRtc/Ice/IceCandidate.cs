namespace Uniform.Matrix.WebRtc.Ice;

using System.Globalization;
using System.Net;
using System.Text;

/// <summary>The type of an ICE candidate, which ranks it: direct beats reflexive beats relayed.</summary>
public enum IceCandidateType
{
    /// <summary>An address of a local interface.</summary>
    Host,
    /// <summary>The public address a NAT maps a host address to, as a STUN or TURN server saw it.</summary>
    ServerReflexive,
    /// <summary>An address learnt from a connectivity check, which no candidate named.</summary>
    PeerReflexive,
    /// <summary>A relayed address on a TURN server.</summary>
    Relayed,
}

/// <summary>An <see href="https://www.rfc-editor.org/rfc/rfc8445">ICE</see> candidate over UDP: where a peer may be reached, written as the <see href="https://www.rfc-editor.org/rfc/rfc8839#section-5.1">candidate attribute</see> of SDP.</summary>
public sealed record IceCandidate
{
    /// <summary>Groups candidates of the same type, base and server; the same for those that would fail alike.</summary>
    public required string Foundation { get; init; }
    /// <summary>Component; always 1, RTP, with RTCP multiplexed.</summary>
    public int Component { get; init; } = 1;
    /// <summary>Priority; higher is preferred.</summary>
    public required uint Priority { get; init; }
    /// <summary>Address and port.</summary>
    public required IPEndPoint Address { get; init; }
    /// <summary>Type.</summary>
    public required IceCandidateType Type { get; init; }
    /// <summary>The address it derives from, for reflexive and relayed candidates.</summary>
    public IPEndPoint? RelatedAddress { get; init; }

    /// <summary>Return the priority of <see href="https://www.rfc-editor.org/rfc/rfc8445#section-5.1.2">RFC 8445 5.1.2</see> for a type, a local preference, and component 1.</summary>
    public static uint ComputePriority(IceCandidateType type, ushort localPreference = 65535) => ((uint)TypePreference(type) << 24) | ((uint)localPreference << 8) | (256 - 1);

    /// <summary>Return the type preference RFC 8445 recommends.</summary>
    public static byte TypePreference(IceCandidateType type) => type switch
    {
        IceCandidateType.Host => 126,
        IceCandidateType.PeerReflexive => 110,
        IceCandidateType.ServerReflexive => 100,
        _ => 0,
    };

    /// <summary>Parse a candidate attribute, with or without its <c>a=</c>; null when malformed, not UDP, or its address is a name, e.g. the mDNS names browsers give host candidates.</summary>
    /// <example><code><![CDATA[IceCandidate? candidate = IceCandidate.Parse("candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host");]]></code></example>
    public static IceCandidate? Parse(string attribute)
    {
        // Foundation, component, transport, priority, address, port, "typ", type
        string text = attribute.StartsWith("a=", StringComparison.Ordinal) ? attribute[2..] : attribute;
        if (!text.StartsWith("candidate:", StringComparison.Ordinal)) return null;
        string[] fields = text["candidate:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 8 || fields[6] != "typ") return null;
        if (!int.TryParse(fields[1], CultureInfo.InvariantCulture, out int component) || !string.Equals(fields[2], "udp", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uint.TryParse(fields[3], CultureInfo.InvariantCulture, out uint priority)) return null;
        if (!IPAddress.TryParse(fields[4], out IPAddress? address) || !ushort.TryParse(fields[5], CultureInfo.InvariantCulture, out ushort port)) return null;
        IceCandidateType? type = fields[7] switch { "host" => IceCandidateType.Host, "srflx" => IceCandidateType.ServerReflexive, "prflx" => IceCandidateType.PeerReflexive, "relay" => IceCandidateType.Relayed, _ => null };
        if (type is null) return null;

        // Extensions by name and value: the related address, others ignored
        IPAddress? relatedAddress = null;
        int relatedPort = -1;
        for (int i = 8; i + 1 < fields.Length; i += 2)
        {
            if (fields[i] == "raddr" && IPAddress.TryParse(fields[i + 1], out IPAddress? raddr)) relatedAddress = raddr;
            if (fields[i] == "rport" && ushort.TryParse(fields[i + 1], CultureInfo.InvariantCulture, out ushort rport)) relatedPort = rport;
        }
        IPEndPoint? related = relatedAddress is not null && relatedPort >= 0 ? new IPEndPoint(relatedAddress, relatedPort) : null;
        // Return candidate
        return new IceCandidate { Foundation = fields[0], Component = component, Priority = priority, Address = new IPEndPoint(address, port), Type = type.Value, RelatedAddress = related };
    }

    /// <summary>Return the candidate attribute, without <c>a=</c>, as Matrix's <c>m.call.candidates</c> and browsers' trickle carry it.</summary>
    public override string ToString()
    {
        // Mandatory fields, then the related address
        string type = Type switch { IceCandidateType.Host => "host", IceCandidateType.ServerReflexive => "srflx", IceCandidateType.PeerReflexive => "prflx", _ => "relay" };
        StringBuilder text = new($"candidate:{Foundation} {Component} udp {Priority} {Address.Address} {Address.Port} typ {type}");
        if (RelatedAddress is not null) text.Append($" raddr {RelatedAddress.Address} rport {RelatedAddress.Port}");
        // Return text
        return text.ToString();
    }
}
