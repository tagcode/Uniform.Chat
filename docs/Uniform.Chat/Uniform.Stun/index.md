---
uid: Uniform.Stun.Article
title: Uniform.Stun
---
# Uniform.Stun

STUN and TURN clients on a UDP socket that the caller owns and reads, as ICE shares one socket for everything: the caller gives STUN responses to [`StunClient.Receive`](xref:Uniform.Stun.StunClient.Receive*) and relayed data to [`TurnClient.TryUnwrap`](xref:Uniform.Stun.TurnClient.TryUnwrap*).

- [`StunClient`](xref:Uniform.Stun.StunClient): transactions with the retransmissions of [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489#section-6.2.1).
- [`TurnClient`](xref:Uniform.Stun.TurnClient): allocation with long-term credentials and their 401 and 438 challenges, permissions, channels, Send and Data indications, and refreshes until disposed or expired.

[Uniform.Matrix.WebRtc](xref:Uniform.Matrix.WebRtc.Article) uses both for ICE. Over TCP or TLS, the caller frames the stream with [`ChannelData.FrameLength`](xref:Uniform.Stun.Protocol.ChannelData.FrameLength*) and pads channel data, as the Uniform.Turn tests do.
