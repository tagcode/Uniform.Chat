---
uid: Uniform.Stun.Server.Article
title: Uniform.Stun.Server
---
# Uniform.Stun.Server

The standalone STUN and TURN server, `uniform-stun-server`: a .NET host with [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), one log file per session and the console, and [`AddTurnServer`](xref:Uniform.Stun.Hosting.TurnHostingExtensions.AddTurnServer*). Its `appsettings.json` turns the server on. When it cannot start, it prints why and where the settings are, and exits with 1.

Its settings and data folders are named after the application, `~/.config/uniform-stun-server` and `~/.local/share/uniform-stun-server`. How to run it is in its user guide.
