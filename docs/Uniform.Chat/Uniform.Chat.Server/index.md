---
title: Uniform.Chat.Server
uid: Uniform.Chat.Server.Article
---
# Uniform.Chat.Server

The server application: it reads settings with [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), writes one log file per session, adds the engine of [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Article), and runs with the Terminal.Gui dashboard or without it (`Host:Ui`, see [`ServerHostOptions`](xref:Uniform.Chat.Server.ServerHostOptions)). The dashboard has the settings, users, rooms and logs windows; user management goes through [`AdminService`](xref:Uniform.Matrix.Server.Accounts.AdminService), which logs each change at Information level.

The program file is `uniform-chat-server` (`uniform-chat-server.exe` on Windows), as the system service is named. `Program` sets the application name `uniform-chat-server`, the same as the assembly's, as ASP.NET loads the assembly of that name at start; it names the settings and data folders, e.g. `~/.config/uniform-chat-server` and `~/.local/share/uniform-chat-server`. Before the name Uniform Chat they were `uniform-matrix-server-host`; the server does not move them, the user guide's Updating article does.

Start-up errors that people can fix themselves are shown in two lines instead of a stack trace: a listen address that is already in use, with a hint about `--Urls`, and a TLS certificate that cannot be read, naming the setting and path. The full exception stays in the log file.

With `Turn:Enabled`, `EmbeddedTurn` adds the STUN and TURN server of [Uniform.Stun.Hosting](xref:Uniform.Stun.Hosting.Article) and connects it to the homeserver's calls.

With `LiveKit:Enabled`, `EmbeddedLiveKit` serves the MatrixRTC authorization service of [Uniform.LiveKit.Hosting](xref:Uniform.LiveKit.Hosting.Article) at `/livekit/jwt`. It checks the homeserver's own OpenID tokens in its store, without a round trip, and those of other servers over federation. Its `IRoomMembership` gives tokens only to users joined to the call's room, by the room's current state on this homeserver, which also holds remote users of the rooms it is in. Unless they are set, `LiveKit:FullAccessHomeservers` becomes the server name, and `Calls:LiveKitServiceUrl` becomes the service at `Server:PublicBaseUrl`.

How to run it, as a service too, is in the server's user guide.
