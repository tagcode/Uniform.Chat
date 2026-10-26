# Uniform.Matrix.Client

## Authentication
Registration, and the multi-step "user-interactive auth" that deleting a device or changing a password also needs.
SSO and OAuth 2.0/OIDC login. Matrix is moving towards OIDC; matrix.org already uses it, so password login won't work everywhere.
Refresh tokens, "logout everywhere", and handling a soft logout when a token expires.
The homeserver URL the server can return at login is ignored.
## Rooms and events
Room management: creating and deleting aliases, knocking, room upgrades, server capabilities.
History and search: event context (messages around an event), message search, related events (/relations) and threads.
Spaces: the room hierarchy.
State types: typed content for power levels, join rules and history visibility.
Other: reporting content.
## Other areas
Media: uploading, downloading and thumbnails for mxc:// URIs.
Devices: listing, renaming and deleting devices.
Notifications: push rules, pushers and the notifications list.
Account: avatars, password change, deactivation, email and phone identifiers.
Calls: VoIP (TURN servers and m.call.* events).
Newer sync protocol: "simplified sliding sync" (MSC4186), the faster sync that newer clients use.
## Library quality
Typed RoomId, EventId, RoomAlias and mxc:// URI types; today these are plain strings.
Parsing matrix.to links and matrix: URIs.
Retrying network errors on ordinary requests; today only sync and rate-limited requests retry.
Making sure HttpClient.Timeout stays longer than the sync long-poll.
Logging, JSON source generation (for trimming and ahead-of-time compilation), and canonical JSON (needed later for encryption signing).

# Uniform.Matrix.Server

Layout: Uniform.Matrix.Protocol.Server (pure: room versions, event format, hashing, signing, redaction, auth rules, state resolution v2, federation client), Uniform.Matrix.Server (engine: storage, services, endpoints, AddMatrixServer), and one server app that runs with or without the Terminal.Gui GUI (Host:Ui = Auto, Dashboard or None).
Dependencies (approved): ASP.NET Core, EF Core with SQLite and Npgsql, Microsoft.Extensions.Identity.Core, Microsoft.AspNetCore.Mvc.Testing, StbImageWriteSharp, DnsClient (federation), Testcontainers.PostgreSql. Complement for spec compliance.
Tests: our own MatrixClient against the server in memory. Live tests move from the live test accounts to the local server as features land; run them against Synapse now and then as the reference.

## Milestone 1: standalone homeserver
1. Skeleton: app with both modes, /versions, .well-known, CORS, Matrix errors, in-memory test host.
2. Accounts: registration (disabled, open, token), password login, whoami, logout, devices, user-interactive auth.
3. Rooms: event storage and hashing, createRoom, auth rules, messages and state, local membership, redaction.
4. Sync: initial and incremental, long-polling, filters, timeline limits, /messages, /context, lazy-loaded members. (done; account data, to-device and device lists are empty until steps 5 and 7)
5. Typing, receipts, read markers, account data, tags, profiles, aliases, room directory, user search. (done; presence is stored but not sent in sync yet)
6. Media: upload, download, thumbnails (authenticated v1). (done; no legacy unauthenticated downloads, no async upload, no URL previews)
7. Encryption relay: keys, one-time and fallback keys, device lists, to-device, cross-signing, signatures, key backup. (done; the live encryption flows also run against the in-memory server in EncryptionTests)
8. Compatibility: live tests against our server and Synapse, Element Web smoke test, Complement client-server tests. (encryption flows run against our server in EncryptionTests; the terminal client connects with Matrix:Homeserver. Complement needs Go, and Docker access for the user: the docker group, or rootless Docker)
9. Admin TUI: dashboard, users, passwords, rooms. (done: Server → Users and Server → Rooms)


## Later
PostgreSQL in production, admin API, rate limits, URL previews, push gateway, sliding sync (MSC4186), room version 12, OpenTelemetry.

URL previews (not implemented yet)
- Server: GET /_matrix/client/v1/media/preview_url?url=...&ts=... (authenticated media).
  Fetch the page, parse OpenGraph (og:title, og:description, og:image) with AngleSharp,
  store og:image in the media repository and return it as an mxc:// URI. Cache results per URL.
- Server safety: refuse private/internal IP ranges (SSRF), limit size, time and redirects;
  configurable allow/deny lists, off by default like Synapse's url_preview_enabled.
- Client: show "[title — description]" and the og:image as a shade-block preview under
  messages with links; Ui:UrlPreviews setting; later off by default in encrypted rooms.
  Can already be tested against a Synapse server once url_preview_enabled is set.

## Milestone 2: Federation

A public name is only needed for federating with real servers, a Synapse test server or the open network. Synapse has to reach our server and validate its certificate. When we get there, I'd need:

a subdomain, e.g. durum.example.org, or whatever name you choose, pointing at the machine running the test server;
one TCP port forwarded through NAT and the firewall: 8448, the federation default, or 443 with .well-known/matrix/server delegation;
a TLS certificate, most easily from a reverse proxy like Caddy that gets Let's Encrypt certificates by itself. Kestrel can also do TLS with a certificate file.
I'd keep that server's registration closed and rate limits on, since unfinished software will be reachable from the internet. I'll ask when we're close to that point; it's a while away.

