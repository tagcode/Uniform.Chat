---
uid: Uniform.LiveKit.Protocol.Article
title: Uniform.LiveKit.Protocol
---
# Uniform.LiveKit.Protocol

What MatrixRTC calls and their authorization service exchange, with no network code.

- [`AccessToken`](xref:Uniform.LiveKit.Protocol.AccessToken) holds the claims of a LiveKit access token. [`Sign`](xref:Uniform.LiveKit.Protocol.AccessToken.Sign*) makes the HS256 JWT with an API secret, and [`TryRead`](xref:Uniform.LiveKit.Protocol.AccessToken.TryRead*) checks one. [`VideoGrant`](xref:Uniform.LiveKit.Protocol.VideoGrant) is its `video` claim, the room permissions. Unset permissions are left out, as LiveKit reads some missing ones as allowed.
- [`MatrixRtcNames`](xref:Uniform.LiveKit.Protocol.MatrixRtcNames) makes LiveKit room names and participant identities as [lk-jwt-service](https://github.com/element-hq/lk-jwt-service) does. Each is the SHA-256 of a JSON array of strings, in unpadded Base64. The array is escaped as Rust's serde_json escapes it, so every client and service arrives at the same name. The legacy `/sfu/get` identity is `@user:server:DEVICE` in plain text.
- The JSON bodies of the service: [`TokenRequest`](xref:Uniform.LiveKit.Protocol.TokenRequest) of `/get_token`, [`LegacySfuRequest`](xref:Uniform.LiveKit.Protocol.LegacySfuRequest) of `/sfu/get`, and [`SfuResponse`](xref:Uniform.LiveKit.Protocol.SfuResponse) answering both. Failures answer with [`AuthorizationError`](xref:Uniform.LiveKit.Protocol.AuthorizationError).
