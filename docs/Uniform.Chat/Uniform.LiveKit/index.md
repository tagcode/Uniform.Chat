---
uid: Uniform.LiveKit.Article
title: Uniform.LiveKit
---
# Uniform.LiveKit

A LiveKit client in C#, for MatrixRTC calls, and clients of LiveKit's HTTP services.

## Joining a room

- [`LiveKitSignalClient`](xref:Uniform.LiveKit.LiveKitSignalClient) is LiveKit's signalling: a WebSocket to the server's `/rtc` with protobuf messages each way, and pings at the interval the server asks. It joins with two peer connections, the way LiveKit's clients did before the single one.
- [`LiveKitRoom`](xref:Uniform.LiveKit.LiveKitRoom) is a participant in a room. The server offers the subscriber connection, with a section for each track of the others, and offers it again as tracks come and go. [`PublishAsync`](xref:Uniform.LiveKit.LiveKitRoom.PublishAsync*) announces the microphone and the camera with `AddTrackRequest`, then offers them on a send-only publisher connection. [`IdentityOf`](xref:Uniform.LiveKit.LiveKitRoom.IdentityOf*) tells whose a received stream is, from the `msid` of its section. Both connections are [`AudioPeerConnection`](xref:Uniform.Matrix.WebRtc.AudioPeerConnection)s of [Uniform.Matrix.WebRtc](xref:Uniform.Matrix.WebRtc.Article).
- [`FrameCryptor`](xref:Uniform.LiveKit.FrameCryptor) encrypts frames end to end as LiveKit's clients and Element Call do. Each frame keeps its first bytes in the clear (1 of Opus, 10 of a VP8 keyframe, 3 of other VP8 frames) and carries AES-GCM ciphertext, its IV and the key's index. Keys come from raw keys through HKDF-SHA-256 with the salt `LKFrameEncryptionKey`.
- [`LiveKitMedia`](xref:Uniform.LiveKit.LiveKitMedia) makes a call's rooms one [`IMediaConnection`](xref:Uniform.Matrix.WebRtc.IMediaConnection): it encrypts this side's frames on the room it publishes to, and decrypts the others' with each sender's key on the rooms it subscribes to.

## HTTP services

Over an [`HttpClient`](xref:System.Net.Http.HttpClient) the caller gives:

- [`MatrixRtcAuthorizationClient`](xref:Uniform.LiveKit.MatrixRtcAuthorizationClient) sends a Matrix OpenID token to the authorization service named by a homeserver's `rtc_foci`. It gets back LiveKit's URL and an access token for the call.
- [`LiveKitRoomService`](xref:Uniform.LiveKit.LiveKitRoomService) calls LiveKit's room service over Twirp with JSON. Each call carries a one-minute token signed with the API secret. [`CreateRoomAsync`](xref:Uniform.LiveKit.LiveKitRoomService.CreateRoomAsync*) creates a call's room, and keeps a room that already exists.

The messages are in [Uniform.LiveKit.Protocol](xref:Uniform.LiveKit.Protocol.Article).

## Tests

Uniform.LiveKit.Tests checks the frame encryption against frames made with Python's `cryptography` in LiveKit's layout. With `LIVEKIT_SERVER` naming LiveKit's server program, it also starts one on loopback. One participant publishes encrypted audio and VP8 video, and another must receive and decrypt them.
