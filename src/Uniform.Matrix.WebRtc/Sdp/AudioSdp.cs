namespace Uniform.Matrix.WebRtc.Sdp;

using System.Globalization;
using Uniform.Matrix.WebRtc.Ice;

/// <summary>What this side puts in its offer or answer.</summary>
/// <param name="Ufrag">ICE username fragment.</param>
/// <param name="Password">ICE password.</param>
/// <param name="Fingerprint">DTLS certificate fingerprint, e.g. <c>sha-256 AB:CD:…</c>.</param>
/// <param name="Ssrc">SSRC of the audio sent.</param>
/// <param name="Cname">RTCP canonical name.</param>
public sealed record LocalAudio(string Ufrag, string Password, string Fingerprint, uint Ssrc, string Cname);

/// <summary>What the peer's offer or answer says of its audio.</summary>
/// <param name="Ufrag">ICE username fragment.</param>
/// <param name="Password">ICE password.</param>
/// <param name="Fingerprint">DTLS certificate fingerprint.</param>
/// <param name="Setup">DTLS role: <c>actpass</c> in offers, <c>active</c> or <c>passive</c> in answers.</param>
/// <param name="Mid">Media ID of the audio section.</param>
/// <param name="OpusPayloadType">RTP payload type of Opus.</param>
/// <param name="Candidates">ICE candidates in the description, if any.</param>
public sealed record RemoteAudio(string Ufrag, string Password, string Fingerprint, string Setup, string Mid, byte OpusPayloadType, IReadOnlyList<IceCandidate> Candidates);

/// <summary>Offers and answers of one Opus audio stream over WebRTC: bundled, with RTCP multiplexed, ICE credentials, DTLS-SRTP, and trickled candidates; other media in an offer, such as video, are rejected in the answer.</summary>
public static class AudioSdp
{
    /// <summary>The payload type offered for Opus, as browsers use.</summary>
    public const byte OpusPayloadType = 111;

    /// <summary>Return an offer of audio, as the caller sends it in <c>m.call.invite</c>.</summary>
    public static SessionDescription CreateOffer(LocalAudio local)
    {
        // One bundled audio section, DTLS role left to the answerer
        SessionDescription sdp = Session();
        sdp.Attributes.Add("group:BUNDLE 0");
        sdp.Media.Add(Audio(local, "0", OpusPayloadType, "actpass", "sendrecv"));
        // Return offer
        return sdp;
    }

    /// <summary>Return the answer to <paramref name="offer"/>: its audio section accepted, with this side the DTLS client unless <paramref name="setup"/> says otherwise, and the other sections rejected.</summary>
    /// <param name="offer">The peer's offer.</param>
    /// <param name="local">This side's audio.</param>
    /// <param name="setup">This side's DTLS role: <c>active</c> for a new connection, the current role when answering a renegotiation.</param>
    /// <exception cref="InvalidOperationException">When the offer has no audio with Opus over DTLS-SRTP.</exception>
    public static SessionDescription CreateAnswer(SessionDescription offer, LocalAudio local, string setup = "active")
    {
        // The audio section to accept
        RemoteAudio remote = Read(offer) ?? throw new InvalidOperationException("The offer has no Opus audio over DTLS-SRTP.");
        SessionDescription sdp = Session();
        sdp.Attributes.Add($"group:BUNDLE {remote.Mid}");

        // A section for each offered, in order; DTLS active, as browsers answer
        bool answered = false;
        foreach (MediaDescription media in offer.Media)
        {
            string mid = media.Get("mid") ?? "";
            if (!answered && mid == remote.Mid)
            {
                answered = true;
                string direction = media.Get("sendonly") is not null ? "recvonly" : media.Get("recvonly") is not null ? "sendonly" : media.Get("inactive") is not null ? "inactive" : "sendrecv";
                sdp.Media.Add(Audio(local, mid, remote.OpusPayloadType, setup, direction));
                continue;
            }
            sdp.Media.Add(new MediaDescription { Kind = media.Kind, Port = 0, Protocol = media.Protocol, Formats = [.. media.Formats.Take(1)], Lines = ["c=IN IP4 0.0.0.0"], Attributes = [$"mid:{mid}", "inactive"] });
        }
        // Return answer
        return sdp;
    }

    /// <summary>Return an offer that restarts ICE: <paramref name="previous"/>, this side's last offer or answer, with the new ICE credentials of <paramref name="local"/>, <c>setup:actpass</c>, no candidates, and the session version increased; the sections and their order stay, rejected ones too, as renegotiation requires.</summary>
    public static SessionDescription CreateRestartOffer(SessionDescription previous, LocalAudio local)
    {
        // Session lines, the origin's version one more
        SessionDescription sdp = new();
        foreach (string line in previous.Lines) sdp.Lines.Add(line.StartsWith("o=", StringComparison.Ordinal) ? NextVersion(line) : line);
        sdp.Attributes.AddRange(previous.Attributes);

        // Sections with new credentials and an open DTLS role
        foreach (MediaDescription media in previous.Media)
        {
            List<string> attributes = [.. media.Attributes.Where(a => !a.StartsWith("candidate:", StringComparison.Ordinal) && a != "end-of-candidates").Select(a =>
                a.StartsWith("ice-ufrag:", StringComparison.Ordinal) ? $"ice-ufrag:{local.Ufrag}"
                : a.StartsWith("ice-pwd:", StringComparison.Ordinal) ? $"ice-pwd:{local.Password}"
                : a.StartsWith("setup:", StringComparison.Ordinal) ? "setup:actpass"
                : a)];
            sdp.Media.Add(media with { Formats = [.. media.Formats], Lines = [.. media.Lines], Attributes = attributes });
        }
        // Return offer
        return sdp;
    }

    /// <summary>Return an origin line with its session version one more.</summary>
    private static string NextVersion(string origin)
    {
        // o=username session-id version …
        string[] fields = origin.Split(' ');
        if (fields.Length > 2 && long.TryParse(fields[2], CultureInfo.InvariantCulture, out long version)) fields[2] = (version + 1).ToString(CultureInfo.InvariantCulture);
        // Return line
        return string.Join(' ', fields);
    }

    /// <summary>Return the peer's audio from its offer or answer: the first audio section with Opus and DTLS-SRTP; null when there is none.</summary>
    public static RemoteAudio? Read(SessionDescription sdp)
    {
        // Audio over DTLS-SRTP, not rejected
        foreach (MediaDescription media in sdp.Media.Where(m => m.Kind == "audio" && m.Port != 0 && m.Protocol.Contains("TLS", StringComparison.Ordinal)))
        {
            // Opus by its rtpmap, and the credentials
            byte? opus = media.GetAll("rtpmap").Select(OpusType).FirstOrDefault(t => t is not null);
            string? ufrag = sdp.Get(media, "ice-ufrag"), password = sdp.Get(media, "ice-pwd"), fingerprint = sdp.Get(media, "fingerprint");
            if (opus is null || ufrag is null || password is null || fingerprint is null) continue;
            IceCandidate[] candidates = [.. media.GetAll("candidate").Select(c => IceCandidate.Parse("candidate:" + c)).OfType<IceCandidate>()];
            // Return audio
            return new RemoteAudio(ufrag, password, fingerprint, sdp.Get(media, "setup") ?? "actpass", media.Get("mid") ?? "0", opus.Value, candidates);
        }
        // On no usable audio
        return null;
    }

    /// <summary>Return the session lines: version, origin with a random session ID, name and time.</summary>
    private static SessionDescription Session()
    {
        // As browsers write them
        SessionDescription sdp = new();
        sdp.Lines.AddRange(["v=0", $"o=- {Random.Shared.NextInt64(1, long.MaxValue)} 2 IN IP4 127.0.0.1", "s=-", "t=0 0"]);
        sdp.Attributes.Add("msid-semantic: WMS *");
        // Return session
        return sdp;
    }

    /// <summary>Return the audio section of this side.</summary>
    private static MediaDescription Audio(LocalAudio local, string mid, byte payloadType, string setup, string direction)
    {
        // Transport, then the stream and codec; candidates trickle
        string pt = payloadType.ToString(CultureInfo.InvariantCulture);
        return new MediaDescription
        {
            Kind = "audio",
            Formats = [pt],
            Lines = ["c=IN IP4 0.0.0.0"],
            Attributes =
            [
                "rtcp:9 IN IP4 0.0.0.0",
                $"ice-ufrag:{local.Ufrag}",
                $"ice-pwd:{local.Password}",
                "ice-options:trickle",
                $"fingerprint:{local.Fingerprint}",
                $"setup:{setup}",
                $"mid:{mid}",
                direction,
                $"msid:{local.Cname} audio",
                "rtcp-mux",
                $"rtpmap:{pt} opus/48000/2",
                $"fmtp:{pt} minptime=10;useinbandfec=1",
                $"ssrc:{local.Ssrc} cname:{local.Cname}",
            ],
        };
    }

    /// <summary>Return the payload type of an rtpmap value when it maps Opus, e.g. <c>111 opus/48000/2</c>.</summary>
    private static byte? OpusType(string rtpmap)
    {
        // Payload type, then the encoding
        string[] parts = rtpmap.Split(' ', 2);
        if (parts.Length < 2 || !parts[1].StartsWith("opus/48000", StringComparison.OrdinalIgnoreCase)) return null;
        // Return payload type
        return byte.TryParse(parts[0], CultureInfo.InvariantCulture, out byte type) ? type : null;
    }
}
