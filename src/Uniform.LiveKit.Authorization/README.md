# Uniform.LiveKit.Authorization

A MatrixRTC authorization service that behaves like lk-jwt-service:

- `LiveKitAuthorizer` verifies a Matrix OpenID token with the user's homeserver and issues a LiveKit access token for the call's room. Users of full-access homeservers may publish, and get the room created. Others only join rooms that exist.
- `FederationOpenIdVerifier` asks the homeserver over the federation API, following `.well-known/matrix/server` delegation.

Uniform.LiveKit.Hosting serves it in ASP.NET.
