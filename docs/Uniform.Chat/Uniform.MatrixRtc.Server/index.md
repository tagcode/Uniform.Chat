---
uid: Uniform.MatrixRtc.Server.Article
title: Uniform.MatrixRtc.Server
---
# Uniform.MatrixRtc.Server

The standalone MatrixRTC authorization service, `uniform-matrixrtc-server`, a replacement for lk-jwt-service in front of a LiveKit server, with the same endpoints. It is an ASP.NET host with [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), one log file per session and the console, and [`AddLiveKitAuthorization`](xref:Uniform.MatrixRtc.Hosting.MatrixRtcHostingExtensions.AddLiveKitAuthorization*). It serves the endpoints at the root, at port 8080, as lk-jwt-service does.

It also reads environment variables of its own (`MATRIXRTC_URL`, `MATRIXRTC_KEY`, `MATRIXRTC_SECRET`, their `_FILE` forms, `MATRIXRTC_FULL_ACCESS_HOMESERVERS` and `MATRIXRTC_JWT_BIND`), named like lk-jwt-service's `LIVEKIT_` ones, for container setups. They rank above the settings file and below the program's own environment variables and command line. Without LiveKit's URL, key, secret or full-access homeservers, it prints what is missing and exits with 1.

Its settings and data folders are `~/.config/uniform-matrixrtc-server` and `~/.local/share/uniform-matrixrtc-server`. The LiveKit server itself is not part of it. How to run it is in its user guide.
