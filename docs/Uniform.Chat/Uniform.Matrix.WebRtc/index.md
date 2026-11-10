---
uid: Uniform.Matrix.WebRtc.Article
title: Uniform.Matrix.WebRtc
---
# Uniform.Matrix.WebRtc

The subset of WebRTC that a 1:1 call needs, in C#: one Opus audio stream each way, and VP8 video each way, interoperating with browsers and Element. Only DTLS uses a library, BouncyCastle; the rest is written here against the RFCs and their test vectors. Nothing here knows about Matrix; [Uniform.Matrix.Calls](xref:Uniform.Matrix.Calls.Article) builds calls on it.

## Layers

STUN and TURN come from [Uniform.Stun](xref:Uniform.Stun.Article): [`StunClient`](xref:Uniform.Stun.StunClient) transactions and the [`TurnClient`](xref:Uniform.Stun.TurnClient), on the messages of [Uniform.Stun.Protocol](xref:Uniform.Stun.Protocol.Article).

| Folder | What | Specification |
|---|---|---|
| `Ice/` | [`IceAgent`](xref:Uniform.Matrix.WebRtc.Ice.IceAgent): host, server-reflexive and relayed candidates, connectivity checks, role conflicts, nomination, peer-reflexive candidates, consent; [`IceCandidate`](xref:Uniform.Matrix.WebRtc.Ice.IceCandidate) as SDP writes it | [RFC 8445](https://www.rfc-editor.org/rfc/rfc8445), [RFC 8839](https://www.rfc-editor.org/rfc/rfc8839), [RFC 7675](https://www.rfc-editor.org/rfc/rfc7675) |
| `Dtls/` | [`DtlsSrtp`](xref:Uniform.Matrix.WebRtc.Dtls.DtlsSrtp): DTLS 1.2 with BouncyCastle, use_srtp, the peer's certificate checked against its fingerprint, keys exported; [`DtlsCertificate`](xref:Uniform.Matrix.WebRtc.Dtls.DtlsCertificate) self-signed ECDSA | [RFC 5764](https://www.rfc-editor.org/rfc/rfc5764) |
| `Srtp/` | [`SrtpSession`](xref:Uniform.Matrix.WebRtc.Srtp.SrtpSession): SRTP and SRTCP with AES_CM_128_HMAC_SHA1_80, rollover counters, replay windows | [RFC 3711](https://www.rfc-editor.org/rfc/rfc3711) |
| `Rtp/` | [`RtpPacket`](xref:Uniform.Matrix.WebRtc.Rtp.RtpPacket) and [`JitterBuffer`](xref:Uniform.Matrix.WebRtc.Rtp.JitterBuffer); VP8 frames put together by [`Vp8Depacketizer`](xref:Uniform.Matrix.WebRtc.Rtp.Vp8Depacketizer) and split by [`Vp8Packetizer`](xref:Uniform.Matrix.WebRtc.Rtp.Vp8Packetizer); receiver reports and picture loss indications of [`Rtcp`](xref:Uniform.Matrix.WebRtc.Rtp.Rtcp), counted by [`ReceiverStatistics`](xref:Uniform.Matrix.WebRtc.Rtp.ReceiverStatistics) | [RFC 3550](https://www.rfc-editor.org/rfc/rfc3550), [RFC 7741](https://www.rfc-editor.org/rfc/rfc7741), [RFC 4585](https://www.rfc-editor.org/rfc/rfc4585) |
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

## Received video

With [`PeerConnectionOptions.ReceiveVideo`](xref:Uniform.Matrix.WebRtc.PeerConnectionOptions.ReceiveVideo), an answer accepts the VP8 video of the peer's offer as `recvonly`, bundled with the audio; [`ReceivesVideo`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.ReceivesVideo) tells whether it did. The packets are put together into whole frames, given in order in [`VideoReceived`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.VideoReceived), for a decoder such as that of [Uniform.Matrix.Video](xref:Uniform.Matrix.Video.Article).

A frame waits for packets that come out of order. After a packet is lost, only a keyframe is given, and the connection asks the peer for one with an RTCP picture loss indication; [`RequestKeyframeAsync`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.RequestKeyframeAsync*) asks for one too, e.g. when a frame did not decode. Receiver reports go to the peer every second, so it can see the loss and the round trip.

## Sent video

With [`PeerConnectionOptions.SendVideo`](xref:Uniform.Matrix.WebRtc.PeerConnectionOptions.SendVideo), an answer also sends video when the peer's offer receives it; the section names this side's video SSRC and stream, so browsers make a track of it. [`SendVideoAsync`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.SendVideoAsync*) takes encoded VP8 frames, e.g. from a hardware encoder, and sends each in RTP packets of at most 1100 bytes, timed by a 90 kHz clock from the moment of sending. When the peer asks for a keyframe, with a picture loss indication or a full intra request, [`KeyframeRequested`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.KeyframeRequested) tells the encoder.

A call's caller offers video with [`CreateOffer(video: true)`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.CreateOffer*): both ways, and the answer decides which ways it goes.

## Loss, bitrate and orientation

- **Lost packets:** the last 512 video packets sent are kept, and sent again, as sent, when the peer asks with a generic NACK, [RFC 4585 6.2.1](https://www.rfc-editor.org/rfc/rfc4585#section-6.2.1). A gap in the received video is asked for at once, before its frames wait for a keyframe.
- **Bitrate:** each receiver report carries a REMB with what the peer may send: the loss-based estimate of [`BandwidthEstimator`](xref:Uniform.Matrix.WebRtc.Rtp.BandwidthEstimator), down on heavy loss and slowly up on little, within the cap of [`RequestBitrateAsync`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.RequestBitrateAsync*), e.g. when this device cannot decode more. The peer's REMBs, also Chromium's estimates as we announce `goog-remb`, come in [`BitrateRequested`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection.BitrateRequested) for the encoder.
- **Sender reports:** each second, a sender report of the video and one of the audio sent pair the wall-clock time with each stream's RTP time, so the peer can line picture and sound up, and carry the counts sent.
- **Orientation:** the 3GPP video orientation header extension, `urn:3gpp:video-orientation`, is offered and answered; a sent frame's rotation goes on its last packet, and a received frame's comes with it in [`Vp8EncodedFrame.Rotation`](xref:Uniform.Matrix.WebRtc.Rtp.Vp8EncodedFrame.Rotation).

Not supported: IPv6 and TCP candidates, mDNS host names (browsers' hidden host candidates; the peer-reflexive path covers them), renegotiation other than ICE restarts, lining up received video with audio, RTX retransmission streams, and transport-wide congestion control.

## Tests

Uniform.Matrix.WebRtc.Tests checks each layer:

- STUN against the vectors of [RFC 5769](https://www.rfc-editor.org/rfc/rfc5769), and SRTP against those of RFC 3711 and libsrtp;
- TURN against an in-process fake server, and against a real one when `UNIFORM_MATRIX_TURN` (`host:port`) and `UNIFORM_MATRIX_TURN_SECRET` (the shared secret) are set;
- ICE between two agents directly, with a role conflict, and through two fake TURN servers; DTLS between two sides in memory;
- a whole call with headless Chromium's WebRTC, driven over the DevTools protocol, when Chromium is installed: each side must decode the other's tone.
