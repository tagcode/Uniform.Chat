# Uniform.Matrix.Protocol.Server

Protocol building blocks of a Matrix homeserver, without storage: [room versions](https://spec.matrix.org/latest/rooms/) 10 and 11, the event (PDU) format, content hashes, reference-hash event IDs, signing, redaction and the authorization rules.

```bash
dotnet add package Uniform.Matrix.Protocol.Server
```

## Events

```csharp
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Server;

RoomVersion version = RoomVersion.V11;

// Hash and sign with the server's Ed25519 key, then derive the event ID
EventFormat.HashAndSign(pdu, version, "example.org", "ed25519:a_key", signingSeed);
string eventId = EventFormat.ComputeEventId(pdu, version);

// Check a received event
bool intact = EventFormat.HasValidContentHash(pdu);
bool signed = EventFormat.HasValidSignature(pdu, version, "example.org", "ed25519:a_key", publicKey);

// Redact as the room version prescribes
JsonObject redacted = EventFormat.Redact(pdu, version);
```

## Authorization

`AuthEvents.Select` names the state an event is authorized against, and `AuthRules.Check` applies the rules of the room version: it returns null when the event is allowed, or the reason it is not.

```csharp
string? rejection = AuthRules.Check(pdu, version, authState);
```

`PowerLevels` reads `m.room.power_levels` with the defaults of the spec.

The homeserver that uses these is [Uniform.Matrix.Server](https://www.nuget.org/packages/Uniform.Matrix.Server).

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Chat), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/Uniform.Matrix.Protocol.Server/index.md)
- [Building](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/building.md) and [testing](https://github.com/tagcode/Uniform.Chat/blob/main/docs/Uniform.Chat/testing.md)
