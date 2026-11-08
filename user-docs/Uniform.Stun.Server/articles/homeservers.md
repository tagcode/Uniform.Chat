---
title: Homeservers
uid: Uniform.Stun.Server.Homeservers.Article
---
# Homeservers

A Matrix homeserver hands its users time-limited TURN credentials, made with a secret it shares with the TURN server. Give both the same secret: `Turn:SharedSecrets` here, separated by commas when there are several, e.g. while replacing one.

## Synapse

In `homeserver.yaml`:

```yaml
turn_uris:
  - "turn:turn.example.org:3478?transport=udp"
  - "turn:turn.example.org:3478?transport=tcp"
turn_shared_secret: "a-long-random-secret"
turn_user_lifetime: 86400000
```

## Uniform Chat Server

Uniform Chat Server has this server built in, see its guide. To use a separate one instead, set `Calls:TurnUris` and `Calls:TurnSharedSecret`:

```json
{
  "Calls": {
    "TurnUris": "turn:turn.example.org:3478?transport=udp,turn:turn.example.org:3478?transport=tcp",
    "TurnSharedSecret": "a-long-random-secret"
  }
}
```

## Without a homeserver

Fixed users work for other software, as `name:password` pairs in `Turn:Users`. Anyone with such a password can use the relay until you remove it, so prefer time-limited credentials.
