# Uniform.Matrix.Protocol.Core

The types both sides of the protocol use: identifiers, events, the request and response records of the APIs, canonical JSON and error codes.

Models are records with `init` properties.

- **Responses** mark spec-required fields `required`, so a response missing one fails during deserialization rather than surfacing as a null later. Collections default to empty, so `sync.Rooms.Join` never needs a null check.
- **Events** derive from [MatrixEvent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent), which keeps `content` as raw JSON. [TryGetContent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent.TryGetContent*) reads it as a typed content record and returns false instead of throwing when another client sent malformed content.
- **Content records** such as [MessageContent](xref:Uniform.Matrix.Protocol.Core.MessageContent) have no required members for the same reason.
- **Constants** for event types, message types, memberships and error codes live in static classes such as [EventTypes](xref:Uniform.Matrix.Protocol.Core.EventTypes) and [ErrorCodes](xref:Uniform.Matrix.Protocol.Core.ErrorCodes).

JSON names are mapped to snake_case by [MatrixJson.Options](xref:Uniform.Matrix.Protocol.Core.MatrixJson.Options); `[JsonPropertyName]` is only used for dotted keys such as `m.relates_to` and names that are not snake_case, such as `displayname` and `msgtype`.
