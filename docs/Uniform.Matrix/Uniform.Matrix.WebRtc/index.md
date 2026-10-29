---
uid: Uniform.Matrix.WebRtc.Article
title: Uniform.Matrix.WebRtc
---
# Uniform.Matrix.WebRtc

The subset of WebRTC that a 1:1 voice call needs, in C#: one Opus audio stream each way, interoperating with browsers and Element. Only DTLS uses a library, BouncyCastle; the rest is written here against the RFCs and their test vectors. Nothing here knows about Matrix; [Uniform.Matrix.Calls](xref:Uniform.Matrix.Calls.Article) builds calls on it.

## Layers

| Folder | What | Specification |
|---|---|---|
| `Stun/` | [`StunMessage`](xref:Uniform.Matrix.WebRtc.Stun.StunMessage) with MESSAGE-INTEGRITY and FINGERPRINT, XOR'ed addresses, and [`StunClient`](xref:Uniform.Matrix.WebRtc.Stun.StunClient) transactions with retransmission | [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489) |
| `Turn/` | [`TurnClient`](xref:Uniform.Matrix.WebRtc.Turn.TurnClient): allocation with long-term credentials, permissions, channels, Send and Data indications, refreshes | [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656) |
| `Ice/` | [`IceAgent`](xref:Uniform.Matrix.WebRtc.Ice.IceAgent): host, server-reflexive and relayed candidates, connectivity checks, role conflicts, nomination, peer-reflexive candidates, consent; [`IceCandidate`](xref:Uniform.Matrix.WebRtc.Ice.IceCandidate) as SDP writes it | [RFC 8445](https://www.rfc-editor.org/rfc/rfc8445), [RFC 8839](https://www.rfc-editor.org/rfc/rfc8839), [RFC 7675](https://www.rfc-editor.org/rfc/rfc7675) |
| `Dtls/` | [`DtlsSrtp`](xref:Uniform.Matrix.WebRtc.Dtls.DtlsSrtp): DTLS 1.2 with BouncyCastle, use_srtp, the peer's certificate checked against its fingerprint, keys exported; [`DtlsCertificate`](xref:Uniform.Matrix.WebRtc.Dtls.DtlsCertificate) self-signed ECDSA | [RFC 5764](https://www.rfc-editor.org/rfc/rfc5764) |
| `Srtp/` | [`SrtpSession`](xref:Uniform.Matrix.WebRtc.Srtp.SrtpSession): SRTP and SRTCP with AES_CM_128_HMAC_SHA1_80, rollover counters, replay windows | [RFC 3711](https://www.rfc-editor.org/rfc/rfc3711) |
| `Rtp/` | [`RtpPacket`](xref:Uniform.Matrix.WebRtc.Rtp.RtpPacket) and [`JitterBuffer`](xref:Uniform.Matrix.WebRtc.Rtp.JitterBuffer) | [RFC 3550](https://www.rfc-editor.org/rfc/rfc3550) |
| `Sdp/` | [`SessionDescription`](xref:Uniform.Matrix.WebRtc.Sdp.SessionDescription), and [`AudioSdp`](xref:Uniform.Matrix.WebRtc.Sdp.AudioSdp) offers and answers: bundled, RTCP multiplexed, other media rejected | [RFC 8866](https://www.rfc-editor.org/rfc/rfc8866), [RFC 8829](https://www.rfc-editor.org/rfc/rfc8829) |

One UDP socket carries everything. Its datagrams are told apart by their first byte, as [RFC 7983](https://www.rfc-editor.org/rfc/rfc7983) says: STUN 0–3, DTLS 20–63, TURN channel data 64–79, and RTP and RTCP 128–191.

## A connection

[`AudioPeerConnection`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection) puts the layers together. The caller makes an offer, the callee answers, both trickle their candidates to the other, and both connect. The offerer controls ICE, and the answerer is the DTLS client, as browsers do.

The following shows both sides in one process; in a call, the strings travel in Matrix events:

```csharp
await using AudioPeerConnection caller = new(), callee = new();
caller.CandidateGathered += candidate => callee.AddRemoteCandidate(candidate.ToString());
callee.CandidateGathered += candidate => caller.AddRemoteCandidate(candidate.ToString());
callee.AudioReceived += packet => Play(decoder.Decode(packet.Payload));

string answer = callee.AcceptOffer(caller.CreateOffer());
caller.AcceptAnswer(answer);
await Task.WhenAll(caller.ConnectAsync(), callee.ConnectAsync());
await caller.SendAudioAsync(encoder.Encode(frame));
```

TURN servers, e.g. those of a homeserver's `/voip/turnServer`, go in [`PeerConnectionOptions.Servers`](xref:Uniform.Matrix.WebRtc.PeerConnectionOptions.Servers); [`RelayOnly`](xref:Uniform.Matrix.WebRtc.PeerConnectionOptions.RelayOnly) keeps the call on the relay, so the peer does not learn this device's addresses.

Not supported: IPv6 and TCP candidates, mDNS host names (browsers' hidden host candidates; the peer-reflexive path covers them), ICE restarts, renegotiation, and video.

## Tests

Uniform.Matrix.WebRtc.Tests checks each layer:

- STUN against the vectors of [RFC 5769](https://www.rfc-editor.org/rfc/rfc5769), and SRTP against those of RFC 3711 and libsrtp;
- TURN against an in-process fake server, and against a real one when `UNIFORM_MATRIX_TURN` (`host:port`) and `UNIFORM_MATRIX_TURN_SECRET` (the shared secret) are set;
- ICE between two agents directly, with a role conflict, and through two fake TURN servers; DTLS between two sides in memory;
- a whole call with headless Chromium's WebRTC, driven over the DevTools protocol, when Chromium is installed: each side must decode the other's tone.
