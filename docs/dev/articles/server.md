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
| `Accounts/` | `AccountService` (register, log in, devices, passwords), `UserInteractiveAuth`, `AccountDataService` (account data and tags), `ProfileService`, `PresenceService`, `AdminService` (the dashboard's user and room windows) |
| `Rooms/` | `RoomService` (create, join, invite, leave, state, redact, read), `EventSender`, `ReceiptService`, `TypingService` (in memory), `DirectoryService` (aliases, public rooms, user search) |
| `Sync/` | `SyncService`, `ServerStream`, `SyncFilter`, `Visibility` |
| `Encryption/` | `DeviceKeyService` (device, one-time, fallback and cross-signing keys, signatures), `ToDeviceService` (to-device messages and device lists), `KeyBackupService` |
| `Media/` | `MediaService` (files under `media/` in the data directory), `Thumbnailer` |

The server never sees private keys. It checks that uploaded device keys, cross-signing keys and signatures are signed as the spec requires, hands out each one-time key once, and stores encrypted backups as they come.

## Testing

`Uniform.Matrix.Server.Tests` start the whole server in memory with `TestHomeserver` (ASP.NET Core's test server, a temporary data directory) and drive it with our own `MatrixClient`. Server logs go to the test output. `EncryptionTests` run two of our `OlmMachine`s against it: messages between users, recovery from backup, key forwarding and emoji verification.

```csharp
await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
MatrixClient alice = server.CreateClient();
await alice.RegisterAsync("alice", "password");
string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" });
```

## Not yet covered

Federation, presence in sync, push rules and notifications, room upgrades, threads and relations, search, URL previews, rate limits, sliding sync, and PostgreSQL.
