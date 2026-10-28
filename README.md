# Uniform.Matrix

A pure .NET implementation of the [Matrix](https://spec.matrix.org/latest/) chat protocol.

There are chat clients for the terminal, the desktop (Windows and Linux) and Android, and a matrix server. They are built from class libraries that can be used independently.

> [!NOTE]
> This is version 0.1, an early pre-release. Expect changes, and do not rely on it for important conversations yet.

## Terminal client

`Uniform.Matrix.Client.Terminal` is the terminal chat client: rooms, messages, files and image previews, end-to-end encryption with cross-signing, key backup and emoji verification, settings and logs.

<pre>
 File  Room  View  Help
┌Rooms─────────────────┐┌General — Welcome to the general chat────────────┐┌Members (3)──────┐
│ 1  General (2)       ││12:01 &lt;alice&gt; hi all                             ││alice            │
│ 2 @bob               ││12:02 &lt;bob&gt; hello! this is a long message that   ││bob              │
│ 3 +Secret project    ││            wraps under the text                 ││carol            │
│                      ││12:02 -!- carol joined                           ││                 │
│                      ││12:03  * alice waves                             ││                 │
│                      ││12:04 &lt;you&gt; on my way  [sending…]                ││                 │
└──────────────────────┘└─────────────────────────────────────────────────┘└─────────────────┘
 hello everyone_
 F1 Help │ Ctrl+N Rooms │ Ctrl+O Join │ Ctrl+Q Quit │ Connected as @you:matrix.org   bob typing…
</pre>

- User guide: [Getting started](user-docs/Uniform.Matrix.Client.Terminal/articles/getting-started.md), [Interface](user-docs/Uniform.Matrix.Client.Terminal/articles/interface.md), [Settings](user-docs/Uniform.Matrix.Client.Terminal/articles/settings.md)
- Developer guide: [Overview](docs/index.md), [Using the protocol client](docs/Uniform.Matrix/Uniform.Matrix.Protocol.Client/index.md)

## Desktop and Android client

`Uniform.Matrix.Client.Desktop` and `Uniform.Matrix.Client.Android` are the graphical client, built with [Avalonia](https://avaloniaui.net) on the same engine: rooms, formatted messages with replies, reactions and edits, files and image previews, invites and direct chats, and end-to-end encryption with recovery and emoji verification. It is newer than the terminal client and has fewer features.

![The desktop client](user-docs/Uniform.Matrix.Client.Desktop/images/desktop.png)

- User guide: [Getting started](user-docs/Uniform.Matrix.Client.Desktop/articles/getting-started.md), [Rooms](user-docs/Uniform.Matrix.Client.Desktop/articles/rooms.md), [Messages and files](user-docs/Uniform.Matrix.Client.Desktop/articles/messages.md)

## Server

`Uniform.Matrix.Server.Host` is a homeserver with a dashboard in the terminal, or without one as a service: accounts, rooms, sync, profiles, typing and receipts, the room directory, files with thumbnails, and the encryption relay with cross-signing and key backup. Federation with other servers is not there yet.

<pre>
┌Matrix server localhost───────────────────────────────────────────────────────────┐
│ File  Server                                                                     │
│ Listening:  http://localhost:8008                                                │
│ Uptime:     0:12:31                                                              │
│ Requests:   1520 answered, 3 in progress, 2 failed                               │
│ Accounts:   4 users, 3 rooms                                                     │
│                                                                                  │
│ Data:       /home/alice/.local/share/uniform-matrix-server-host                  │
│ Log:        /home/alice/.local/share/uniform-matrix-server-host/logs/2026-10-26… │
│                                                                                  │
│ F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q stops it.    │
└──────────────────────────────────────────────────────────────────────────────────┘
</pre>

- User guide: [Installing](user-docs/Uniform.Matrix.Server.Host/articles/install.md), [Settings](user-docs/Uniform.Matrix.Server.Host/articles/settings.md)
- Developer guide: [Server](docs/Uniform.Matrix/Uniform.Matrix.Server/index.md), [Overview](docs/index.md)

## Libraries

| Package | Contents |
|---|---|
| `Uniform.Matrix.Protocol.Core` | Spec types shared by clients and servers: identifiers, events, API records, canonical JSON |
| `Uniform.Matrix.Protocol.Client` | HTTP client of the Client-Server API |
| `Uniform.Matrix.Protocol.Crypto` | End-to-end encryption in pure .NET: Olm, Megolm (a port of [vodozemac](https://github.com/matrix-org/vodozemac)), cross-signing, secret storage, key backup, SAS |
| `Uniform.Matrix.Protocol.Server` | Homeserver protocol: room versions, event format, signing, authorization rules |
| `Uniform.Matrix.Server` | Homeserver engine on ASP.NET Core and SQLite: `AddMatrixServer`, endpoints, services, storage |
| `Uniform.Matrix.Client` | Client engine with local SQLite storage: sync, outbox, media cache, encryption, room state |
| `Uniform.Matrix.Hosting` | Settings and logging for .NET host applications |
| `Uniform.Matrix.Gui.Terminal` | Terminal.Gui settings window, log window and dialogs |

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Uniform.Matrix.slnx
dotnet test --solution Uniform.Matrix.slnx
dotnet run --project src/Uniform.Matrix.Server.Host
dotnet run --project src/Uniform.Matrix.Client.Terminal -- --Matrix:UserId=@alice:localhost --Matrix:Homeserver=http://localhost:8008
```

See [Building](docs/Uniform.Matrix/building.md), [Testing](docs/Uniform.Matrix/testing.md) and [Documentation](docs/Uniform.Matrix/documentation.md) for the build, the tests and the documentation sites.

## License

[MIT](LICENSE), except `Uniform.Matrix.Protocol.Crypto`, which is `MIT AND Apache-2.0`: its Olm and Megolm code is ported from vodozemac and stays under the [Apache License 2.0](LICENSES/Apache-2.0.txt); see [third-party notices](THIRD-PARTY-NOTICES.md).
