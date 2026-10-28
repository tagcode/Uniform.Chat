---
title: Uniform.Matrix
uid: Uniform.Matrix.Home.Article
---
# Uniform.Matrix

.NET 10 libraries and applications for the [Matrix](https://spec.matrix.org/latest/) chat protocol.

| Project | Kind | Status |
|---|---|---|
| [`Uniform.Matrix.Protocol.Core`](xref:Uniform.Matrix.Protocol.Core.Article) | Spec types shared by clients and servers: IDs, events, API records, canonical JSON | Client-Server API types |
| [`Uniform.Matrix.Protocol.Client`](xref:Uniform.Matrix.Protocol.Client.Article) | HTTP client of the [Client-Server API](https://spec.matrix.org/latest/client-server-api/) | Login, sync, rooms, messages, media, devices, keys, backup |
| [`Uniform.Matrix.Protocol.Crypto`](xref:Uniform.Matrix.Protocol.Crypto.Article) | End-to-end encryption in pure .NET | Olm, Megolm, cross-signing, secret storage, backup, key requests, SAS |
| [`Uniform.Matrix.Hosting`](xref:Uniform.Matrix.Hosting.Article) | Settings and logging for .NET host applications | Settings file layers, option catalog, session logs |
| [`Uniform.Matrix.Gui.Terminal`](xref:Uniform.Matrix.Gui.Terminal.Article) | Terminal.Gui components | Settings window, log window, dialogs |
| [`Uniform.Matrix.Client`](xref:Uniform.Matrix.Client.Article) | Headless client with local SQLite storage | Session, sync, outbox, media cache, encryption, room state |
| [`Uniform.Matrix.Client.Terminal`](xref:Uniform.Matrix.Client.Terminal.Article) | Terminal client app (Terminal.Gui v2) | Rooms, messages, files, encryption, settings and logs |
| [`Uniform.Matrix.Client.Avalonia.Core`](xref:Uniform.Matrix.Client.Avalonia.Core.Article) | Graphical app shared by desktop and Android (Avalonia) | Rooms, messages, files, encryption, verification, notifications on Linux |
| [`Uniform.Matrix.Client.Desktop`](xref:Uniform.Matrix.Client.Desktop.Article) | Graphical app for Windows and Linux | Entry point, application menu entry |
| [`Uniform.Matrix.Client.Android`](xref:Uniform.Matrix.Client.Android.Article) | Graphical app for Android | Entry point; no store release yet |
| [`Uniform.Matrix.Protocol.Server`](xref:Uniform.Matrix.Protocol.Server.Article) | Homeserver protocol: room versions, event format, signing, auth rules | Room versions 10, 11 and 12 |
| [`Uniform.Matrix.Server`](xref:Uniform.Matrix.Server.Article) | Homeserver engine on ASP.NET Core and SQLite | Accounts, rooms, sync and sliding sync, media, encryption relay, directory, push, calls, federation |
| [`Uniform.Matrix.Server.Host`](xref:Uniform.Matrix.Server.Host.Article) | Homeserver app, with or without the dashboard | Dashboard, users and rooms admin, settings and logs |

- [Building](xref:Uniform.Matrix.Building.Article): build the solution and the apps, and make releases.
- [Testing](xref:Uniform.Matrix.Testing.Article): the test projects, live tests and the native test oracle.
- [Documentation](xref:Uniform.Matrix.Documentation.Article): these sites and the user guides.
- [API reference](api/index.md): generated from XML doc comments.

# Folder structure

```text
src/
  Uniform.Matrix.Protocol.Core/          Shared spec types, no dependencies
    Api/                                 Request, response, event and content records; constants
    UserId.cs, MxcUri.cs, MatrixLink.cs  Identifiers and matrix.to links
    CanonicalJson.cs                     Canonical JSON and signatures
    MatrixJson.cs                        Shared JSON settings
    MatrixException.cs                   Matrix error responses
  Uniform.Matrix.Protocol.Client/        HTTP client of the Client-Server API
    MatrixClient*.cs                     Request pipeline and one partial file per spec area
    MatrixSession.cs                     Storable login session
    HomeserverDiscovery.cs               .well-known lookup
    UserInteractiveAuthException.cs      Password challenges
  Uniform.Matrix.Protocol.Crypto/        End-to-end encryption in pure .NET: Olm, Megolm and OlmMachine
    OlmAccount.cs                        Identity, one-time and fallback keys; inbound and outbound sessions
    OlmSession.cs                        Olm double ratchet and message formats
    Megolm.cs                            Megolm ratchet, sessions, session key formats and messages
    OlmKeys.cs                           Ed25519 keys, backup encryption (PkEncryption) and SAS
    OlmCipher.cs                         HKDF, AES-256-CBC and HMAC of Olm, Megolm and pickles
    OlmPrimitives.cs                     X25519 and Ed25519 through BouncyCastle, protobuf, pickle JSON
    Primitives.cs                        AES-CTR, encrypted attachments, secret storage crypto, recovery keys
    EncryptedAttachments.cs              Message content of encrypted files
    SecretStorage.cs                     Secret storage (4S) in account data
    OlmMachine*.cs                       Keys, device lists, Olm, Megolm, cross-signing, backup, key requests, SAS
    Storage.cs                           ICryptoStore, InMemoryCryptoStore, IKeyTransport
  Uniform.Matrix.Hosting/                Settings layers, option catalog and editor, session logging; any .NET host
  Uniform.Matrix.Gui.Terminal/           Terminal.Gui components: settings window, log window, dialogs, text wrapping
  Uniform.Matrix.Client/                 Headless client: AddMatrixClient, services, storage, room state
    Configuration/                       Matrix and storage options, per-account paths
    Services/                            Session, sync, outbox, media cache, encryption, worker
    Storage/                             EF Core SQLite: session, events, outbox, media, encryption state
    Migrations/                          EF Core migrations
    State/                               RoomStore and Room: client-side room state built from sync
    Formatting/                          Markdown to HTML for sending, HTML to text and event descriptions for display
    MatrixKeyTransport.cs,               EncryptedMedia.cs  Encryption over a MatrixClient
  Uniform.Matrix.Client.Terminal/        Terminal client app
    Configuration/                       Display options
    Ui/                                  Main window, message log, image previews, client dialogs
  Uniform.Matrix.Client.Avalonia.Core/   Avalonia app shared by desktop and Android: views, view models, services
    Services/                            One engine host per sign-in, remembered account, UI dispatcher
    ViewModels/                          Login, chat, room list items, message list
    Views/                               Windows and views in XAML, rich text, styles
  Uniform.Matrix.Client.Desktop/         Windows and Linux entry point, Linux menu entry installer
  Uniform.Matrix.Client.Android/         Android entry point, manifest and resources
  Uniform.Matrix.Protocol.Server/        Room versions, event format, hashing, signing, redaction, auth rules
  Uniform.Matrix.Server/                 Homeserver engine: AddMatrixServer, endpoints, services, storage
  Uniform.Matrix.Server.Host/            Server app, with or without the dashboard
tests/
  Uniform.Matrix.Protocol.Tests/         API and canonical JSON unit tests, live tests against the test server
  Uniform.Matrix.Protocol.Crypto.Tests/  Olm, Megolm, oracle, protocol and live encryption tests
  Uniform.Matrix.Hosting.Tests/          Settings and log file tests
  Uniform.Matrix.Client.Tests/           Room state, stores, services and rich text tests
  Uniform.Matrix.Client.Terminal.Tests/  Option registration, message log and image tests
  Uniform.Matrix.Client.Avalonia.Core.Tests/  View models against the in-memory server, headless rendering
  Uniform.Matrix.Protocol.Server.Tests/  Event format and auth rule tests
  Uniform.Matrix.Server.Tests/           Server through our own client, in memory
docs/                                    This site: the home page and the generated API reference (api/)
  Uniform.Matrix/                        Building, testing, documentation, and one folder per project
theme/                                   Changes to DocFX's statictoc template, for all sites
user-docs/
  Uniform.Matrix.Client.Desktop/         User guide of the desktop app
  Uniform.Matrix.Client.Android/         User guide of the Android app, mostly the desktop guide's pages
  Uniform.Matrix.Client.Terminal/        User guide of the terminal client
  Uniform.Matrix.Server.Host/            User guide of the server
  Uniform.Matrix.Core/                   Text included by more than one guide
```

Dependencies point down; the protocol libraries know nothing of storage, hosting or UI:

```text
Uniform.Matrix.Client.Terminal ──► Uniform.Matrix.Client ──► Uniform.Matrix.Protocol.Client ──► Uniform.Matrix.Protocol.Core
    │                              │   │
    │                              │   └──► Uniform.Matrix.Protocol.Crypto ──► Uniform.Matrix.Protocol.Core
    │                              └───────┐
    │                                      ▼
    └──► Uniform.Matrix.Gui.Terminal ──► Uniform.Matrix.Hosting
```

The graphical app uses the same engine, without the terminal components:

```text
Uniform.Matrix.Client.Desktop ──┐
                              ├──► Uniform.Matrix.Client.Avalonia.Core ──► Uniform.Matrix.Client ──► …
Uniform.Matrix.Client.Android ──┘
```

The server side, which also uses Core through Crypto:

```text
Uniform.Matrix.Server.Host ──► Uniform.Matrix.Server ──► Uniform.Matrix.Protocol.Server ──► Uniform.Matrix.Protocol.Crypto
    │                          │
    │                          └───────────┐
    │                                      ▼
    └──► Uniform.Matrix.Gui.Terminal ──► Uniform.Matrix.Hosting
```

The server reuses Core, Crypto (to check key signatures), Hosting and Gui.Terminal, so its settings and log windows are the same as the client's. See [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Article) for how it works.
