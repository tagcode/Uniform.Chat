# Matrix .NET

.NET 10 libraries and applications for the [Matrix](https://spec.matrix.org/latest/) chat protocol.

| Project | Kind | Status |
|---|---|---|
| `Uniform.Matrix.Protocol.Core` | Spec types shared by clients and servers: IDs, events, API records, canonical JSON | Client-Server API types |
| `Uniform.Matrix.Protocol.Client` | HTTP client of the [Client-Server API](https://spec.matrix.org/latest/client-server-api/) | Login, sync, rooms, messages, media, devices, keys, backup |
| `Uniform.Matrix.Protocol.Crypto` | End-to-end encryption in pure .NET | Olm, Megolm, cross-signing, secret storage, backup, key requests, SAS |
| `Uniform.Matrix.Hosting` | Settings and logging for .NET host applications | Settings file layers, option catalog, session logs |
| `Uniform.Matrix.Gui.Terminal` | Terminal.Gui components | Settings window, log window, dialogs |
| `Uniform.Matrix.Client` | Headless client with local SQLite storage | Session, sync, outbox, media cache, encryption, room state |
| `Durum.Matrix.Client.Terminal` | Terminal client app (Terminal.Gui v2) | Rooms, messages, files, encryption, settings and logs |
| `Uniform.Matrix.Protocol.Server` | Homeserver protocol: room versions, event format, signing, auth rules | Room versions 10 and 11 |
| `Uniform.Matrix.Server` | Homeserver engine on ASP.NET Core and SQLite | Accounts, rooms, sync, media, encryption relay, directory |
| `Durum.Matrix.Server.Host` | Homeserver app, with or without the dashboard | Dashboard, users and rooms admin, settings and logs |

- [Architecture](articles/architecture.md): projects and how they fit together.
- [Server](articles/server.md): how the homeserver handles requests, stores events and syncs.
- [Using the protocol client](articles/using-client.md): log in and call the API from your own code with `Uniform.Matrix.Protocol.Client`.
- [Building and testing](articles/building-and-testing.md): build, test and generate these docs.
- [API reference](api/index.md): generated from XML doc comments.
