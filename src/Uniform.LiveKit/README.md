# Uniform.LiveKit

Clients of the LiveKit side of MatrixRTC calls:

- `MatrixRtcAuthorizationClient` exchanges a Matrix OpenID token for LiveKit's URL and an access token, at a homeserver's MatrixRTC focus (lk-jwt-service).
- `LiveKitRoomService` calls LiveKit's room service over Twirp, e.g. to create a call's room.
