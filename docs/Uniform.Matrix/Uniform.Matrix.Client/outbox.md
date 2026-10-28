# Outbox

Messages are written to the `Outbox` table first and then sent in order. Each message gets its transaction ID when queued and keeps it across retries and restarts, so the server never stores a resent message twice. A transient failure (network, 5xx, rate limit) keeps the message and pauses the queue to keep order; a permanent rejection (e.g. not in the room) marks it Failed and moves on. If the app crashes after the server accepted a message but before it was marked Sent, its echo in sync carries the transaction ID and confirms it.

SQLite is the default, through EF Core. No EF Core provider stores YAML, so a YAML store would be a separate implementation of `ClientStore`.
