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
- Renaming devices, showing who is verified in member lists, and choosing your own reaction emoji.
- Showing logs in the app, and more settings: Markdown on or off, image previews, the download folder.
- Notifications on Windows (toast notifications) and Android (UnifiedPush or Firebase, which need a push gateway); clicking a notification to open its room.
- A full-size image viewer inside the app; avatars of people in the timeline and member list.
- Room directory, spaces, and leaving or forgetting old rooms.
- Android: release signing and a Play Store listing; Android builds in the release workflow.

## Library quality
- Typed RoomId, EventId and RoomAlias; today these are plain strings (`UserId` and `MxcUri` are typed).
- Retrying network errors on ordinary requests; today only sync and rate-limited requests retry.
- JSON source generation, for trimming.

# Server

How it works is in `docs/dev/articles/server.md`. Tests run our own `MatrixClient` against the server in memory; run the live tests against Synapse now and then as the reference.

## Milestone 1: standalone homeserver (done, except compatibility)
- Element Web smoke test against the server.
- Complement client-server tests. Needs Go, and Docker access for the user (the docker group, or rootless Docker).
- Email pushers: they are stored, but no email is sent.
- Forwarded headers from a trusted reverse proxy (a known-proxies setting), so login rate limits count each client address instead of the proxy's.

## Later
PostgreSQL in production, admin API, a push gateway of our own (for the Android app, with UnifiedPush or Firebase), sliding sync (MSC4186), OpenTelemetry.

URL previews (the server side is done: `UrlPreviews:Enabled`, off by default):
- Server: allow and deny lists of hosts.
- Clients: show `[title — description]` and the `og:image` under messages with links, with `MatrixClient.GetUrlPreviewAsync`; a `Ui:UrlPreviews` setting, off by default in encrypted rooms.

## Milestone 2: federation
A public test name, ports and a certificate are available (kept out of the repo). Done: HTTPS from certbot files (`Tls:*`), signing and checking `X-Matrix` requests, server name resolution (IP, port, well-known), the key server, remote key fetching, `query/profile` and remote profiles, joining remote rooms by room ID (`make_join`/`send_join` both ways), messages both ways (`/send` and a background sender), joining version 12 rooms through `via` servers, inviting other servers' users and receiving their invites (`invite` v2, signed by both servers), encryption across servers (keys, one-time keys, to-device, device list updates), typing and read receipts, retrying transactions with backoff, the `r0` and unstable key backup paths of older clients, all behind `Federation:Enabled`. Voice and video calls work between users of this server in SchildiChat Legacy, through TURN. Tested against a real Synapse: joins of version 11 and 12 rooms, and messages both ways.

Also done: sliding sync (MSC4186) for Element X and SchildiChat Next, history for other servers (`get_missing_events`, `backfill`, `event`, `state`, `state_ids`), rejecting invites through the inviting server (`make_leave`/`send_leave` both ways), joining by remote alias (`query/directory` both ways), creating version 12 rooms, presence between servers, `Server:TrustedProxies` for `X-Forwarded-For` behind a reverse proxy, the outgoing federation queue and the sender's position kept across restarts, media of other servers (fetched through the federation media API and kept) and serving ours to them, remote signing keys kept in the database with key servers (`Federation:TrustedKeyServers`, and `/_matrix/key/v2/query` served), room summaries, space hierarchies (also over federation), relations, `timestamp_to_event`, reports (logged), filling gaps with `get_missing_events`, public room directories of other servers (`publicRooms` both ways).

Next:
- Make version 12 the default for new rooms once our clients send `via` servers when joining by room ID (eight server tests join by ID alone).
- SRV lookup (`_matrix-fed._tcp`), which needs a small DNS client.
- Checking received events against the state before them, auth chains with the auth rules, state resolution v2; gaps of more than 20 missing events.
- Sliding sync: per-list timeline limits, left rooms, more list filters, keeping connections across restarts.
- A review list for reports, which are only logged now; hierarchies and summaries of rooms on other servers.
- Room ACLs, threads.
- Element X calls through Element Call against a LiveKit server.

Keep the test server's registration closed and rate limits on, since unfinished software will be reachable from the internet.

# Voice calls

- Voice and video calls in the Avalonia client, and in the server:
  - 1:1 calls: `m.call.*` events, `/voip/turnServer` on the server, and a TURN server such as coturn.
  - Group calls: MatrixRTC with a LiveKit SFU, announced in `.well-known`.

# UserManagement 

- Separate user management out into Uniform.UserManagement that is agnostic of Uniform.Matrix.*
- Create Uniform.UserManagement.Gui.Terminal
- Have Matrix server include Uniform.UserManagement.Gui.Terminal

# Uniform.Matrix.Client.Desktop

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

F1 could open the user documentations in system browser for desktop clients

