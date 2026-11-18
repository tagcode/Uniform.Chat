# Uniform.MatrixRtc.Protocol

the SFU access tokens and MatrixRTC authorization messages, in pure C#:

- `AccessToken` and `VideoGrant`: the SFU's HS256 JWT, to sign and to check.
- `MatrixRtcNames`: The SFU room names and participant identities of MatrixRTC calls, as lk-jwt-service makes them.
- `TokenRequest`, `LegacySfuRequest` and `SfuResponse`: the JSON of `/get_token` and `/sfu/get`.
