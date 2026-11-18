# Uniform.MatrixRtc.Authorization

A MatrixRTC authorization service that behaves like lk-jwt-service:

- `MatrixRtcAuthorizer` verifies a Matrix OpenID token with the user's homeserver and issues an SFU access token for the call's room. Users of full-access homeservers may publish, and get the room created. Others only join rooms that exist.
- `FederationOpenIdVerifier` asks the homeserver over the federation API, following `.well-known/matrix/server` delegation.

Uniform.MatrixRtc.Hosting serves it in ASP.NET.
