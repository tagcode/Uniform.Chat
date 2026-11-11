---
uid: Uniform.LiveKit.Authorization.Article
title: Uniform.LiveKit.Authorization
---
# Uniform.LiveKit.Authorization

A MatrixRTC authorization service that behaves like [lk-jwt-service](https://github.com/element-hq/lk-jwt-service). Element Call, Element X and other MatrixRTC clients ask it for LiveKit tokens.

[`LiveKitAuthorizer`](xref:Uniform.LiveKit.Authorization.LiveKitAuthorizer) handles both requests:

1. It checks the fields. A missing one answers 400 `M_BAD_JSON`.
2. It asks the token's homeserver who the token belongs to, through an [`IOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.IOpenIdVerifier).
3. The user must belong to that same homeserver, as a homeserver could otherwise vouch for anyone. In `/get_token`, the user must also be the member's claimed user. When the authorizer was given an [`IRoomMembership`](xref:Uniform.LiveKit.Authorization.IRoomMembership), the user must also be joined to the Matrix room. A service inside a homeserver knows that from the room's state; lk-jwt-service and a standalone service don't check it. Every failure answers 401 `M_UNAUTHORIZED` with the same text.
4. Users of [`FullAccessHomeservers`](xref:Uniform.LiveKit.Authorization.LiveKitAuthorizerOptions.FullAccessHomeservers) get the room created in LiveKit first. The room waits 5 minutes for its first participant and stays 20 seconds after the last one leaves. Their token may publish. Other users only subscribe, and only to rooms that exist. They may not hand over a delayed leave event (`delay_id`).
5. It answers with LiveKit's URL and a token that holds for an hour.

[`FederationOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.FederationOpenIdVerifier) asks `/_matrix/federation/v1/openid/userinfo`. It goes to the server name's `m.server` delegation, or to the name itself, at port 8448 when no port is given.

## Delayed leaves

A full-access member may hand its delayed leave event ([MSC4140](https://github.com/matrix-org/matrix-spec-proposals/pull/4140)) to the service, with `delay_id` and `delay_timeout` in its token request, or at `/delegate_delayed_leave`. Element Call looks for that endpoint first, and hands its leave over when the endpoint answers anything but 404. Its device then restarts the leave only every 5 minutes instead of every 4 seconds.

[`DelayedLeaveKeeper`](xref:Uniform.LiveKit.Authorization.DelayedLeaveKeeper) keeps each handed-over leave:

1. It waits for the member to appear in its LiveKit room, asking LiveKit's room service.
2. While the member stays, it restarts the delayed event at four fifths of its delay, at most an hour, as lk-jwt-service does.
3. When LiveKit no longer has the member, checked every 5 seconds, it sends the leave at once, so the call membership ends.

It acts on the delayed events through [`IDelayedEvents`](xref:Uniform.LiveKit.Authorization.IDelayedEvents). [`HomeserverDelayedEvents`](xref:Uniform.LiveKit.Authorization.HomeserverDelayedEvents) finds the member's homeserver by its server name's `/.well-known/matrix/client`, never by the `delay_cs_api_url` the client gives, so the service only calls homeservers. The service inside Uniform Chat Server acts on its own users' delayed events directly. LiveKit needs no webhooks for this. The jobs live in memory: after a restart of the service, a member's homeserver sends the leave when its delay runs out.
