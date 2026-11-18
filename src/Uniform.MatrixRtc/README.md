# Uniform.MatrixRtc

An SFU client in pure C#, for MatrixRTC calls:

- `SfuRoom` joins a room over the SFU's signalling, publishes the microphone and camera, and receives the others' tracks, over WebRTC connections of Uniform.Matrix.WebRtc.
- `FrameCryptor` encrypts and decrypts frames end to end, as the SFU's clients and Element Call do.
- `SfuMedia` puts a call's rooms and keys together as one media connection.
- `MatrixRtcAuthorizationClient` exchanges a Matrix OpenID token for the SFU's URL and an access token, at a homeserver's MatrixRTC focus (lk-jwt-service).
- `SfuRoomService` calls the SFU's room service over Twirp, e.g. to create a call's room.
