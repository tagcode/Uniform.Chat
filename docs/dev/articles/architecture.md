# Architecture

## Projects

```text
src/
  Uniform.Matrix.Protocol.Core/          Shared spec types, no dependencies
    Api/                                 Request, response, event and content records; constants
    UserId.cs,                           MxcUri.cs                      Identifiers
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
  Durum.Matrix.Client.Terminal/          Terminal client app
    Configuration/                       Display options
    Ui/                                  Main window, message log, image previews, client dialogs
  Durum.Matrix.Client.Gui/               Avalonia app shared by desktop and Android: views, view models, services
    Services/                            One engine host per sign-in, remembered account, UI dispatcher
    ViewModels/                          Login, chat, room list items, message list
    Views/                               Windows and views in XAML, rich text, styles
  Durum.Matrix.Client.Desktop/           Windows and Linux entry point, Linux menu entry installer
  Durum.Matrix.Client.Android/           Android entry point, manifest and resources; not in the solution
  Uniform.Matrix.Protocol.Server/        Room versions, event format, hashing, signing, redaction, auth rules
  Uniform.Matrix.Server/                 Homeserver engine: AddMatrixServer, endpoints, services, storage
  Durum.Matrix.Server.Host/              Server app, with or without the dashboard
tests/
  Uniform.Matrix.Protocol.Tests/         API and canonical JSON unit tests, live tests against the test server
  Uniform.Matrix.Protocol.Crypto.Tests/  Olm, Megolm, oracle, protocol and live encryption tests
  Uniform.Matrix.Hosting.Tests/          Settings and log file tests
  Uniform.Matrix.Client.Tests/           Room state, stores, services and rich text tests
  Durum.Matrix.Client.Terminal.Tests/    Option registration, message log and image tests
  Durum.Matrix.Client.Gui.Tests/         View models against the in-memory server, headless rendering
  Uniform.Matrix.Protocol.Server.Tests/  Event format and auth rule tests
  Uniform.Matrix.Server.Tests/           Server through our own client, in memory
docs/
  user/                                  End-user DocFX site
  dev/                                   This site
```

Dependencies point down; the protocol libraries know nothing of storage, hosting or UI:

```text
Durum.Matrix.Client.Terminal ──► Uniform.Matrix.Client ──► Uniform.Matrix.Protocol.Client ──► Uniform.Matrix.Protocol.Core
    │                              │   │
    │                              │   └──► Uniform.Matrix.Protocol.Crypto ──► Uniform.Matrix.Protocol.Core
    │                              └───────┐
    │                                      ▼
    └──► Uniform.Matrix.Gui.Terminal ──► Uniform.Matrix.Hosting
```

The graphical app uses the same engine, without the terminal components:

```text
Durum.Matrix.Client.Desktop ──┐
                              ├──► Durum.Matrix.Client.Gui ──► Uniform.Matrix.Client ──► …
Durum.Matrix.Client.Android ──┘
```

The server side, which also uses Core through Crypto:

```text
Durum.Matrix.Server.Host ──► Uniform.Matrix.Server ──► Uniform.Matrix.Protocol.Server ──► Uniform.Matrix.Protocol.Crypto
    │                          │
    │                          └───────────┐
    │                                      ▼
    └──► Uniform.Matrix.Gui.Terminal ──► Uniform.Matrix.Hosting
```

The server reuses Core, Crypto (to check key signatures), Hosting and Gui.Terminal, so its settings and log windows are the same as the client's. See [Server](server.md) for how it works.

## Uniform.Matrix.Protocol.Core

The types both sides of the protocol use: identifiers, events, the request and response records of the APIs, canonical JSON and error codes.

Models are records with `init` properties.

- **Responses** mark spec-required fields `required`, so a response missing one fails during deserialization rather than surfacing as a null later. Collections default to empty, so `sync.Rooms.Join` never needs a null check.
- **Events** derive from [MatrixEvent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent), which keeps `content` as raw JSON. [TryGetContent](xref:Uniform.Matrix.Protocol.Core.MatrixEvent.TryGetContent*) reads it as a typed content record and returns false instead of throwing when another client sent malformed content.
- **Content records** such as [MessageContent](xref:Uniform.Matrix.Protocol.Core.MessageContent) have no required members for the same reason.
- **Constants** for event types, message types, memberships and error codes live in static classes such as [EventTypes](xref:Uniform.Matrix.Protocol.Core.EventTypes) and [ErrorCodes](xref:Uniform.Matrix.Protocol.Core.ErrorCodes).

JSON names are mapped to snake_case by [MatrixJson.Options](xref:Uniform.Matrix.Protocol.Core.MatrixJson.Options); `[JsonPropertyName]` is only used for dotted keys such as `m.relates_to` and names that are not snake_case, such as `displayname` and `msgtype`.

## Uniform.Matrix.Protocol.Client

[MatrixClient](xref:Uniform.Matrix.Protocol.Client.MatrixClient) wraps one homeserver and one session. It is split into partial files by spec area. Each endpoint is one method that sends a request and returns a response record. All requests go through a single private `SendAsync`, which:

1. adds the bearer token for authenticated endpoints, and refuses to send if not logged in;
2. serializes the body with [MatrixJson.Options](xref:Uniform.Matrix.Protocol.Core.MatrixJson.Options);
3. retries [rate-limited](https://spec.matrix.org/latest/client-server-api/#rate-limiting) requests up to [MaxRateLimitRetries](xref:Uniform.Matrix.Protocol.Client.MatrixClient.MaxRateLimitRetries) times, waiting as long as the server asks, unless that is longer than [MaxRateLimitDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.MaxRateLimitDelay);
4. turns other non-2xx responses into a [MatrixException](xref:Uniform.Matrix.Protocol.Core.MatrixException) when the body is a [standard error](https://spec.matrix.org/latest/client-server-api/#standard-error-response), or an `HttpRequestException` otherwise.

Path segments such as room IDs (`!abc:server`), aliases (`#room:server`) and event IDs are always percent-escaped. The caller owns the `HttpClient`, which keeps the library usable with `IHttpClientFactory`, custom handlers and test stubs.

### Session

After login, [Session](xref:Uniform.Matrix.Protocol.Client.MatrixClient.Session) holds a [MatrixSession](xref:Uniform.Matrix.Protocol.Client.MatrixSession) with homeserver, user ID, device ID and access token. Store it and pass it to the `MatrixClient(HttpClient, MatrixSession)` constructor to resume without logging in again.

### Sync

[SyncLoopAsync](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncLoopAsync*) is the main way to receive events. It long-polls [sync](https://spec.matrix.org/latest/client-server-api/#syncing), passes each `next_batch` as the next `since`, and yields every response. Transient failures (network errors, 5xx, timeouts, rate limiting) are retried with exponential backoff between [SyncRetryDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncRetryDelay) and [SyncRetryMaxDelay](xref:Uniform.Matrix.Protocol.Client.MatrixClient.SyncRetryMaxDelay); other errors, such as an expired token, end the loop with an exception.

### User-interactive auth

Some endpoints, such as deleting devices, answer the first request with 401 and a body listing accepted [auth flows](https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api) and a session ID. Such bodies have no `errcode`, so the request pipeline maps them to [UserInteractiveAuthException](xref:Uniform.Matrix.Protocol.Client.UserInteractiveAuthException) before ordinary Matrix errors. Methods that need it take a password and answer a `m.login.password` challenge themselves; without a password the exception reaches the caller, who can ask the user and call again.

### Discovery

[HomeserverDiscovery.DiscoverAsync](xref:Uniform.Matrix.Protocol.Client.HomeserverDiscovery.DiscoverAsync*) follows [server discovery](https://spec.matrix.org/latest/client-server-api/#server-discovery): it reads `https://{server}/.well-known/matrix/client` and falls back to `https://{server}/` when the file does not exist.

## Uniform.Matrix.Protocol.Crypto

End-to-end encryption is pure .NET, so the client runs on any platform .NET runs on, with no native libraries. Two layers:

1. **Olm and Megolm**: a port of [vodozemac](https://github.com/matrix-org/vodozemac) 0.11.1, the maintained Rust implementation, to C#. It keeps vodozemac's API shape (`OlmAccount`, `OlmSession`, `MegolmOutboundSession`, `MegolmInboundSession`, `Ed25519`, `PkEncryption`, `Sas`) and its wire formats: Olm and Megolm messages with truncated MACs (session config version 1), libolm-compatible backup encryption, `hkdf-hmac-sha256.v2` SAS MACs. X25519 and Ed25519 come from BouncyCastle; HKDF, HMAC, SHA-256 and AES from .NET. Errors are `OlmException`. The port keeps vodozemac's Apache-2.0 copyright notices; each ported file says so in its header, and THIRD-PARTY-NOTICES.md at the repository root lists them.
   - Pickles are vodozemac's own format, JSON encrypted with HKDF-derived AES-CBC and HMAC, so state written by the native library loads here and back. Libolm pickles and expanded Ed25519 keys are not supported.
   - Unlike vodozemac, this code has not been audited, and the .NET runtime cannot guarantee constant-time execution or that secrets are wiped from memory; `Dispose` clears key buffers on a best-effort basis.
2. **OlmMachine**: the Matrix protocol.
   - Keys: signed device keys on first use, one-time keys topped up to half capacity from sync's `device_one_time_keys_count`, a fallback key replaced when used. Everything is signed over [canonical JSON](xref:Uniform.Matrix.Protocol.Core.CanonicalJson).
   - Devices: `/keys/query` results are accepted only with a valid self-signature; a known device ID keeps its first fingerprint. Lists are marked outdated from `device_lists.changed`.
   - Receiving: Olm to-device events are decrypted with an existing session or a new one from a pre-key message, and the payload must name us, our key and the actual sender. `m.room_key` becomes an inbound Megolm session; `RoomKeyReceived` lets callers retry waiting events. Megolm events are checked for the right room and against replayed indices.
   - Sending: one outbound session per room, rotated after the room's message or time limit or when someone it was shared with left. The key goes to every device of every member over Olm, claiming and verifying one-time keys where no session exists. Our own copy is kept so our messages decrypt in sync.
   - Cross-signing: `/keys/query` master, self-signing and user-signing keys are accepted only when the master key signed the others; devices note which self-signing key validly signed them. A device is *cross-signed* when that key belongs to a trusted identity: our own when its private master key is here, or verified by SAS; another user's when our user-signing key signed their master key. `SetUpRecoveryAsync` refuses to replace existing cross-signing keys, since that would distrust every device.
   - Secret storage: `m.secret_storage.v1.aes-hmac-sha2` (HKDF-SHA-256, AES-256-CTR, HMAC-SHA-256) with the key check from the key description, a base58 recovery key with prefix and parity, or a PBKDF2 passphrase. It holds the three cross-signing private keys and the backup key.
   - Key backup: `m.megolm_backup.v1.curve25519-aes-sha2`. Sessions are exported from their first known index, encrypted to the backup's public key and uploaded in batches after each sync; `BackedUp` marks what is done. Uploads happen only with the private key of the current version, so a backup replaced on the server cannot capture keys. Restore imports sessions that reach further back than stored ones and raises `RoomKeyReceived`.
   - Key requests: a missing session is requested once from our other devices with `m.room_key_request`. Requests and `m.secret.request` are answered only to our cross-signed or verified devices; forwarded keys and secrets are accepted only from them, only for our own outstanding requests, and secrets only when they match the published public keys.
   - Verification: to-device SAS (`m.sas.v1`, `curve25519-hkdf-sha256`, `hkdf-hmac-sha256.v2`) with the accept commitment, emoji and decimals, and MACs over the device key and, when trusted, the master key. Afterwards our own device is signed with the self-signing key and asked for missing secrets; another user's master key is signed with the user-signing key.
   - Imported sessions (backup, forwarded) are marked `Imported`, as their sender is not proven by Olm.
   - All calls are serialized; state lives in an `ICryptoStore` as encrypted pickles. Private keys are stored as named values, encrypted with the pickle key.

Encrypted attachments (`EncryptedAttachments`) use AES-256-CTR with a random key and an IV whose low 64 bits are zero, and SHA-256 of the ciphertext, which is checked before decrypting. The message carries `file` instead of `url`; `MessageContent.MediaUrl` gives either.

## Uniform.Matrix.Hosting

Settings and logging for any application on the .NET generic host, the client's and later the server's. Registered with `builder.AddSettings()`, `builder.AddSessionLogging(directory, keep)` and `services.AddOptionSection<T>(configuration, section)`; folders follow the XDG base directories through `AppDirectories`.

### Settings

`SettingsStore` adds two configuration layers: the application's settings file (`$XDG_CONFIG_HOME/<app>/settings.json`, where `<app>` is the host's application name in kebab case, e.g. `durum-matrix-client-terminal`) right after `appsettings.json`, so environment and command line still win, and an in-memory *applied* layer on top. Applying sets the top layer and reloads the configuration; saving merges into the file, clears those keys from the applied layer and reloads. Options read through `IOptionsMonitor<UiOptions>` change at once; the others are marked `[RestartRequired]` and read at start. Each application registers its option records with `AddOptionSection<T>`, and `OptionCatalog` lists their properties by reflection, with descriptions read from the XML documentation of each record's assembly, so the settings window and the API reference say the same. `[Secret]` options are not shown, saved or exported. `SettingsEditor` holds the window's edits and is tested without the UI.

### Logs

`FileLoggerProvider` writes `logs/client-yyyyMMdd-HHmmss.log` per session and prunes the oldest beyond `Storage:LogFilesKept`. `LogFiles` lists and parses them: a line with time and level starts an entry, other lines (stack traces) continue it.

## Uniform.Matrix.Gui.Terminal

Terminal.Gui components without Matrix types, for every terminal application:

- `SettingsWindow` lists a `SettingsEditor`'s options with value, source and description, and edits, resets, applies, saves, loads and exports them.
- `LogWindow` lists session logs, filters entries by level and text, colors them by level, shows whole entries and follows the current file.
- `Dialogs` has the general prompts: ask, confirm, choose, pick one of a list, pick a file, show text or a value to copy.
- `TextWrapper` wraps text by terminal columns, so emoji and CJK take two.

## Uniform.Matrix.Client

The headless client. `builder.AddMatrixClient()` registers the Matrix and storage options, session logging under the data directory, storage, encryption, sync and the outbox; the application adds its own `ICredentialPrompt` for the first login. The data directory defaults to `~/.local/share/<app>` by the host's application name.

```text
MatrixWorker (hosted service)
  ├─ ClientStore.MigrateAsync      create or upgrade the SQLite schema
  ├─ ClientSession.ConnectAsync    resume stored token, or log in and store it
  ├─ sync                          full-state sync from stored position, then SyncLoopAsync
  │    └─ SyncProcessor            save events + position, apply to RoomStore, publish SyncChanges
  └─ OutboxSender.RunAsync         send queued messages in order
a frontend reads SyncChanges and queues messages via OutboxSender
```

The client stores encryption state in its SQLite database (`EfCryptoStore`), with the 32-byte pickle key in `crypto.key` (mode 0600). `SyncProcessor` decrypts each sync before storing it and re-decrypts stored events when late keys arrive; `OutboxSender` encrypts for encrypted rooms after loading the full member list, and fails a message rather than sending it in the clear when encryption is unavailable. Files for encrypted rooms are encrypted and uploaded, then sent through the outbox; `MediaCache` decrypts them after the hash check, and the server is not asked for thumbnails of ciphertext. `CryptoService` backs up new room keys after each sync, and `MainWindow.Security.cs` holds the recovery and verification dialogs.

### Room state

[RoomStore](xref:Uniform.Matrix.Client.State.RoomStore) turns sync responses into one [Room](xref:Uniform.Matrix.Client.State.Room) per room ID. [Apply](xref:Uniform.Matrix.Client.State.RoomStore.Apply*) returns [SyncChanges](xref:Uniform.Matrix.Client.State.SyncChanges) so a UI only redraws what changed. A room keeps:

- current state, with display name computed per [spec](https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-room): name, alias, then member names such as "Alice and Bob" or "Empty Room (was Alice)";
- members, with display names [disambiguated](https://spec.matrix.org/latest/client-server-api/#calculating-the-display-name-for-a-user) as "Bob (@bob:server)" while shared;
- the most recent [MaxTimelineLength](xref:Uniform.Matrix.Client.State.RoomStore.MaxTimelineLength) events, with redactions applied. A limited timeline clears older events, since they no longer connect; [BackfillAsync](xref:Uniform.Matrix.Client.State.RoomStore.BackfillAsync*) loads history back;
- typing users, read receipts, unread counts, tags, fully read marker and the direct-message user from `m.direct`.

Servers may lazy-load members, sending only those needed for the timeline. [LoadMembersAsync](xref:Uniform.Matrix.Client.State.RoomStore.LoadMembersAsync*) fetches the full list, e.g. for a nick list. The store is not thread-safe; apply syncs and read rooms on one thread.

### Rich text

`MarkdownComposer` turns typed Markdown into `formatted_body` with Markdig (CommonMark, strikethrough, autolinks, raw HTML disabled), unwraps a single paragraph like Element, and turns completed nicks into `matrix.to` mention pills; text without formatting or mentions is sent without HTML. `HtmlText` renders incoming HTML with AngleSharp into terminal text: blocks, lists, quotes, code, links with addresses, mention names, hidden spoilers, and no `mx-reply` fallback.

### Local storage

Each account has its own directory, `<data directory>/<localpart>_<server>/`, holding `client.db` and `media/`. Tables:

| Table | Contents |
|---|---|
| `Accounts` | Homeserver, device ID, access token and the last `next_batch` |
| `Events` | Every received timeline event as JSON, indexed by room and time; redactions strip stored content |
| `Outbox` | Messages written locally, with state Pending, Sent or Failed |
| `Media` | Downloaded files, stored under `media/` |

Events and the sync position are saved in one transaction before the in-memory state changes, so a crash can at worst re-apply a sync; events are deduplicated by ID. On start, the first sync asks for full state from the stored position, which rebuilds room names and members in memory without losing messages.

### Outbox

Messages are written to the `Outbox` table first and then sent in order. Each message gets its transaction ID when queued and keeps it across retries and restarts, so the server never stores a resent message twice. A transient failure (network, 5xx, rate limit) keeps the message and pauses the queue to keep order; a permanent rejection (e.g. not in the room) marks it Failed and moves on. If the app crashes after the server accepted a message but before it was marked Sent, its echo in sync carries the transaction ID and confirms it.

SQLite is the default, through EF Core. No EF Core provider stores YAML, so a YAML store would be a separate implementation of `ClientStore`.

## Durum.Matrix.Client.Terminal

The terminal app: `Program` calls `AddSettings`, `AddMatrixClient` and registers its `UiOptions`, then runs the UI.

### User interface

`Program` creates the Terminal.Gui application first, starts the host's background services and then runs `MainWindow` on the main thread until the user quits. Background work never touches views directly: sync changes, outbox changes and connection status are marshalled with `IApplication.Invoke`, and the password prompt shows its dialog the same way.

- `MainWindow` lays out the menu bar, room list, `TimelinePane`, member list, input line and status bar, and handles menu actions, `/commands` and keys. Shortcuts are handled application-wide in one key handler; menu items only show them.
- `TimelinePane` is a `ListView` of wrapped lines. It rewraps on resize, colors lines by `EntryKind`, inserts day separators, keeps pending outbox messages last and follows new lines while scrolled to the bottom.
- `MainWindow.Messages.cs` holds message actions (reply, react, edit, delete, save), compose modes, typing notifications, nick completion, file sending and the devices dialog. Replies, edits and reactions go through the outbox like messages; uploads and redactions are sent directly.
- `MainWindow.Images.cs` shows image previews. An entry with an `ImageUri` asks `TimelinePane.PreviewProvider` for lines on every rebuild; the first request starts a background load of an 800×600 thumbnail through `MediaCache` (falling back to the full image), decodes it with StbImageSharp into `DecodedImage`, then refreshes the pane. `AsciiArt.Render` samples a brightness grid (one value per cell, or 2×4 per cell for Braille), stretches its contrast, then maps it to shade blocks ` ░▒▓█` with Floyd–Steinberg dithering between the five levels, to the 70-step ASCII ramp, or to dithered Braille dots. It corrects for cells being about twice as tall as wide. Previews fill the pane unless limited in settings and are cached per image and size; the pane rebuilds on height changes while it shows images. The viewer feeds a downscaled image to Terminal.Gui's `ImageView`, which uses Kitty or Sixel graphics when available.
- `MessageFormatter` and `TextWrapper` are pure functions and unit-tested. Wrapping counts terminal columns, so emoji and CJK take two.
- A dialog opened from another dialog's result is queued with `IApplication.Invoke`; opening it directly lets the key that closed the first dialog reach the second.
- Terminal.Gui 2.5 does not report F3 when the terminal sends `ESC O R` (most xterm-style terminals do), so message selection is on Ctrl+Up.
- Reading `Room` state for display goes through `SyncProcessor.Read`, as `RoomStore` is not thread-safe. History and member loading fetch from the server outside the lock and apply inside it.

## Durum.Matrix.Client.Gui

The graphical app, in Avalonia 12 with the Fluent theme and CommunityToolkit.Mvvm. The same views run in a desktop window and as the single view of the Android activity; `GuiPlatform` carries what differs: the application name (and so the data folders), the device name shown to other sessions, whether the screen is always one pane, and extra services.

### Hosts

`AccountPaths` needs `Matrix:UserId` when the services are built, so the app has no host until someone signs in. `ClientConnection.StartAsync` builds a `HostApplicationBuilder` per sign-in: `AddSettings`, the user ID and server from the form as in-memory configuration, `AddMatrixClient`, and `GuiCredentialPrompt`, which returns the form's password or throws `PasswordRequiredException` when a resumed session needs one. Signing out or in again disposes the host and builds a new one. `AccountMemory` keeps the last user ID in the settings file, so the next start resumes without the form. `SettingsViewModel` keeps the theme, text size and notification switch in its `Ui` section and saves on every change; `App` applies the theme as `RequestedThemeVariant` and the text size as Fluent's `ControlContentThemeFontSize`.

### View models

`MainViewModel` switches `Page` between `LoginViewModel`, `ConnectingViewModel` and `ChatViewModel`; `MainView` picks the view by data template.

`ChatViewModel` follows the engine's events on a background task: it reads `SyncProcessor.Changes` and posts each batch to the UI thread through `IUiDispatcher`, where room state is read under `SyncProcessor.Read`. `ConnectionStatus.Changed`, `EventsDecrypted` and `OutboxSender.RoomChanged` are posted the same way. Each posted change runs inside a try/catch that logs, as an exception on the UI thread would end the app.

`MessageList` turns events into `MessageViewModel` items. Edits, deletions and reactions change the item they refer to instead of adding one, also when older history arrives after them. Unsent outbox messages are shown last; their list is reloaded on every outbox change, and a version counter drops loads that finish out of order, so a sent message never comes back as pending.

Room creation, invites, deletions, typing notices, read markers and file uploads call `MatrixClient` directly; messages, replies, edits and reactions go through the outbox. Files to encrypted rooms are encrypted with `UploadEncryptedAsync` and their message is sent through the outbox, which encrypts it.

`SecurityViewModel` shows `GetSecurityStatusAsync` and offers set-up, recovery or verification by what is missing; it waits for `CryptoService.WhenStarted`, as encryption starts after sign-in. Setting up recovery asks for the password only when the server answers with a user-interactive auth challenge. `ChatViewModel` follows `CryptoService.VerificationChanged` and shows one `VerificationViewModel` at a time, so incoming requests and our own requests go through the same panel.

`ChatViewModel` also decides on notifications: new messages in direct chats, messages whose `m.mentions` name the user or the room, and invites that were not in the list before, at most three per sync and none from history or the first sync. They are passed to `GuiPlatform.Notifier` unless the window has the focus on that room. The desktop app sets `FreeDesktopNotifier` on Linux, which calls `org.freedesktop.Notifications.Notify` over the session bus with Tmds.DBus.Protocol, the D-Bus library Avalonia already uses.

### Views

- `ChatView` has the room list and the open room side by side, or one at a time below 720 pixels and always on phones. It keeps the newest message in view while scrolled to the bottom, loads older messages at the top, and goes back to the list on `TopLevel.BackRequested` (Android's back button).
- `RichText` is a `SelectableTextBlock` that parses a message's `formatted_body` with AngleSharp into inlines: emphasis, code, headings, quotes, lists, links and mention pills; reply fallbacks are dropped. Without HTML it shows the plain body.
- File dialogs use `TopLevel.StorageProvider`, which is the system picker on both platforms.
- The message box's keys are handled in the tunnel phase: a multi-line `TextBox` takes Enter and Ctrl+V itself, so a bubbling handler never sees them. Pasting checks the clipboard for files, then an image, and falls back to the box's own text paste.
- Links: `RichText` records the character range of each link while building its inlines (a `LineBreak` counts as one character) and hit-tests `TextLayout` on click, as Avalonia has no inline hyperlink. Plain-text bodies get their web addresses linked.
- Bindings are compiled (`x:DataType`), so binding errors fail the build.

### Android

`MainApplication` sets `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to the app's private files folder before anything else runs, so `AppDirectories` and the engine need no Android code. The activity resizes for the keyboard. The app has internet permission only, and Android backup is off, so encryption keys stay on the device.

## Not yet covered

- Encryption extras: QR code verification, in-room verification requests, dehydrated devices, and key export files.
- Registration, refresh tokens, push rules, spaces and threads.
- In the graphical app: renaming devices, and notifications on Windows and Android.
