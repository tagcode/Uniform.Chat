# Running the server

The server is a Matrix homeserver: it keeps accounts, rooms and messages, and clients such as this terminal client or Element connect to it. One program runs it either with a dashboard in the terminal, or without one as a background service.

> [!NOTE]
> The server is in early development. Accounts, rooms, messages, room state and sync work, as do profiles, typing, read receipts, room tags, room aliases, the public room directory, user search, files and images with thumbnails, and end-to-end encryption with cross-signing, emoji verification and key backup. Federation with other servers is not there yet, so only users of this server can talk to each other.

## Requirements

- Linux with a terminal that supports UTF-8 and colors, for the dashboard
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Run with the dashboard

From the repository root:

```bash
dotnet run --project src/Durum.Matrix.Server.Host
```

When started from a terminal, the server opens its dashboard and serves at `http://localhost:8008`:

```text
┌Matrix server localhost───────────────────────────────────────────────────────────┐
│ File  Server                                                                     │
│ Listening:  http://localhost:8008                                                │
│ Uptime:     0:12:31                                                              │
│ Requests:   1520 answered, 3 in progress, 2 failed                               │
│ Accounts:   4 users, 3 rooms                                                     │
│                                                                                  │
│ Data:       /home/alice/.local/share/durum-matrix-server-host                    │
│ Log:        /home/alice/.local/share/durum-matrix-server-host/logs/2026-10-26…   │
│                                                                                  │
│ F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q stops it.    │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Listening** shows the addresses clients connect to.
- **Requests** counts client requests: answered, being answered now (syncing clients wait here for news), and failed with a server error.
- **Accounts** counts users and rooms, every few seconds.
- **Data** is the folder with the database and signing key; see [Files](#files).
- **Log** is this session's log file.

The dashboard refreshes every second. The server keeps serving while menus and windows are open.

| Key | Action |
|---|---|
| F9 or Alt+F | Open the File menu |
| File → Settings… | View and change the server's settings |
| File → Logs… | Read this session's log and earlier ones |
| Server → Users… | Create users, reset passwords, give admin rights, deactivate accounts |
| Server → Rooms… | See rooms and list them in the public room directory |
| Ctrl+Q or File → Quit | Stop the server |

### Settings window

File → Settings is the same window as in the client (see [Settings](interface.md#settings)): it lists every option, its value and where the value comes from.

- **Apply** uses a change at once, until the server stops. Options marked ↻, such as the server name and the data folder, take effect only after a restart.
- **Save** writes the changes to `~/.config/durum-matrix-server-host/settings.json`, which is read at every start.
- Registration tokens are secret: they are never shown in the window.

To open registration for a while, for example: select `Server:Registration`, choose `Open` and Apply. New users can register at once. Set it back to `Disabled` and Apply when they are done. Nothing is saved unless you choose Save.

### Users window

Server → Users lists every account with its display name, logged-in devices, joined rooms, and whether it is an admin or deactivated.

- **New** asks for the user name, such as `alice` for `@alice:localhost`, the password twice, and whether the user is a server admin. Registration does not need to be open for this.
- **Password** sets a new password for the selected user and logs out all their devices, for example when they forgot it.
- **Admin** gives or takes server admin rights. Admins may delete any room alias and change any room's directory listing.
- **Deactivate** logs out every device of the user and stops them logging in; pressed again, it reactivates the account. Their rooms and messages stay.

### Rooms window

Server → Rooms lists every room with its name, alias, joined members, and whether it is public (listed in the directory) or encrypted.

- **Publish** lists the selected room in the public room directory, or removes it. Listing a room does not change who may join; that follows the room's own join rule.
- Messages of encrypted rooms are encrypted on the members' devices, so the server cannot show or read them.

### Logs window

File → Logs shows the log of this session, and lets you open earlier sessions. Each start writes its own file in `logs` under the data folder. The newest 20 are kept.

## Run without the dashboard

Without a terminal, for example as a service, the server runs without the dashboard and also writes its log to standard output:

```bash
dotnet run --project src/Durum.Matrix.Server.Host -- --Host:Ui=None
```

`Host:Ui` chooses the mode:

| Value | Mode |
|---|---|
| `Auto` (default) | The dashboard when input and output are a terminal; none when either is redirected, as under systemd or with `> server.log` |
| `Dashboard` | Dashboard for monitoring server status and for perusing logs. |
| `None` | No dashboard. |

Stop the server with Ctrl+C, or with `SIGTERM` from a service manager.

### As a systemd service

Publish the server once, then run the published program from a unit file:

```bash
dotnet publish src/Durum.Matrix.Server.Host -c Release -o ~/matrix-server
```

```ini
# ~/.config/systemd/user/matrix-server.service
[Unit]
Description=Matrix homeserver

[Service]
ExecStart=%h/matrix-server/Durum.Matrix.Server.Host
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now matrix-server
journalctl --user -u matrix-server -f
```

systemd connects the server's input to `/dev/null` and its output to the journal, so `Auto` runs without the dashboard there and the log goes to the journal.

## Settings

Settings come from these places; later ones override earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/durum-matrix-server-host/settings.json`, written by the settings window
3. Environment variables, with `__` between parts: `Server__Registration=Open`
4. The command line: `--Server:Registration=Open`
5. Changes applied in the settings window, until the server stops

| Option | Default | Meaning |
|---|---|---|
| `Urls` | `http://localhost:8008` | Addresses to listen on, separated by `;`. ↻ |
| `Server:ServerName` | `localhost` | The name in user IDs and room aliases, such as `example.org` in `@alice:example.org`. Choose it before creating accounts: changing it later leaves every existing account and room behind. ↻ |
| `Server:PublicBaseUrl` | not set | The address clients should use, such as `https://matrix.example.org`. When set, the server announces it at `/.well-known/matrix/client`. |
| `Server:Registration` | `Disabled` | Who can create accounts: `Disabled`, `Open` (anyone), or `Token` (anyone with a registration token). |
| `Server:RegistrationTokens` | not set | Tokens for `Token` registration, separated by commas. |
| `Media:MaxUploadSize` | `52428800` | Largest file users can upload, in bytes (50 MiB). |
| `Storage:DataDirectory` | `~/.local/share/durum-matrix-server-host` | Folder for the database, signing key, media and logs. ↻ |
| `Storage:LogFilesKept` | `20` | How many session logs to keep. ↻ |
| `Host:Ui` | `Auto` | `Auto`, `Dashboard` or `None`, see [Run without the dashboard](#run-without-the-dashboard). ↻ |

↻ marks options that take effect after a restart.

> [!WARNING]
> The server speaks plain HTTP. Keep it on `localhost`, or put a reverse proxy such as Caddy or nginx in front of it for HTTPS before letting other computers connect. Listening on all interfaces (`--Urls=http://0.0.0.0:8008`) without one sends passwords unencrypted.

## Accounts

The server starts with registration disabled, so nobody can create an account by themselves. The simplest way to add people is the dashboard: Server → Users → New (see [Users window](#users-window)).

To let people register in their own client instead:

1. Start the server with registration open, or with tokens:

   ```bash
   dotnet run --project src/Durum.Matrix.Server.Host -- --Server:Registration=Open
   dotnet run --project src/Durum.Matrix.Server.Host -- --Server:Registration=Token --Server:RegistrationTokens=let-me-in
   ```

   Or change `Server:Registration` in the settings window and Apply, as above.
2. Register in a client that lets you type the homeserver address, such as Element Desktop: choose "Create account", edit the homeserver, and enter `http://localhost:8008`. With `Token`, the client asks for the token.
3. Close registration again when you are done.

Registration also works from the command line:

```bash
curl -X POST http://localhost:8008/_matrix/client/v3/register \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"choose-a-password","auth":{"type":"m.login.dummy"}}'
```

For `Token` registration, use `"auth":{"type":"m.login.registration_token","token":"let-me-in"}`.

The new user is `@alice:localhost`, or `@alice:` followed by the configured server name. Their display name starts out as their user name.

## Connecting clients

Log in with the full user ID and the homeserver address `http://localhost:8008`. In Element, edit the homeserver on the sign-in page. In this terminal client, give the address with `--Matrix:Homeserver`:

```bash
dotnet run --project src/Durum.Matrix.Client.Terminal -- --Matrix:UserId=@alice:localhost --Matrix:Homeserver=http://localhost:8008
```

Without an address, clients find the server from the user ID: they look for `https://<server name>/.well-known/matrix/client`. That needs a real server name, HTTPS and `Server:PublicBaseUrl`.

## Files

Everything the server keeps is in the data folder, `~/.local/share/durum-matrix-server-host` by default (under `$XDG_DATA_HOME` when set):

| File | Contents |
|---|---|
| `server.db` | The SQLite database: accounts, devices, rooms and messages. The server updates it to its current layout at start. |
| `media/` | Uploaded files, and the thumbnails made from them in `media/thumbnails/`. |
| `signing.key` | The server's private signing key, which signs every event it creates. It is created when first needed and readable only by your user. |
| `logs/` | One log file per session. |

To back up the server, stop it and copy the whole folder. Keep `signing.key` with the database and keep it private: it is the server's identity.

## Troubleshooting

| Message | Meaning |
|---|---|
| `Failed to bind to address http://127.0.0.1:8008: address already in use` | Another program, perhaps another copy of the server, uses the port. Stop it, or choose another port with `--Urls=http://localhost:8009`. |
| `M_FORBIDDEN: Registration is disabled.` | Set `Server:Registration` to `Open` or `Token`. |
| `M_USER_IN_USE` | The user name is taken; choose another. |
| `... is not an ed25519 signing key.` | `signing.key` was changed or damaged. Restore it from a backup. |
| `Running without dashboard: input is redirected; --Host:Ui=Dashboard forces it` | The server was started without a terminal, for example from an IDE's run button or with redirected input or output. Run it from a terminal, or use `--Host:Ui=Dashboard` to force the dashboard. |
