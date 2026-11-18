---
title: Settings
uid: Uniform.MatrixRtc.Server.Settings.Article
---
# Settings

The service's settings are in section `MatrixRtc`. Changes take effect at the next request, without a restart. Environment variables set the same settings, shown in the last column. They are lk-jwt-service's with `MATRIXRTC_` in place of `LIVEKIT_`: when you move from lk-jwt-service, rename them.

| Option | Default | Meaning |
|---|---|---|
| `MatrixRtc:Enabled` | `false` | Serve the endpoints; this program's `appsettings.json` sets it to `true`. |
| `MatrixRtc:Url` | not set | Address of the LiveKit server that apps join, such as `wss://livekit.example.org`. `MATRIXRTC_URL` |
| `MatrixRtc:ApiKey` | not set | API key of the LiveKit server, one of the `keys` in its configuration. `MATRIXRTC_KEY` or `MATRIXRTC_KEY_FILE` |
| `MatrixRtc:ApiSecret` | not set | Secret of that API key. Keep it out of command lines. `MATRIXRTC_SECRET` or `MATRIXRTC_SECRET_FILE` |
| `MatrixRtc:FullAccessHomeservers` | not set | Homeservers whose users may start calls and send audio and video, separated by commas, or `*` for all; users of other servers can only watch and listen in calls that are already going. Required. `MATRIXRTC_FULL_ACCESS_HOMESERVERS` |
| `MatrixRtc:TokenLifetime` | `01:00:00` | How long a SFU token can be used to join; a call that has started goes on. |
| `MatrixRtc:CanUpdateOwnMetadata` | `false` | Let participants change their own name and metadata on the SFU. |
| `Logging:LogLevel:Default` | `Information` | Least important log entries written: `Information` logs every issued token, `Warning` only refusals. |

The address to listen on is `Urls`, `http://0.0.0.0:8080` by default, or `MATRIXRTC_JWT_BIND`, such as `:8080`.
