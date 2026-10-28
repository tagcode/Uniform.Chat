# Services

| Folder | Services |
|---|---|
| `Accounts/` | `AccountService` (register, log in, devices, passwords), `UserInteractiveAuth`, `AccountDataService` (account data and tags), `PushRuleService` (push rules and unread counts), `PusherService` and `PushNotifier` (pushers and push gateways), `ProfileService`, `PresenceService`, `AdminService` (the dashboard's user and room windows) |
| `Rooms/` | `RoomService` (create, join, invite, leave, state, redact, read), `EventSender`, `ReceiptService`, `TypingService` (in memory), `DirectoryService` (aliases, public rooms, user search) |
| `Sync/` | `SyncService`, `ServerStream`, `SyncFilter`, `Visibility` |
| `Encryption/` | `DeviceKeyService` (device, one-time, fallback and cross-signing keys, signatures), `ToDeviceService` (to-device messages and device lists), `KeyBackupService` |
| `Media/` | `UrlPreviewService` (page previews), `MediaService` (files under `media/` in the data directory; media reserved with `/media/v1/create` is a row with an empty content type until its file arrives, and downloads wait for it up to `timeout_ms`), `Thumbnailer` |
