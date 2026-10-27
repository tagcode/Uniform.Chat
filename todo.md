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
PostgreSQL in production, admin API, a push gateway of our own (for the Android app, with UnifiedPush or Firebase), sliding sync (MSC4186), room version 12, OpenTelemetry.

URL previews (the server side is done: `UrlPreviews:Enabled`, off by default):
- Server: allow and deny lists of hosts.
- Clients: show `[title — description]` and the `og:image` under messages with links, with `MatrixClient.GetUrlPreviewAsync`; a `Ui:UrlPreviews` setting, off by default in encrypted rooms.

## Milestone 2: federation
A public name is only needed for federating with real servers, a Synapse test server or the open network. Synapse has to reach our server and validate its certificate. When we get there, it needs:
- a subdomain, e.g. matrix.example.org, pointing at the machine running the test server;
- one TCP port forwarded through NAT and the firewall: 8448, the federation default, or 443 with `.well-known/matrix/server` delegation;
- a TLS certificate, most easily from a reverse proxy like Caddy that gets Let's Encrypt certificates by itself; Kestrel can also use a certificate file.

Keep that server's registration closed and rate limits on, since unfinished software will be reachable from the internet.

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
