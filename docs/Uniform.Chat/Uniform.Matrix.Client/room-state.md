---
title: Room state
uid: Uniform.Matrix.Client.RoomState.Article
---
# Room state

[RoomStore](xref:Uniform.Matrix.Client.State.RoomStore) turns sync responses into one [Room](xref:Uniform.Matrix.Client.State.Room) per room ID. [Apply](xref:Uniform.Matrix.Client.State.RoomStore.Apply*) returns [SyncChanges](xref:Uniform.Matrix.Client.State.SyncChanges) so a UI only redraws what changed. A room keeps:

- current state, with display name computed per [spec](https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-room): name, alias, then member names such as "Alice and Bob" or "Empty Room (was Alice)";
- members, with display names [disambiguated](https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-user) as "Bob (@bob:server)" while shared;
- the most recent [MaxTimelineLength](xref:Uniform.Matrix.Client.State.RoomStore.MaxTimelineLength) events, with redactions applied. A limited timeline clears older events, since they no longer connect; [BackfillAsync](xref:Uniform.Matrix.Client.State.RoomStore.BackfillAsync*) loads history back;
- typing users, read receipts, unread counts, tags, fully read marker and the direct-message user from `m.direct`.

Servers may lazy-load members, sending only those needed for the timeline. [LoadMembersAsync](xref:Uniform.Matrix.Client.State.RoomStore.LoadMembersAsync*) fetches the full list, e.g. for a nick list. The store is not thread-safe; apply syncs and read rooms on one thread.

## Stored history

The room state keeps only a recent timeline; the whole history that the client has seen is in the database, read a page of lines at a time. Each stored event has a line in display order. An event shown on its own gets a line from its time: its `origin_server_ts` × [LinesPerMillisecond](xref:Uniform.Matrix.Client.Storage.ClientStore.LinesPerMillisecond), plus a count of earlier events of the same millisecond. Edits, reactions and redactions take the line of the event they change, so a message always loads with all of its changes, however much later they came. A change that arrives before its target, e.g. a reaction to a message older than the stored history, waits without a line until that message is stored.

[GetRoomLinesAsync](xref:Uniform.Matrix.Client.Storage.ClientStore.GetRoomLinesAsync*) returns the newest lines before a given line; to show a room and scroll back:

```csharp
RoomLines newest = await store.GetRoomLinesAsync(roomId, 250);
RoomLines older = await store.GetRoomLinesAsync(roomId, 100, newest.OldestLine);
```

Lines from time keep their order when older history fills a gap after a limited sync.
