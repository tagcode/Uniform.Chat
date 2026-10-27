# Server

The homeserver is three projects:

| Project | Role |
|---|---|
| `Uniform.Matrix.Protocol.Server` | Pure protocol code, no storage: [room versions](https://spec.matrix.org/latest/rooms/) 10 and 11, the event format, content hashes, reference-hash event IDs, signing, redaction and the [authorization rules](https://spec.matrix.org/latest/rooms/v11/#authorization-rules). |
| `Uniform.Matrix.Server` | The engine: `builder.AddMatrixServer()` registers options, the database and services; `app.MapMatrixServer()` adds the middleware and maps the endpoints. |
| `Uniform.Matrix.Server.Host` | The application: reads settings, sets up logging, and runs with the Terminal.Gui dashboard or without it (`Host:Ui`). |

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddSettings();
builder.AddMatrixServer();
WebApplication app = builder.Build();
app.MapMatrixServer();
await app.RunAsync();
```

## Requests

ASP.NET Core minimal APIs, one `Map…` extension per spec area in `Endpoints/`. Each endpoint reads the request, calls a service and returns JSON.

- `MatrixHttp.HandleAsync` wraps every request: CORS headers, and [standard errors](https://spec.matrix.org/latest/client-server-api/#standard-error-response) from exceptions. A `MatrixException` becomes its status and `errcode`; `MatrixErrorException` adds fields such as `current_version`; `AuthenticationRequiredException` answers 401 with the [user-interactive auth](https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api) flows; bad JSON becomes `M_BAD_JSON` or `M_NOT_JSON`; unknown paths `M_UNRECOGNIZED`.
- `RequireAccessToken()` on a route group checks the token from the `Authorization` header (or the deprecated `access_token` query parameter); handlers get the user with `http.GetRequester()`, or `http.GetOwnRequester(userId)` for endpoints that take a user ID and must refuse other users.
- Access tokens are stored as SHA-256 hashes, so a leaked database does not leak sessions.

## Storage

EF Core on SQLite, `ServerDbContext` with entities in `Storage/Entities.cs`. `DatabaseMigrator` applies migrations at start, before the server answers. Each schema change adds a migration:

```bash
dotnet tool run dotnet-ef migrations add <Name> --project src/Uniform.Matrix.Server --output-dir Storage/Migrations
```

Services take an `IDbContextFactory<ServerDbContext>` and use a short-lived context per operation; they are singletons.

## Events and state

`EventSender` turns an `EventDraft` into a PDU: it picks the auth events, `prev_events` (the room's forward extremities) and depth, hashes and signs it with the server's key (`ServerKeys`, in `signing.key`), computes the event ID, checks the authorization rules against the current state, and stores the event, its graph edges and the new state.

Room state is stored as state groups: each state event makes a new group that records only its change against the previous group, with a full snapshot every 50 changes, so reading the state at any event is a short chain. `StateCache` keeps loaded groups, which never change. `CurrentState` has one row per type and state key for fast reads, with the membership of member events, so a user's rooms are one query.

`ClientFormat` turns a stored PDU into the [client format](https://spec.matrix.org/latest/client-server-api/#room-event-format): no graph fields, `unsigned` with `age`, the transaction ID for the sending device, `redacted_because` and `prev_content`.

## Sync and the stream

`ServerStream` is one counter for every change a sync must notice: events, account data, receipts, typing, device list changes and to-device messages. `WriteAsync(position => …)` runs a change with the next position, one at a time, so positions commit in order, and wakes waiting syncs. Sync tokens are `s` and a position; `/messages` and `prev_batch` use the same tokens. Changes that are not stored, such as typing, still take a position, and the counter itself is stored, so positions are never reused after a restart.

`SyncService` builds a response for the positions after the token: joined rooms with their timeline, state and summary, invites with stripped state, left rooms, account data, receipts and typing, to-device messages, device list changes and key counts. Incremental syncs long-poll with `ServerStream.WaitPastAsync` until something reaches the user. `Visibility` applies [history visibility](https://spec.matrix.org/latest/client-server-api/#history-visibility) to every event a user is shown, and `SyncFilter` the parts of [filters](https://spec.matrix.org/latest/client-server-api/#filtering) the server applies.

## Services

| Folder | Services |
|---|---|
| `Accounts/` | `AccountService` (register, log in, devices, passwords), `UserInteractiveAuth`, `AccountDataService` (account data and tags), `PushRuleService` (push rules and unread counts), `PusherService` and `PushNotifier` (pushers and push gateways), `ProfileService`, `PresenceService`, `AdminService` (the dashboard's user and room windows) |
| `Rooms/` | `RoomService` (create, join, invite, leave, state, redact, read), `EventSender`, `ReceiptService`, `TypingService` (in memory), `DirectoryService` (aliases, public rooms, user search) |
| `Sync/` | `SyncService`, `ServerStream`, `SyncFilter`, `Visibility` |
| `Encryption/` | `DeviceKeyService` (device, one-time, fallback and cross-signing keys, signatures), `ToDeviceService` (to-device messages and device lists), `KeyBackupService` |
| `Media/` | `UrlPreviewService` (page previews), `MediaService` (files under `media/` in the data directory; media reserved with `/media/v1/create` is a row with an empty content type until its file arrives, and downloads wait for it up to `timeout_ms`), `Thumbnailer` |

## Push rules and unread counts

Each user's [push rules](https://spec.matrix.org/latest/client-server-api/#push-rules) are stored as the `m.push_rules` account data that clients sync, and changed through `/pushrules`. `PushRuleService` merges them with `PushRules.Defaults` (in `Uniform.Matrix.Protocol.Server`) on every read: a stored default rule keeps only its `enabled` and `actions`, so changes to the defaults reach users who customised their rules. User rules come before the defaults of their kind, except that `.m.rule.master` stays first. Initial syncs always carry `m.push_rules`, also for users who never changed a rule.

`notification_count` and `highlight_count` in sync are worked out when the sync is built, not stored per event: the room's events by others after the user's read receipt (or their own latest event, whichever is later) go through `PushRules.Evaluate`, newest first and at most `PushRuleService.MaxCounted` of them. The evaluator implements the condition kinds of the spec (`event_match` with globs and word boundaries for `content.body`, `event_property_is`, `event_property_contains`, `contains_display_name`, `room_member_count`, `sender_notification_permission`) and skips the deprecated name-based mention rules for events with `m.mentions`. Encrypted messages count through `.m.rule.encrypted`, but the server cannot see mentions inside them; clients highlight those themselves.

## Pushers

[Pushers](https://spec.matrix.org/latest/client-server-api/#push-notifications) say where a user's notifications go, usually a push gateway that wakes a phone app. `PusherService` keeps them in the `Pushers` table; each belongs to the device that set it and goes when that device logs out. `PushNotifier` is a background service that follows `ServerStream`: for each new event it runs the push rules of the room's joined members (and of the invitee of an invite) with `PushRuleService.DecideAsync`, and posts a notification in the [push gateway API](https://spec.matrix.org/latest/push-gateway-api/) format to each of their http pushers, with the tweaks of the deciding rule and the user's unread count across rooms. The `event_id_only` format leaves out the content and names. Push keys the gateway rejects are deleted; failing gateways are logged and skipped. Events from before the server started are not pushed.

Gateway URLs come from users, so the gateway client (`PushNotifier.CreateHandler`) connects only to public addresses unless `Push:AllowPrivateGateways` is on. It checks the very addresses it connects to, in `SocketsHttpHandler.ConnectCallback`, so a host name cannot resolve to a public address for a check and a private one for the request.

## URL previews

`UrlPreviewService` answers `preview_url` when `UrlPreviews:Enabled` is on: it fetches the page, reads its OpenGraph `og:` tags with AngleSharp (the page's `<title>` and description when there are none), fetches `og:image` and stores it as media of the user who asked, and returns it as an `mxc://` URI with `matrix:image:size`. A link to an image previews as that image. Pages and images are read up to `UrlPreviews:MaxSize` within `UrlPreviews:Timeout`, and results are cached per address for `UrlPreviews:CacheDuration`. The HTTP client is the same kind as for push gateways (`PublicAddresses.CreateHandler`): only public addresses, checked on every connection including redirects, unless `UrlPreviews:AllowPrivateAddresses` is on.

## Rate limits

`RateLimits.AddMatrixRateLimits` (in `Http/`) sets up ASP.NET Core's rate limiter with two token-bucket policies, which endpoints opt into with `RequireRateLimiting`: `RateLimits.Writes` per access token, on room creation, invites, sending, state, redactions and uploads; and `RateLimits.Logins` per client address, on `/login` and `/register`. A refused request gets 429 with `M_LIMIT_EXCEEDED`, `retry_after_ms` and a `Retry-After` header; `MatrixClient` waits that long and retries. The `RateLimits:*` settings are read at startup. Reads and sync are not limited. Client addresses come from the connection, as forwarded headers are not read yet, so behind a reverse proxy the login limits are shared by all users.

## Presence

`PresenceService` keeps presence in memory, like typing: each change takes a `ServerStream` position, and sync sends `m.presence` for the user and everyone sharing a joined room with them. A sync marks the user online, or idle with `set_presence=unavailable`; `set_presence=offline` changes nothing. A state chosen with the presence API, such as `unavailable` or `offline` to appear away, is not changed by syncing. A timer takes users offline once they have not synced for `PresenceService.OfflineAfter` (2 minutes), which wakes the syncs of the people who see them. Presence is lost on restart.

The server never sees private keys. It checks that uploaded device keys, cross-signing keys and signatures are signed as the spec requires, hands out each one-time key once, and stores encrypted backups as they come.

## Federation

Federation is being built, behind `Federation:Enabled`. In `Federation/`:

- `ServerResolver` finds other servers as the spec's [server name resolution](https://spec.matrix.org/latest/server-server-api/#resolving-server-names) says: IP literals and names with a port directly, otherwise `/.well-known/matrix/server` delegation, else port 8448. SRV records are not looked up yet.
- `FederationClient` sends requests signed with `FederationAuth` (in `Uniform.Matrix.Protocol.Server`) as an `X-Matrix` authorization, through the same public-addresses-only handler as push gateways and URL previews.
- `RemoteServerKeys` fetches other servers' keys from their `/_matrix/key/v2/server`, accepts them only when every listed key signed the response, and caches them in memory until `valid_until_ts`, at most a week.
- `ServerAuthentication` is middleware on endpoints marked `RequireFederation()`: it buffers the body, checks the signature over the raw request target, and gives the endpoint the origin with `GetOrigin()`.

`FederationEndpoints` serves `/_matrix/key/v2/server`, `/_matrix/federation/v1/version`, `query/profile`, `make_join`, `send_join` (v2) and `send`, and `ProfileService` asks other servers for their users' profiles.

Joins: `RoomService.JoinAsync` sends joins of room IDs of other servers that are not here to `RemoteJoins`, which asks the room's server for a `make_join` template, signs it, sends it with `send_join`, checks the content hash and the sender server's signature of every returned event, and stores the room with `EventSender.StoreJoinedRoomAsync`. The returned state and auth chain get negative stream positions, so they sort as history before the join and never count as new. On the resident side `EventSender.MakeJoinAsync` builds the template and `AcceptJoinAsync` checks the signed join (its ID, hash, origin and the auth rules) before storing it like a local event. Auth chains are not yet checked against the auth rules.

Transactions: `/send` hands each PDU to `EventSender.AcceptEventAsync`, which checks its hash, the sender server's signature and the auth rules against the current state (not yet the state before the event, which matters once rooms fork), and stores it; an event replaces only the forward extremities it builds on. `FederationSender` follows `ServerStream` and sends local users' events, and remote joins this server accepted, to the other servers with joined members, one transaction per event and server, without retries yet. EDUs (typing, receipts, presence) are not sent or read.

Room version 12 (`RoomVersion.V12`, `RoomIdIsCreateHash`): the room ID is `!` and the create event's hash, the create event has no `room_id` and is left out of `auth_events`, and the creators (the sender and `additional_creators`, see `AuthRules.Creators`) have unlimited power and cannot be listed in power levels. Such rooms can be joined through `via` servers, since their IDs name no server; creating them here is refused until room creation builds the create event before the room's row. `FederationTests` run two servers in memory whose federation clients reach each other through a test handler.

HTTPS comes from `TlsCertificate` (in `Http/`), which reads the PEM files of `Tls:*`, sends the intermediates with the certificate (other servers check the chain) and reads the files again within a minute of a renewal.

## Testing

`Uniform.Matrix.Server.Tests` start the whole server in memory with `TestHomeserver` (ASP.NET Core's test server, a temporary data directory) and drive it with our own `MatrixClient`. Server logs go to the test output. `EncryptionTests` run two of our `OlmMachine`s against it: messages between users, recovery from backup, key forwarding and emoji verification.

```csharp
await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
MatrixClient alice = server.CreateClient();
await alice.RegisterAsync("alice", "password");
string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" });
```

## Not yet covered

Most of federation (joins, events, state resolution, EDUs, remote media), email pushers (stored but not sent), room upgrades, threads and relations, search, sliding sync, and PostgreSQL.
