# Uniform.Matrix.Client

A Matrix client engine for the .NET generic host: login and session storage, sync with local SQLite storage, room state, an outbox that keeps messages until the server has them, a media cache, and end-to-end encryption with cross-signing, key backup and verification. It has no user interface; the [Uniform.Matrix terminal client](https://github.com/tagcode/Uniform.Matrix) is one built on it.

```bash
dotnet add package Uniform.Matrix.Client
```

## Host it

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Services;
using Uniform.Matrix.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSettings();
builder.AddMatrixClient();
builder.Services.AddSingleton<ICredentialPrompt, MyPasswordPrompt>();

using IHost host = builder.Build();
await host.StartAsync();
```

Give the user ID with `--Matrix:UserId=@alice:matrix.org`, and the address with `--Matrix:Homeserver=` when the server has no discovery file. The first start asks `ICredentialPrompt` for the password; later starts resume the stored session.

## Use it

```csharp
// Messages are stored, then sent in order, also after a restart
OutboxSender outbox = host.Services.GetRequiredService<OutboxSender>();
await outbox.EnqueueTextAsync(roomId, "Hello");

// Each processed sync, with the rooms it changed
SyncProcessor sync = host.Services.GetRequiredService<SyncProcessor>();
await foreach (SyncChanges changes in sync.Changes.ReadAllAsync())
{
    foreach (Room room in changes.Joined) Console.WriteLine(room.DisplayName);
}
```

`RoomStore` and `Room` hold the state built from sync: display names computed as the spec says, members, timeline and unread counts. Read them under the processor's lock with `sync.Read(store => …)`. `CryptoService` exposes the encryption status, recovery and verification.

Each account has its own folder with `client.db` and `media/` under the data directory, and each session writes its own log file.

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Client/index.md)
- [Building](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/building.md) and [testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/testing.md)
