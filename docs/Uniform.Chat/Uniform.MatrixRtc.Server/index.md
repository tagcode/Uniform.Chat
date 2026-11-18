---
uid: Uniform.MatrixRtc.Server.Article
title: Uniform.MatrixRtc.Server
---
# Uniform.MatrixRtc.Server

The standalone MatrixRTC authorization service, `uniform-matrixrtc-server`, a drop-in replacement for lk-jwt-service in front of a LiveKit server. It is an ASP.NET host with [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), one log file per session and the console, and [`AddLiveKitAuthorization`](xref:Uniform.MatrixRtc.Hosting.LiveKitHostingExtensions.AddLiveKitAuthorization*). It serves the endpoints at the root, at port 8080, as lk-jwt-service does.

It also reads lk-jwt-service's environment variables (`LIVEKIT_URL`, `LIVEKIT_KEY`, `LIVEKIT_SECRET`, their `_FILE` forms, `LIVEKIT_FULL_ACCESS_HOMESERVERS` and `LIVEKIT_JWT_BIND`), so an existing container setup keeps working. They rank above the settings file and below the program's own environment variables and command line. Without LiveKit's URL, key, secret or full-access homeservers, it prints what is missing and exits with 1.

Its settings and data folders are `~/.config/uniform-matrixrtc-server` and `~/.local/share/uniform-matrixrtc-server`. The LiveKit server itself is not part of it. How to run it is in its user guide.
