# Uniform.Matrix.Protocol.Server

Pure protocol code of a homeserver, without storage: [room versions](https://spec.matrix.org/latest/rooms/) 10, 11 and 12 (`RoomVersion`), the event format, content hashes, reference-hash event IDs, signing and redaction (`EventFormat`), the auth events selection and the [authorization rules](https://spec.matrix.org/latest/rooms/v12/#authorization-rules) (`AuthEvents`, `AuthRules`), power levels, push rule evaluation (`PushRules`), signed requests between servers (`FederationAuth`) and server names (`ServerNames`).

It is used by [Uniform.Matrix.Server](../Uniform.Matrix.Server/index.md), and builds on `Uniform.Matrix.Protocol.Crypto` for Ed25519 signatures.
