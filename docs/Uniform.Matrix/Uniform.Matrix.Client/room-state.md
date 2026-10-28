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
