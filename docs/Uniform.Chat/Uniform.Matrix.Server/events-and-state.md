---
title: Events and state
uid: Uniform.Matrix.Server.EventsAndState.Article
---
# Events and state

[`EventSender`](xref:Uniform.Matrix.Server.Rooms.EventSender) turns an [`EventDraft`](xref:Uniform.Matrix.Server.Rooms.EventDraft) into a PDU: it picks the auth events, `prev_events` (the room's forward extremities) and depth, hashes and signs it with the server's key ([`ServerKeys`](xref:Uniform.Matrix.Server.Keys.ServerKeys), in `signing.key`), computes the event ID, checks the authorization rules against the current state, and stores the event, its graph edges and the new state.

Room state is stored as state groups: each state event makes a new group that records only its change against the previous group, with a full snapshot every 50 changes, so reading the state at any event is a short chain. [`StateCache`](xref:Uniform.Matrix.Server.Rooms.StateCache) keeps loaded groups, which never change. `CurrentState` has one row per type and state key for fast reads, with the membership of member events, so a user's rooms are one query.

`ClientFormat` turns a stored PDU into the [client format](https://spec.matrix.org/latest/client-server-api/#room-event-format): no graph fields, `unsigned` with `age`, the transaction ID for the sending device, `redacted_because` and `prev_content`.
