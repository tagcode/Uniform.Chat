---
title: Presence
uid: Uniform.Matrix.Server.Presence.Article
---
# Presence

[`PresenceService`](xref:Uniform.Matrix.Server.Accounts.PresenceService) keeps presence in memory, like typing: each change takes a [`ServerStream`](xref:Uniform.Matrix.Server.Sync.ServerStream) position, and sync sends `m.presence` for the user and everyone sharing a joined room with them. A sync marks the user online, or idle with `set_presence=unavailable`; `set_presence=offline` changes nothing. A state chosen with the presence API, such as `unavailable` or `offline` to appear away, is not changed by syncing. A timer takes users offline once they have not synced for [`PresenceService.OfflineAfter`](xref:Uniform.Matrix.Server.Accounts.PresenceService.OfflineAfter) (2 minutes), which wakes the syncs of the people who see them. Presence is lost on restart.

The server never sees private keys. It checks that uploaded device keys, cross-signing keys and signatures are signed as the spec requires, hands out each one-time key once, and stores encrypted backups as they come.
