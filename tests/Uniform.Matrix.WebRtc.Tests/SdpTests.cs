namespace Uniform.Matrix.WebRtc.Tests;

using Uniform.Matrix.WebRtc.Sdp;

/// <summary>Tests of <see cref="SessionDescription"/> and <see cref="AudioSdp"/>.</summary>
public class SdpTests
{
    /// <summary>An offer as a browser makes it for a video call: audio and video, credentials at media level, a candidate.</summary>
    private const string BrowserOffer = """
        v=0
        o=- 4611731400430051336 2 IN IP4 127.0.0.1
        s=-
        t=0 0
        a=group:BUNDLE 0 1
        a=extmap-allow-mixed
        a=msid-semantic: WMS stream
        m=audio 9 UDP/TLS/RTP/SAVPF 111 63 9 0 8 13 110 126
        c=IN IP4 0.0.0.0
        a=rtcp:9 IN IP4 0.0.0.0
        a=candidate:1 1 udp 2122260223 192.0.2.10 50000 typ host generation 0
        a=ice-ufrag:Ab1c
        a=ice-pwd:abcdefghijklmnopqrstuvwx
        a=ice-options:trickle
        a=fingerprint:sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99
        a=setup:actpass
        a=mid:0
        a=sendrecv
        a=rtcp-mux
        a=rtpmap:111 opus/48000/2
        a=rtcp-fb:111 transport-cc
        a=fmtp:111 minptime=10;useinbandfec=1
        a=rtpmap:63 red/48000/2
        a=ssrc:1001 cname:abc
        m=video 9 UDP/TLS/RTP/SAVPF 96 97
        c=IN IP4 0.0.0.0
        a=ice-ufrag:Ab1c
        a=ice-pwd:abcdefghijklmnopqrstuvwx
        a=setup:actpass
        a=mid:1
        a=sendrecv
        a=rtpmap:96 VP8/90000
        """;

    /// <summary>Read the browser's audio: credentials, fingerprint, role, Opus and the candidate.</summary>
    [Fact]
    public void ReadBrowserOffer()
    {
        // Audio section
        RemoteAudio audio = AudioSdp.Read(SessionDescription.Parse(BrowserOffer))!;
        Assert.Equal("Ab1c", audio.Ufrag);
        Assert.Equal("abcdefghijklmnopqrstuvwx", audio.Password);
        Assert.StartsWith("sha-256 AA:BB", audio.Fingerprint);
        Assert.Equal("actpass", audio.Setup);
        Assert.Equal("0", audio.Mid);
        Assert.Equal(111, audio.OpusPayloadType);
        // Done
        Assert.Equal("192.0.2.10:50000", Assert.Single(audio.Candidates).Address.ToString());
    }

    /// <summary>Answer the browser's offer: audio accepted with this side active, video rejected, only audio bundled.</summary>
    [Fact]
    public void AnswerBrowserOffer()
    {
        // Answer, parsed back
        LocalAudio local = new("Xy9z", "zyxwvutsrqponmlkjihgfedc", "sha-256 01:02", 1234, "me");
        SessionDescription answer = SessionDescription.Parse(AudioSdp.CreateAnswer(SessionDescription.Parse(BrowserOffer), local).ToString());
        Assert.Equal("BUNDLE 0", answer.Get("group"));
        Assert.Equal(2, answer.Media.Count);
        Assert.Equal(0, answer.Media[1].Port);
        Assert.Equal("1", answer.Media[1].Get("mid"));

        // Our audio as the browser would read it
        RemoteAudio audio = AudioSdp.Read(answer)!;
        Assert.Equal("active", audio.Setup);
        Assert.Equal("Xy9z", audio.Ufrag);
        // Done
        Assert.NotNull(answer.Media[0].Get("rtcp-mux"));
    }

    /// <summary>Offer audio that reads back the same.</summary>
    [Fact]
    public void Offer()
    {
        // Offer, parsed back
        LocalAudio local = new("Xy9z", "zyxwvutsrqponmlkjihgfedc", "sha-256 01:02", 1234, "me");
        string text = AudioSdp.CreateOffer(local).ToString();
        Assert.Contains("\r\na=setup:actpass\r\n", text);
        RemoteAudio audio = AudioSdp.Read(SessionDescription.Parse(text))!;
        // Done
        Assert.Equal(new RemoteAudio("Xy9z", "zyxwvutsrqponmlkjihgfedc", "sha-256 01:02", "actpass", "0", 111, []), audio with { Candidates = [] });
    }
}
