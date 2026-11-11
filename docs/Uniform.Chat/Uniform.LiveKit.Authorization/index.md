---
uid: Uniform.LiveKit.Authorization.Article
title: Uniform.LiveKit.Authorization
---
# Uniform.LiveKit.Authorization

A MatrixRTC authorization service that behaves like [lk-jwt-service](https://github.com/element-hq/lk-jwt-service). Element Call, Element X and other MatrixRTC clients ask it for LiveKit tokens.

[`LiveKitAuthorizer`](xref:Uniform.LiveKit.Authorization.LiveKitAuthorizer) handles both requests:

1. It checks the fields. A missing one answers 400 `M_BAD_JSON`.
2. It asks the token's homeserver who the token belongs to, through an [`IOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.IOpenIdVerifier).
3. The user must belong to that same homeserver, as a homeserver could otherwise vouch for anyone. In `/get_token`, the user must also be the member's claimed user. Every failure answers 401 `M_UNAUTHORIZED` with the same text.
4. Users of [`FullAccessHomeservers`](xref:Uniform.LiveKit.Authorization.LiveKitAuthorizerOptions.FullAccessHomeservers) get the room created in LiveKit first. The room waits 5 minutes for its first participant and stays 20 seconds after the last one leaves. Their token may publish. Other users only subscribe, and only to rooms that exist. They may not hand over a delayed leave event (`delay_id`).
5. It answers with LiveKit's URL and a token that holds for an hour.

[`FederationOpenIdVerifier`](xref:Uniform.LiveKit.Authorization.FederationOpenIdVerifier) asks `/_matrix/federation/v1/openid/userinfo`. It goes to the server name's `m.server` delegation, or to the name itself, at port 8448 when no port is given.

The delayed leave events of `delay_id` are accepted but not sent yet. That needs LiveKit's webhooks. Clients keep their own delayed events alive meanwhile.
