# Getting started

## Requirements

- Linux with a terminal that supports UTF-8
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A terminal with UTF-8 and colors: GNOME Terminal, Konsole, kitty, xterm, tmux, Windows Terminal, macOS Terminal
- An account on a Matrix homeserver, for example `@alice:matrix.org`

## Build

From the repository root:

```bash
dotnet build
```

## Run

Start the client with your full Matrix user ID:

```bash
dotnet run --project src/Durum.Matrix.Client.Terminal -- --Matrix:UserId=@alice:matrix.org
```

If your server has no discovery file, for example a server of your own on this computer, also give its address: `--Matrix:Homeserver=http://localhost:8008`.

On the first run it asks for your password, logs in and remembers the session, so later runs start without a password. Messages are kept in a local database under `~/.local/share/durum-matrix-client-terminal/`, and messages you write are kept there until the server has received them, so nothing is lost when the connection drops.

The window has a menu bar, a room list, the messages of the selected room and its members. Type a message and press Enter to send it; see [Interface](interface.md) for menus, keys and `/commands`.

Settings can be changed in File → Settings, which saves them to `~/.config/durum-matrix-client-terminal/settings.json`; see [Settings](interface.md#settings). They can also go in `appsettings.json` next to the program, for example `"Matrix": { "UserId": "@alice:matrix.org" }`. Command-line options override both.

Each run writes its own log under `~/.local/share/durum-matrix-client-terminal/logs/`; View → Logs shows them.

To log out on the server, choose File → Log out. Quitting with Ctrl+Q keeps the session.

## Troubleshooting

| Message | Meaning |
|---|---|
| `M_FORBIDDEN: Invalid username or password` | Wrong user ID or password. |
| `No valid m.homeserver.base_url for ...` | The server's discovery file is broken; contact its administrator. |
| `M_LIMIT_EXCEEDED ... (retry after N s)` | The server limits logins; wait N seconds. |
