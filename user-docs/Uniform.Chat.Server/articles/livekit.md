---
title: Video calls in Element X
uid: Uniform.Chat.Server.LiveKit.Article
---
# Video calls in Element X

Element X, Element Call and other MatrixRTC apps don't call each other directly. They all join a room on a LiveKit server, a media server that forwards everyone's audio and video. Before joining, an app asks the homeserver's authorization service for a LiveKit token, proving who it is with a Matrix OpenID token.

The homeserver has that authorization service built in. You run the LiveKit server next to it. Uniform Chat's own calls don't need any of this; see [Calls through firewalls](xref:Uniform.Chat.Server.Turn.Article) for those.

## LiveKit server

Install LiveKit from [livekit.io](https://docs.livekit.io/home/self-hosting/local/). It is a single program. Write its configuration, `livekit.yaml`, with an API key and a secret of at least 32 characters:

```yaml
port: 7880
rtc:
  tcp_port: 7881
  port_range_start: 50000
  port_range_end: 60000
  use_external_ip: true
keys:
  homeserver: a-long-random-secret-of-at-least-32-characters
room:
  auto_create: false
```

`auto_create: false` makes sure only your users can start calls. The authorization service creates each call's room for them. Run it with `livekit-server --config livekit.yaml`.

LiveKit must be reachable from the internet on these ports. Behind a home router, forward them to its computer:

| Port | Protocol | Use |
|---|---|---|
| 443 | TCP | The apps' WebSocket, through a reverse proxy with a certificate, to LiveKit's 7880 |
| 7881 | TCP | Media over TCP, for networks that block UDP |
| 50000–60000 | UDP | Media |

The apps connect to `wss://`, so put a reverse proxy with a certificate in front of port 7880, at a name such as `livekit.example.org`. With Caddy, for example:

```text
livekit.example.org {
    reverse_proxy localhost:7880
}
```

## Homeserver

Turn on the authorization service and point it at LiveKit. Put the secret in the settings file or an environment variable, not on the command line:

```json
{
  "LiveKit": {
    "Enabled": true,
    "Url": "wss://livekit.example.org",
    "ApiKey": "homeserver",
    "ApiSecret": "a-long-random-secret-of-at-least-32-characters"
  }
}
```

The service is then at `/livekit/jwt` of the homeserver's `Server:PublicBaseUrl`, and the homeserver announces it to the apps. No restart is needed. Your users can start calls and send audio and video. Users of other homeservers can join calls that are already going, but only to watch and listen, unless you add their servers to `LiveKit:FullAccessHomeservers`. See [Settings](xref:Uniform.Chat.Server.Settings.Article) for every `LiveKit` setting.

To check the service, open `https://example.org/livekit/jwt/healthz`, with your server's address. It answers with an empty page and status 200 when the service is on, and 404 when it is off.

## Using another service

If you already run lk-jwt-service or a separate `uniform-livekit-server`, leave `LiveKit:Enabled` off. Set `Calls:LiveKitServiceUrl` to that service's address, such as `https://example.org/livekit/jwt`, and the homeserver announces it instead.
