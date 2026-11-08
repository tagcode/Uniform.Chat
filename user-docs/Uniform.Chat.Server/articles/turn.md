---
title: Calls through firewalls
uid: Uniform.Chat.Server.Turn.Article
---
# Calls through firewalls

Voice calls go straight from one phone or computer to the other when the networks allow it. Many don't: mobile networks, office firewalls and some home routers block such direct traffic, and then the call connects but nobody hears anything. A TURN server fixes this by relaying the call. The server has one built in, which your users' calls use as soon as it is on. Element X and other apps that call through Element Call use their own call service instead, see `Calls:LiveKitServiceUrl` in [Settings](xref:Uniform.Chat.Server.Settings.Article).

## Turning it on

The server must be reachable from the internet on these ports:

| Port | Protocol | Use |
|---|---|---|
| 3478 | UDP and TCP | STUN and TURN, `Turn:Port` |
| 49152–65535 | UDP | Relayed calls, `Turn:MinRelayPort` to `Turn:MaxRelayPort` |
| 5349, if set | TCP | TURN over TLS, `Turn:TlsPort` |

Behind a home router, forward these ports to the server's computer. A smaller relay range is easier to forward and is enough for a small server; each call uses one or two ports:

```bash
./uniform-chat-server --Turn:Enabled=true --Turn:MinRelayPort=50000 --Turn:MaxRelayPort=50099
```

The server then hands out credentials for its own TURN server to every call, with addresses made from the host name of `Server:PublicBaseUrl`. It needs to know the public address that relayed calls are sent to, `Turn:ExternalAddress`. When not set, it is looked up from that host name, which is right when the name points at your router. Set it when the name points elsewhere, e.g. at a proxy on another machine:

```bash
./uniform-chat-server --Turn:Enabled=true --Turn:ExternalAddress=203.0.113.5
```

The log tells where it listens:

```text
TURN server listening on udp 0.0.0.0:3478, tcp 0.0.0.0:3478, relaying from 203.0.113.5 ports 50000-50099
```

When a setting is wrong, the log says so and the homeserver runs on without the TURN server.

## Over TLS

Some networks only let web traffic through. TURN over TLS gets through more of them, especially on port 443 when nothing else uses that port on the address:

```bash
./uniform-chat-server --Turn:Enabled=true --Turn:TlsPort=5349
```

It uses the certificate of `Tls:CertificatePath` and `Tls:KeyPath`, or of `Turn:CertificatePath` and `Turn:KeyPath`. A renewed certificate is used without a restart.

## Safety

Only your users can use the relay: each call gets credentials that expire after `Calls:TurnUserLifetime`. The server refuses to relay to private, loopback and link-local addresses, so nobody can reach your local network through it, and limits how many relays a user and the whole server may have. `Turn:AllowedPeers` lets calls reach chosen private addresses anyway, e.g. when the users of a local network call each other through it.

## Another TURN server

To use another TURN server, such as coturn or `uniform-stun-server`, the standalone TURN server of this project, leave `Turn:Enabled` off and set `Calls:TurnUris` and `Calls:TurnSharedSecret`. The secret must be the same on both servers: `static-auth-secret` of coturn, `Turn:SharedSecrets` of `uniform-stun-server`.
