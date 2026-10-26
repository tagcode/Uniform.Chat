# Matrix Client and Server

A terminal chat client and a homeserver for [Matrix](https://matrix.org), the open chat protocol.

The client runs in a text terminal with menus, a room list, the message log and members, an input line for messages and irssi-style `/commands`, and a status bar.

The server runs with a dashboard in the terminal, or without one as a service. Accounts, rooms, messages, sync, profiles, typing, receipts, the room directory, files and end-to-end encryption work; federation is still to come.

> [!NOTE]
> The client is in early development. It supports end-to-end encrypted rooms and files, key backup with a recovery key, and emoji verification of devices.

- [Getting started](articles/getting-started.md): build the client and log in.
- [Interface](articles/interface.md): menus, keys and commands.
- [Running the server](articles/server.md): start the homeserver with or without its dashboard, and create accounts.
