# Uniform.LiveKit

A LiveKit client in pure C#, for MatrixRTC calls:

- `LiveKitRoom` joins a room over LiveKit's signalling, publishes the microphone and camera, and receives the others' tracks, over WebRTC connections of Uniform.Matrix.WebRtc.
- `FrameCryptor` encrypts and decrypts frames end to end, as LiveKit's clients and Element Call do.
- `LiveKitMedia` puts a call's rooms and keys together as one media connection.
- `MatrixRtcAuthorizationClient` exchanges a Matrix OpenID token for LiveKit's URL and an access token, at a homeserver's MatrixRTC focus (lk-jwt-service).
- `LiveKitRoomService` calls LiveKit's room service over Twirp, e.g. to create a call's room.
