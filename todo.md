# Client

The protocol library (`Uniform.Matrix.Protocol.Client`), the engine (`Uniform.Matrix.Client`) and the terminal app.

## Login and account
- SSO and OAuth 2.0/OIDC login. Matrix is moving towards OIDC; matrix.org already uses it, so password login does not work everywhere. The biggest gap for everyday use.
- Refresh tokens, and handling a soft logout when a token expires.
- Use the homeserver URL a server may return at login; it is ignored now.
- Registration in the terminal app; the library has `RegisterAsync`.
- Account deactivation, and email and phone identifiers (3PIDs).

## Rooms and messages
- Threads, related events (`/relations`) and message search.
- Spaces: the room hierarchy.
- Knocking, room upgrades and server capabilities.
- Typed content for join rules and history visibility.
- Reporting content.

## Notifications and sync
- Push rules, pushers and the notifications list; notifications outside the open window.
- Simplified sliding sync (MSC4186), the faster sync that newer clients use.

## Encryption
- QR code verification, verification requests inside a room, key export files, dehydrated devices.

## Desktop and Android app
- Renaming devices, and choosing your own reaction emoji.
- Showing logs in the app, and more settings: Markdown on or off, image previews, the download folder.
- Notifications on Windows (toast notifications) and Android (UnifiedPush or Firebase, which need a push gateway); clicking a notification to open its room.
- A full-size image viewer inside the app; avatars of people in the member list (the timeline has them).
- Spaces, and forgetting old rooms.
- Show the stored rooms and messages when the app starts without network; only timeline events are stored now, not room state, so the room list fills in once connected.
- Android: release signing and a Play Store listing; Android builds in the release workflow.

## Library quality
- Typed RoomId, EventId and RoomAlias; today these are plain strings (`UserId` and `MxcUri` are typed).
- Retrying network errors on ordinary requests; today only sync and rate-limited requests retry.
- JSON source generation, for trimming.

# Server

How it works is in `docs/Uniform.Matrix/Uniform.Matrix.Server/`. Tests run our own `MatrixClient` against the server in memory; run the live tests against Synapse now and then as the reference.

## Milestone 1: standalone homeserver (done, except compatibility)
- Element Web smoke test against the server.
- Complement client-server tests. Needs Go, and Docker access for the user (the docker group, or rootless Docker).
- Email pushers: they are stored, but no email is sent.

## Later
PostgreSQL in production, admin API, a push gateway of our own (for the Android app, with UnifiedPush or Firebase), OpenTelemetry.

URL previews (the server side is done: `UrlPreviews:Enabled`, off by default):
- Server: allow and deny lists of hosts.
- Clients: show `[title — description]` and the `og:image` under messages with links, with `MatrixClient.GetUrlPreviewAsync`; a `Ui:UrlPreviews` setting, off by default in encrypted rooms.

## Milestone 2: federation
A public test name, ports and a certificate are available (kept out of the repo). Done: HTTPS from certbot files (`Tls:*`), signing and checking `X-Matrix` requests, server name resolution (IP, port, well-known), the key server, remote key fetching, `query/profile` and remote profiles, joining remote rooms by room ID (`make_join`/`send_join` both ways), messages both ways (`/send` and a background sender), joining version 12 rooms through `via` servers, inviting other servers' users and receiving their invites (`invite` v2, signed by both servers), encryption across servers (keys, one-time keys, to-device, device list updates), typing and read receipts, retrying transactions with backoff, the `r0` and unstable key backup paths of older clients, all behind `Federation:Enabled`. Voice and video calls work between users of this server in SchildiChat Legacy, through TURN. Tested against a real Synapse: joins of version 11 and 12 rooms, and messages both ways.

Also done: sliding sync (MSC4186) for Element X and SchildiChat Next, history for other servers (`get_missing_events`, `backfill`, `event`, `state`, `state_ids`), rejecting invites through the inviting server (`make_leave`/`send_leave` both ways), joining by remote alias (`query/directory` both ways), creating version 12 rooms, presence between servers, `Server:TrustedProxies` for `X-Forwarded-For` behind a reverse proxy, the outgoing federation queue and the sender's position kept across restarts, media of other servers (fetched through the federation media API and kept) and serving ours to them, remote signing keys kept in the database with key servers (`Federation:TrustedKeyServers`, and `/_matrix/key/v2/query` served), room summaries, space hierarchies (also over federation), relations, `timestamp_to_event`, reports (logged), filling gaps with `get_missing_events`, public room directories of other servers (`publicRooms` both ways), received events checked against their auth events, the state before them and the current state (soft failure), state resolution v2 and v2.1 for forks, auth chains of joined rooms checked, gaps of any size filled through `state_ids`, keys of many servers fetched at once (key server batches, `Federation:KeyRequestTimeout`), version 12 as the default room version, room ACLs, SRV lookup, previews of rooms and spaces on other servers, threads (`/threads` and thread summaries), and a review list for reports in the dashboard. Element X calls work between servers through Element Call and LiveKit.

Next:
- Partial-state joins ([MSC3706](https://github.com/matrix-org/matrix-spec-proposals/pull/3706)) for very large rooms, whose joins now wait for every event's signature check.
- Sliding sync: per-list timeline limits, left rooms, more list filters, keeping connections across restarts.
- Room upgrades (`/upgrade`, tombstones), search.

Keep the test server's registration closed and rate limits on, since unfinished software will be reachable from the internet.

# Voice calls

- Voice and video calls in the Avalonia client, and in the server:
  - 1:1 calls: `m.call.*` events, `/voip/turnServer` on the server, and a TURN server such as coturn.
  - Group calls: MatrixRTC with a LiveKit SFU, announced in `.well-known`.

# UserManagement 

- Separate user management out into Uniform.UserManagement that is agnostic of Uniform.Matrix.*
- Create Uniform.UserManagement.Gui.Terminal
- Have Matrix server include Uniform.UserManagement.Gui.Terminal

# Uniform.Chat.Desktop

- The look and feel of avalonia based GUI is kinda off. Perhaps it can be worked on, or perhaps MAUI or WinUI has better touch?
A theme that fits each platform.

Android: Material.Avalonia, with Material 3 colours, shapes and ripples.
Desktop: keep Fluent, but with our own accent colour and dark mode following the system.
One shared set of style resources, with small differences per platform.
Typography: the system font (Roboto on Android, Segoe UI on Windows, the desktop's own font on Linux), with clear sizes for room names, message text and timestamps.

Spacing and density:

Phones: larger touch targets, at least 48 dp.
Desktop: denser lists.
Everywhere: consistent padding.
Much of the "unpolished" impression comes from this.

Chat-specific look:

message bubbles;
grouping of consecutive messages from the same sender;
avatars with initials as fallback;
unread badges;
a proper composer bar.
Motion: short transitions between room list and room on phones, smooth scrolling, and a typing indicator that animates.

Android conventions:

edge-to-edge layout with the right system bar colours;
the back gesture;
the keyboard pushing the composer up;
long-press menus instead of right-click.

# Stun/Turn

- Uniform.Stun.Protocol
  Uniform.Turn.Protocol
  Uniform.Stun
  Uniform.Turn
  Uniform.Sturn.Hosting
  Uniform.Sturn.Host

# Embedded help

For desktop clients, F1 could open in system browser the user documentations that are already included in the release.

# Android Flutter UI

Prototype with Flutter+Avalania.

# README.md

- Clean up package README.md.

# Voice messages



# User management

Admin user manages other users through matrix protocol.

# Desktop Client

- The Text Size setting doesn't affect the text size in the chat window, nor the room title, nor the "Uniform.Matrix" application name.
- The range of sizes should be wider, add: very small, very large

# Google Play

- Background sync service: The app keeps syncing with the screen off through a foreground service of type remoteMessaging. Play allows that type only for relaying texts to another device, such as a web client paired with the phone. A permanent sync connection doesn't fit, and reviewers check this against a demo video. Ways out:
  - Use push notifications through a push gateway. This is what Play-store Matrix clients do; it's the push work your approved-packages list already allows.
  - Keep the service only in a separate build for direct APK or F-Droid releases.
  - Declare it as specialUse with a written justification, which Google reviews case by case and may refuse.
- Service type for calls. Calls now use the microphone type. The type Google intends for voice-over-IP calls is phoneCall, together with microphone and the MANAGE_OWN_CALLS permission. Ideally the app would also register its calls with Android's telephony system. The demo video is simple: "user starts a call".
- Microphone permission timing: the app asks for it at startup. Asking the first time a call starts is better practice, and it's what Google's permission guidance recommends.
- on Android 13 and later, the app asks for notification permission only at the first call. Until then, message notifications stay off.
- The name: "Matrix" is a trademark of The Matrix.org Foundation, and the Metadata and Intellectual Property policies apply to store names. 
  - Foundation's trademark guidelines
  - Listing title like "Uniform — Matrix chat", before you publish.
- Registering calls with Android's telephony system (ConnectionService).

# Terminal remote admin

For terminal client, server remote admin features. 

# Android client

- tiny ui glitch: When phone goes online, a message from outbox gets sent, it disappears from gui and appears once it is received. Instead "Not sent" should change to "sending" and then status disappears.

# Logging

- For desktop and android client
  - A settings for log level.
  - A window for perusing log files.

# Encryption

- Settings for encryption strength. Key size

# Local encryption

- A) SQLCipher (the SQLitePCLRaw.bundle_e_sqlcipher package) encrypts the whole database. The key goes into the Android Keystore, Windows DPAPI, or the Linux Secret Service.
- B) Own encryption: AES-GCM on the message and outbox contents with a key from the same keystores. No new dependency.
