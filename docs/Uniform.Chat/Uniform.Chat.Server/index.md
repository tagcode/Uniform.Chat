---
title: Uniform.Matrix.Server.Host
uid: Uniform.Matrix.Server.Host.Article
---
# Uniform.Matrix.Server.Host

The server application: it reads settings with [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), writes one log file per session, adds the engine of [Uniform.Matrix.Server](xref:Uniform.Matrix.Server.Article), and runs with the Terminal.Gui dashboard or without it (`Host:Ui`, see [`ServerHostOptions`](xref:Uniform.Matrix.Server.Host.ServerHostOptions)). The dashboard has the settings, users, rooms and logs windows; user management goes through [`AdminService`](xref:Uniform.Matrix.Server.Accounts.AdminService), which logs each change at Information level.

Start-up errors that people can fix themselves are shown in two lines instead of a stack trace: a listen address that is already in use, with a hint about `--Urls`, and a TLS certificate that cannot be read, naming the setting and path. The full exception stays in the log file.

How to run it, as a service too, is in the server's user guide.
