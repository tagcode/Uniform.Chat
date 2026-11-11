---
title: Homeservers
uid: Uniform.LiveKit.Server.Homeservers.Article
---
# Homeservers

Apps find the service through their homeserver, which announces it as a MatrixRTC focus. Announce the address the reverse proxy serves, such as `https://example.org/livekit/jwt`.

## Synapse

Apps read the focus from `/.well-known/matrix/client` of your domain, which your web server usually serves for Synapse. Add the focus there:

```json
{
  "m.homeserver": { "base_url": "https://matrix.example.org" },
  "org.matrix.msc4143.rtc_foci": [
    { "type": "livekit", "livekit_service_url": "https://example.org/livekit/jwt" }
  ]
}
```

## Uniform Chat Server

Uniform Chat Server has this service built in, see its guide. To use a separate one instead, set `Calls:LiveKitServiceUrl`:

```json
{
  "Calls": {
    "LiveKitServiceUrl": "https://example.org/livekit/jwt"
  }
}
```

## Users of other homeservers

`LiveKit:FullAccessHomeservers` lists the homeservers whose users start calls. Users of other homeservers can join a call that one of them started, but only to watch and listen. List their servers as well to let them speak, or `*` for everyone.
