---
uid: Uniform.LiveKit.Article
title: Uniform.LiveKit
---
# Uniform.LiveKit

Clients of the LiveKit side of MatrixRTC, over an [`HttpClient`](xref:System.Net.Http.HttpClient) the caller gives:

- [`MatrixRtcAuthorizationClient`](xref:Uniform.LiveKit.MatrixRtcAuthorizationClient) sends a Matrix OpenID token to the authorization service named by a homeserver's `rtc_foci`. It gets back LiveKit's URL and an access token for the call.
- [`LiveKitRoomService`](xref:Uniform.LiveKit.LiveKitRoomService) calls LiveKit's room service over Twirp with JSON. Each call carries a one-minute token signed with the API secret. [`CreateRoomAsync`](xref:Uniform.LiveKit.LiveKitRoomService.CreateRoomAsync*) creates a call's room, and keeps a room that already exists.

The messages are in [Uniform.LiveKit.Protocol](xref:Uniform.LiveKit.Protocol.Article). Joining a room over LiveKit's signalling comes later, for MatrixRTC calls in the apps.
