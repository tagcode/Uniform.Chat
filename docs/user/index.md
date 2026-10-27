# Matrix Client and Server

Chat clients and a homeserver for [Matrix](https://matrix.org), the open chat protocol.

The terminal client runs in a text terminal with menus, a room list, the message log and members, an input line for messages and irssi-style `/commands`, and a status bar.

The desktop and Android app has the same engine in a graphical window: rooms, formatted messages, replies and reactions, files, invites, and encryption with recovery and emoji verification.

The server runs with a dashboard in the terminal, or without one as a service. Accounts, rooms, messages, sync, profiles, typing, receipts, the room directory, files and end-to-end encryption work; federation is still to come.

> [!NOTE]
> The clients are in early development. The terminal client supports end-to-end encrypted rooms and files, key backup with a recovery key, and emoji verification of devices.

- [Getting started](articles/getting-started.md): build the client and log in.
- [Interface](articles/interface.md): menus, keys and commands.
- [Desktop and Android app](articles/desktop.md): install, sign in, rooms and files.
- [Running the server](articles/server.md): start the homeserver with or without its dashboard, and create accounts.
