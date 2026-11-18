---
uid: Uniform.MatrixRtc.Protocol.Article
title: Uniform.MatrixRtc.Protocol
---
# Uniform.MatrixRtc.Protocol

What MatrixRTC calls and their authorization service exchange, with no network code.

- LiveKit's signalling messages, such as `SignalRequest`, `SignalResponse` and `JoinResponse` in the namespace `LiveKit.Proto`, are generated with Google.Protobuf from LiveKit's own protocol files in `Protos/`, copied unchanged.
- [`AccessToken`](xref:Uniform.MatrixRtc.Protocol.AccessToken) holds the claims of a SFU access token. [`Sign`](xref:Uniform.MatrixRtc.Protocol.AccessToken.Sign*) makes the HS256 JWT with an API secret, and [`TryRead`](xref:Uniform.MatrixRtc.Protocol.AccessToken.TryRead*) checks one. [`VideoGrant`](xref:Uniform.MatrixRtc.Protocol.VideoGrant) is its `video` claim, the room permissions. Unset permissions are left out, as LiveKit reads some missing ones as allowed.
- [`MatrixRtcNames`](xref:Uniform.MatrixRtc.Protocol.MatrixRtcNames) makes SFU room names and participant identities as [lk-jwt-service](https://github.com/element-hq/lk-jwt-service) does. Each is the SHA-256 of a JSON array of strings, in unpadded Base64. The array is escaped as Rust's serde_json escapes it, so every client and service arrives at the same name. The legacy `/sfu/get` identity is `@user:server:DEVICE` in plain text.
- The JSON bodies of the service: [`TokenRequest`](xref:Uniform.MatrixRtc.Protocol.TokenRequest) of `/get_token`, [`LegacySfuRequest`](xref:Uniform.MatrixRtc.Protocol.LegacySfuRequest) of `/sfu/get`, and [`SfuResponse`](xref:Uniform.MatrixRtc.Protocol.SfuResponse) answering both. Failures answer with [`AuthorizationError`](xref:Uniform.MatrixRtc.Protocol.AuthorizationError).
