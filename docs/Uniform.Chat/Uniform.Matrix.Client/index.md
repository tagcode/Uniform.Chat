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

[`OutboxSender`](xref:Uniform.Matrix.Client.Services.OutboxSender) sends queued events in order, each with the transaction ID fixed when it was queued. [`EnqueueFileAsync`](xref:Uniform.Matrix.Client.Services.OutboxSender.EnqueueFileAsync*) copies a file into the account's `outbox` folder, so it can be queued without network; the sender uploads it, encrypted where the room is, records the content that refers to the upload, deletes the copy, and then sends the message. [`CancelAsync`](xref:Uniform.Matrix.Client.Services.OutboxSender.CancelAsync*) and [`EditAsync`](xref:Uniform.Matrix.Client.Services.OutboxSender.EditAsync*) change a message only while it is unsent; they wait for a send under way, and the sender reads each message again just before sending it, so a cancelled message is never sent and an edited one goes as edited.

The client stores encryption state in its SQLite database ([`EfCryptoStore`](xref:Uniform.Matrix.Client.Storage.EfCryptoStore)), with the 32-byte pickle key in `crypto.key` (mode 0600). [`SyncProcessor`](xref:Uniform.Matrix.Client.Services.SyncProcessor) decrypts each sync before storing it and re-decrypts stored events when late keys arrive; [`OutboxSender`](xref:Uniform.Matrix.Client.Services.OutboxSender) encrypts for encrypted rooms after loading the full member list, and fails a message rather than sending it in the clear when encryption is unavailable. Files for encrypted rooms are encrypted and uploaded, then sent through the outbox; [`MediaCache`](xref:Uniform.Matrix.Client.Services.MediaCache) decrypts them after the hash check, and the server is not asked for thumbnails of ciphertext. [`CryptoService`](xref:Uniform.Matrix.Client.Services.CryptoService) backs up new room keys after each sync, and `MainWindow.Security.cs` holds the recovery and verification dialogs.

## Not yet covered

- Registration, refresh tokens, push rules, spaces and threads.
