---
title: Uniform.Matrix.Protocol.Server
uid: Uniform.Matrix.Protocol.Server.Article
---
# Uniform.Matrix.Protocol.Server

Pure protocol code of a homeserver, without storage: [room versions](https://spec.matrix.org/latest/rooms/) 10, 11 and 12 ([`RoomVersion`](xref:Uniform.Matrix.Protocol.Server.RoomVersion)), the event format, content hashes, reference-hash event IDs, signing and redaction ([`EventFormat`](xref:Uniform.Matrix.Protocol.Server.EventFormat)), the auth events selection and the [authorization rules](https://spec.matrix.org/latest/rooms/v12/#authorization-rules) ([`AuthEvents`](xref:Uniform.Matrix.Protocol.Server.AuthEvents), [`AuthRules`](xref:Uniform.Matrix.Protocol.Server.AuthRules)), power levels, push rule evaluation ([`PushRules`](xref:Uniform.Matrix.Protocol.Server.PushRules)), signed requests between servers ([`FederationAuth`](xref:Uniform.Matrix.Protocol.Server.FederationAuth)), server names ([`ServerNames`](xref:Uniform.Matrix.Protocol.Server.ServerNames)), room server ACLs ([`ServerAcl`](xref:Uniform.Matrix.Protocol.Server.ServerAcl)) and DNS SRV messages ([`DnsSrv`](xref:Uniform.Matrix.Protocol.Server.DnsSrv)).

It is used by [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Article), and builds on [`Uniform.Matrix.Protocol.Crypto`](xref:Uniform.Matrix.Protocol.Crypto.Article) for Ed25519 signatures.

## State resolution

When a room's history forks, as when two servers send events at the same time, [`StateResolution.Resolve`](xref:Uniform.Matrix.Protocol.Server.StateResolution.Resolve*) merges the states of the branches as the spec's [state resolution v2](https://spec.matrix.org/latest/rooms/v2/#state-resolution) says, and with the v2.1 changes of [room version 12](https://spec.matrix.org/latest/rooms/v12/#state-resolution). Keys every branch agrees on are kept. Conflicted power events (power levels, join rules, kicks and bans) are applied first, strongest sender first, each checked with [`AuthRules`](xref:Uniform.Matrix.Protocol.Server.AuthRules) against what is resolved so far; the other conflicted events follow, ordered by the power levels they were sent under.

The caller passes the events of the states and their auth chains:

```csharp
Dictionary<(string Type, string StateKey), string> merged = StateResolution.Resolve(RoomVersion.V12, [stateOfBranchA, stateOfBranchB], eventsById);
```

[`AuthRules.CheckAuthChains`](xref:Uniform.Matrix.Protocol.Server.AuthRules.CheckAuthChains*) checks received state, such as a joined room's, against each event's own auth events.

