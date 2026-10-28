---
title: Uniform.Matrix.Protocol.Core
uid: Uniform.Matrix.Protocol.Core.Article
---
# Uniform.Matrix.Protocol.Core

The types both sides of the protocol use: identifiers, events, the request and response records of the APIs, canonical JSON and error codes.

Models are records with `init` properties.

- **Responses** mark spec-required fields `required`, so a response missing one fails during deserialization rather than surfacing as a null later. Collections default to empty, so `sync.Rooms.Join` never needs a null check.
- **Events** derive from [MatrixEvent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent), which keeps `content` as raw JSON. [TryGetContent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent.TryGetContent*) reads it as a typed content record and returns false instead of throwing when another client sent malformed content.
- **Content records** such as [MessageContent](xref:Uniform.Matrix.Protocol.Core.MessageContent) have no required members for the same reason.
- **Constants** for event types, message types, memberships and error codes live in static classes such as [EventTypes](xref:Uniform.Matrix.Protocol.Core.EventTypes) and [ErrorCodes](xref:Uniform.Matrix.Protocol.Core.ErrorCodes).

JSON names are mapped to snake_case by [MatrixJson.Options](xref:Uniform.Matrix.Protocol.Core.MatrixJson.Options); `[JsonPropertyName]` is only used for dotted keys such as `m.relates_to` and names that are not snake_case, such as `displayname` and `msgtype`.

## Links to rooms and users

[MatrixLink](xref:Uniform.Matrix.Protocol.Core.MatrixLink) parses [matrix.to](https://spec.matrix.org/latest/appendices/#matrixto-navigation) links, [`matrix:` URIs](https://spec.matrix.org/latest/appendices/#matrix-uri-scheme) and bare identifiers into the target, an optional event and the `via` servers to join through. [FindAll](xref:Uniform.Matrix.Protocol.Core.MatrixLink.FindAll*) finds the links in a message body or its HTML, for example to offer joining a linked room.

```csharp
if (MatrixLink.TryParse("https://matrix.to/#/!abc:example.org?via=example.org", out MatrixLink? link))
    await client.JoinRoomAsync(link.Target, link.Via);
```
