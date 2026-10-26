# Uniform.Matrix.Protocol.Core

Types shared by Matrix clients and servers: identifiers, events, request and response records of the Client-Server API, Matrix errors, and canonical JSON for signing.

```bash
dotnet add package Uniform.Matrix.Protocol.Core
```

## Identifiers

```csharp
using Uniform.Matrix.Protocol.Core;

UserId userId = UserId.Parse("@alice:matrix.org");
Console.WriteLine(userId.Localpart);  // alice
Console.WriteLine(userId.ServerName); // matrix.org

MxcUri avatar = MxcUri.Parse("mxc://matrix.org/abc123");
Console.WriteLine(avatar.MediaId);    // abc123
```

## API records and events

Requests, responses and event contents are records in snake_case JSON through `MatrixJson.Options`:

```csharp
SyncResponse sync = JsonSerializer.Deserialize<SyncResponse>(json, MatrixJson.Options)!;
foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join)
{
    foreach (ClientEvent clientEvent in room.Timeline.Events)
    {
        if (clientEvent.TryGetContent(out MessageContent? message)) Console.WriteLine($"{clientEvent.Sender}: {message.Body}");
    }
}
```

Constants for event types, membership, error codes and algorithms are in `EventTypes`, `Memberships`, `ErrorCodes` and `EncryptionAlgorithms`. A homeserver's error responses become `MatrixException`, with the HTTP status and the `errcode`.

## Canonical JSON

[Canonical JSON](https://spec.matrix.org/latest/appendices/#canonical-json) is what Matrix signs and hashes: sorted keys, no whitespace.

```csharp
string canonical = CanonicalJson.Serialize(new { b = 1, a = "x" }); // {"a":"x","b":1}
```

`CanonicalJson.SigningForm` drops `signatures` and `unsigned` before signing, and `AddSignature` and `GetSignature` read and write the `signatures` object.

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [Architecture](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/architecture.md)
- [Building and testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/building-and-testing.md)
