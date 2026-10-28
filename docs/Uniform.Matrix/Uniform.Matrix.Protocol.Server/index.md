---
title: Uniform.Matrix.Protocol.Server
uid: Uniform.Matrix.Protocol.Server.Article
---
# Uniform.Matrix.Protocol.Server

Pure protocol code of a homeserver, without storage: [room versions](https://spec.matrix.org/latest/rooms/) 10, 11 and 12 ([`RoomVersion`](xref:Uniform.Matrix.Protocol.Server.RoomVersion)), the event format, content hashes, reference-hash event IDs, signing and redaction ([`EventFormat`](xref:Uniform.Matrix.Protocol.Server.EventFormat)), the auth events selection and the [authorization rules](https://spec.matrix.org/latest/rooms/v12/#authorization-rules) ([`AuthEvents`](xref:Uniform.Matrix.Protocol.Server.AuthEvents), [`AuthRules`](xref:Uniform.Matrix.Protocol.Server.AuthRules)), power levels, push rule evaluation ([`PushRules`](xref:Uniform.Matrix.Protocol.Server.PushRules)), signed requests between servers ([`FederationAuth`](xref:Uniform.Matrix.Protocol.Server.FederationAuth)) and server names ([`ServerNames`](xref:Uniform.Matrix.Protocol.Server.ServerNames)).

It is used by [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Article), and builds on [`Uniform.Matrix.Protocol.Crypto`](xref:Uniform.Matrix.Protocol.Crypto.Article) for Ed25519 signatures.
