---
title: Services
uid: Uniform.Matrix.Server.Services.Article
---
# Services

| Folder | Services |
|---|---|
| `Accounts/` | [`AccountService`](xref:Uniform.Matrix.Server.Accounts.AccountService) (register, log in, devices, passwords), [`UserInteractiveAuth`](xref:Uniform.Matrix.Server.Accounts.UserInteractiveAuth), [`AccountDataService`](xref:Uniform.Matrix.Server.Accounts.AccountDataService) (account data and tags), [`PushRuleService`](xref:Uniform.Matrix.Server.Accounts.PushRuleService) (push rules and unread counts), [`PusherService`](xref:Uniform.Matrix.Server.Accounts.PusherService) and [`PushNotifier`](xref:Uniform.Matrix.Server.Accounts.PushNotifier) (pushers and push gateways), [`ProfileService`](xref:Uniform.Matrix.Server.Accounts.ProfileService), [`PresenceService`](xref:Uniform.Matrix.Server.Accounts.PresenceService), [`AdminService`](xref:Uniform.Matrix.Server.Accounts.AdminService) (the dashboard's user and room windows) |
| `Rooms/` | [`RoomService`](xref:Uniform.Matrix.Server.Rooms.RoomService) (create, join, invite, leave, state, redact, read), [`EventSender`](xref:Uniform.Matrix.Server.Rooms.EventSender), [`ReceiptService`](xref:Uniform.Matrix.Server.Rooms.ReceiptService), [`TypingService`](xref:Uniform.Matrix.Server.Rooms.TypingService) (in memory), [`DirectoryService`](xref:Uniform.Matrix.Server.Rooms.DirectoryService) (aliases, public rooms, user search) |
| `Sync/` | [`SyncService`](xref:Uniform.Matrix.Server.Sync.SyncService), [`ServerStream`](xref:Uniform.Matrix.Server.Sync.ServerStream), [`SyncFilter`](xref:Uniform.Matrix.Server.Sync.SyncFilter), `Visibility` |
| `Encryption/` | [`DeviceKeyService`](xref:Uniform.Matrix.Server.Encryption.DeviceKeyService) (device, one-time, fallback and cross-signing keys, signatures), [`ToDeviceService`](xref:Uniform.Matrix.Server.Encryption.ToDeviceService) (to-device messages and device lists), [`KeyBackupService`](xref:Uniform.Matrix.Server.Encryption.KeyBackupService) |
| `Media/` | [`UrlPreviewService`](xref:Uniform.Matrix.Server.Media.UrlPreviewService) (page previews), [`MediaService`](xref:Uniform.Matrix.Server.Media.MediaService) (files under `media/` in the data directory; media reserved with `/media/v1/create` is a row with an empty content type until its file arrives, and downloads wait for it up to `timeout_ms`), [`Thumbnailer`](xref:Uniform.Matrix.Server.Media.Thumbnailer) |
