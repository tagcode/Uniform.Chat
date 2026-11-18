---
title: Settings
uid: Uniform.LiveKit.Server.Settings.Article
---
# Settings

The service's settings are in section `LiveKit`. Changes take effect at the next request, without a restart. lk-jwt-service's environment variables set the same settings, shown in the last column.

| Option | Default | Meaning |
|---|---|---|
| `LiveKit:Enabled` | `false` | Serve the endpoints; this program's `appsettings.json` sets it to `true`. |
| `LiveKit:Url` | not set | Address of the LiveKit server that apps join, such as `wss://livekit.example.org`. `LIVEKIT_URL` |
| `LiveKit:ApiKey` | not set | API key of the LiveKit server, one of the `keys` in its configuration. `LIVEKIT_KEY` or `LIVEKIT_KEY_FILE` |
| `LiveKit:ApiSecret` | not set | Secret of that API key. Keep it out of command lines. `LIVEKIT_SECRET` or `LIVEKIT_SECRET_FILE` |
| `LiveKit:FullAccessHomeservers` | not set | Homeservers whose users may start calls and send audio and video, separated by commas, or `*` for all; users of other servers can only watch and listen in calls that are already going. Required. `LIVEKIT_FULL_ACCESS_HOMESERVERS` |
| `LiveKit:TokenLifetime` | `01:00:00` | How long a LiveKit token can be used to join; a call that has started goes on. |
| `LiveKit:CanUpdateOwnMetadata` | `false` | Let participants change their own name and metadata in LiveKit. |
| `Logging:LogLevel:Default` | `Information` | Least important log entries written: `Information` logs every issued token, `Warning` only refusals. |

The address to listen on is `Urls`, `http://0.0.0.0:8080` by default, or `LIVEKIT_JWT_BIND`, such as `:8080`.
