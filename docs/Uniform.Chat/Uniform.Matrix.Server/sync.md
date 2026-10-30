---
title: Sync and the stream
uid: Uniform.Matrix.Server.Sync.Article
---
# Sync and the stream

[`ServerStream`](xref:Uniform.Matrix.Server.Sync.ServerStream) is one counter for every change a sync must notice: events, account data, receipts, typing, device list changes and to-device messages. `WriteAsync(position => …)` runs a change with the next position, one at a time, so positions commit in order, and wakes waiting syncs. Sync tokens are `s` and a position; `/messages` and `prev_batch` use the same tokens. Changes that are not stored, such as typing, still take a position, and the counter itself is stored, so positions are never reused after a restart.

[`SyncService`](xref:Uniform.Matrix.Server.Sync.SyncService) builds a response for the positions after the token: joined rooms with their timeline, state and summary, invites with stripped state, left rooms, account data, receipts and typing, to-device messages, device list changes and key counts. Incremental syncs long-poll with [`ServerStream.WaitPastAsync`](xref:Uniform.Matrix.Server.Sync.ServerStream.WaitPastAsync*) until something reaches the user. `Visibility` applies [history visibility](https://spec.matrix.org/latest/client-server-api/#history-visibility) to every event a user is shown, and [`SyncFilter`](xref:Uniform.Matrix.Server.Sync.SyncFilter) the parts of [filters](https://spec.matrix.org/latest/client-server-api/#filtering) the server applies.
