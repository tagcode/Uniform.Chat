# Local storage

Each account has its own directory, `<data directory>/<localpart>_<server>/`, holding `client.db` and `media/`. Tables:

| Table | Contents |
|---|---|
| `Accounts` | Homeserver, device ID, access token and the last `next_batch` |
| `Events` | Every received timeline event as JSON, indexed by room and time; redactions strip stored content |
| `Outbox` | Messages written locally, with state Pending, Sent or Failed |
| `Media` | Downloaded files, stored under `media/` |

Events and the sync position are saved in one transaction before the in-memory state changes, so a crash can at worst re-apply a sync; events are deduplicated by ID. On start, the first sync asks for full state from the stored position, which rebuilds room names and members in memory without losing messages.
