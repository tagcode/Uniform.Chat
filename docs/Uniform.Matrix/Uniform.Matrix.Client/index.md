---
title: Uniform.Matrix.Client
uid: Uniform.Matrix.Client.Article
---
# Uniform.Matrix.Client

The headless client. `builder.AddMatrixClient()` registers the Matrix and storage options, session logging under the data directory, storage, encryption, sync and the outbox; the application adds its own [`ICredentialPrompt`](xref:Uniform.Matrix.Client.Services.ICredentialPrompt) for the first login. The data directory defaults to `~/.local/share/<app>` by the host's application name.

```text
MatrixWorker (hosted service)
  ├─ ClientStore.MigrateAsync      create or upgrade the SQLite schema
  ├─ ClientSession.ConnectAsync    resume stored token, or log in and store it
  ├─ sync                          full-state sync from stored position, then SyncLoopAsync
  │    └─ SyncProcessor            save events + position, apply to RoomStore, publish SyncChanges
  └─ OutboxSender.RunAsync         send queued messages in order
a frontend reads SyncChanges and queues messages via OutboxSender
```

The client stores encryption state in its SQLite database ([`EfCryptoStore`](xref:Uniform.Matrix.Client.Storage.EfCryptoStore)), with the 32-byte pickle key in `crypto.key` (mode 0600). [`SyncProcessor`](xref:Uniform.Matrix.Client.Services.SyncProcessor) decrypts each sync before storing it and re-decrypts stored events when late keys arrive; [`OutboxSender`](xref:Uniform.Matrix.Client.Services.OutboxSender) encrypts for encrypted rooms after loading the full member list, and fails a message rather than sending it in the clear when encryption is unavailable. Files for encrypted rooms are encrypted and uploaded, then sent through the outbox; [`MediaCache`](xref:Uniform.Matrix.Client.Services.MediaCache) decrypts them after the hash check, and the server is not asked for thumbnails of ciphertext. [`CryptoService`](xref:Uniform.Matrix.Client.Services.CryptoService) backs up new room keys after each sync, and `MainWindow.Security.cs` holds the recovery and verification dialogs.

## Not yet covered

- Registration, refresh tokens, push rules, spaces and threads.
