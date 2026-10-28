---
title: Getting started
uid: Uniform.Matrix.Client.Terminal.GettingStarted.Article
---
# Getting started

## Requirements

- A terminal with UTF-8 and colors: GNOME Terminal, Konsole, kitty, xterm, tmux, Windows Terminal, macOS Terminal
- An account on a Matrix homeserver, for example `@alice:matrix.org`

## Install

Download `uniform-matrix-terminal-<version>-linux-x64.tar.gz` (or `linux-arm64`, or `win-x64.zip` and `win-arm64.zip` for Windows) from the [releases](https://github.com/tagcode/Uniform.Matrix/releases) and unpack it anywhere. It needs nothing else installed. The `docs` folder in it has this guide; open `docs/index.html` in a browser.

To build from source instead, install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run `dotnet build` in the repository root; then use `dotnet run --project src/Uniform.Matrix.Client.Terminal --` in place of the program below.

## Run

Start the client; on Windows, run `Uniform.Matrix.Client.Terminal.exe`:

```bash
./Uniform.Matrix.Client.Terminal
```

The first time, it asks for your full Matrix user ID, such as `@alice:matrix.org`. Give the server address too only when the server cannot be found from the user ID, for example `http://localhost:8008` for a server of your own on this computer. Then it asks for your password and logs in. After a successful login it saves the user ID and address to your settings, and remembers the session, so later runs start straight away without asking.

You can also give them on the command line, which skips the question: `--Matrix:UserId=@alice:matrix.org`, and `--Matrix:Homeserver=http://localhost:8008` when needed. Without a terminal to ask in, for example with input redirected, the client prints this help instead. Messages are kept in a local database under `~/.local/share/uniform-matrix-client-terminal/`, and messages you write are kept there until the server has received them, so nothing is lost when the connection drops.

The window has a menu bar, a room list, the messages of the selected room and its members. Type a message and press Enter to send it; see [Interface](xref:Uniform.Matrix.Client.Terminal.Interface.Article) for menus, keys and `/commands`.

Settings can be changed in File → Settings, which saves them to `~/.config/uniform-matrix-client-terminal/settings.json`; see [Settings](xref:Uniform.Matrix.Client.Terminal.Settings.Article), which also lists every command-line parameter. They can also go in `appsettings.json` next to the program, for example `"Matrix": { "UserId": "@alice:matrix.org" }`. Command-line options override both.

Each run writes its own log under `~/.local/share/uniform-matrix-client-terminal/logs/`; View → Logs shows them.

To log out on the server, choose File → Log out. Quitting with Ctrl+Q keeps the session.

## Troubleshooting

| Message | Meaning |
|---|---|
| `M_FORBIDDEN: Invalid username or password` | Wrong user ID or password. |
| `No valid m.homeserver.base_url for ...` | The server's discovery file is broken; contact its administrator. |
| `M_LIMIT_EXCEEDED ... (retry after N s)` | The server limits logins; wait N seconds. |
