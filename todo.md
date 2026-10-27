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
- Parsing matrix.to links and `matrix:` URIs into IDs.
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
A public test name, ports and a certificate are available (kept out of the repo). Done: HTTPS from certbot files (`Tls:*`), signing and checking `X-Matrix` requests, server name resolution (IP, port, well-known), the key server, remote key fetching, `query/profile` and remote profiles, joining remote rooms by room ID (`make_join`/`send_join` both ways), messages both ways (`/send` and a background sender), joining version 12 rooms through `via` servers, inviting other servers' users (`invite` v2, signed by both servers), the `r0` and unstable key backup paths of older clients, all behind `Federation:Enabled`. Voice and video calls work between users of this server in SchildiChat Legacy, through TURN. Tested against a real Synapse: joins of version 11 and 12 rooms, and messages both ways.

Next:
- Store remote keys in the database, old keys too, and ask notary servers (`/_matrix/key/v2/query`) for keys of servers that are down; serve `/_matrix/key/v2/query` ourselves.
- SRV lookup (`_matrix-fed._tcp`), which needs a small DNS client.
- Creating version 12 rooms here (joining them works): build the create event before the room's row, as the room ID is its hash; then make 12 the default. Server-side power checks outside the auth rules (e.g. `CanSendStateAsync`) should count version 12 creators too.
- Retrying transactions to servers that are down, with backoff; EDUs (typing, receipts, presence, device lists, to-device).
- Checking received auth chains with the auth rules, state resolution v2, backfill, `get_missing_events`.
- Receiving invites from other servers: store the invite and its stripped state for a room not here yet, and show it in sync. Then an in-memory test of inviting across servers.
- Joining by remote alias (`query/directory`), leaving (`make_leave`/`send_leave`).
- Sliding sync (MSC4186), so Element X and SchildiChat Next can log in.
- EDUs: typing, receipts, presence, device lists and to-device messages.
- Remote media, directory queries, room ACLs, `/_matrix/federation/v1/publicRooms`.
- A real test against Synapse and the federation tester.

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

# Sliding sync 
- Sliding sync, so Element X and SchildiChat Next can log in to Edict.