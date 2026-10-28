---
title: Installing
uid: Uniform.Matrix.Server.Host.Install.Article
---
# Installing

The server is a Matrix homeserver: it keeps accounts, rooms and messages, and clients such as the Uniform.Matrix clients or Element connect to it. One program runs it either with a dashboard in the terminal, or without one as a background service.

> [!NOTE]
> The server is in early development. Accounts, rooms, messages, room state and sync work, as do profiles, presence, typing, read receipts, unread counts, push rules and push notifications through push gateways, room tags, previews of linked pages (when turned on), room aliases, the public room directory, user search, files and images with thumbnails, and end-to-end encryption with cross-signing, emoji verification and key backup. Federation with other servers is being built and is off by default: with `Federation:Enabled`, users can look up profiles on other servers, join their rooms and talk there, and browse their public room directories, and other servers' users can join rooms here.

## Requirements

- Linux or Windows; for the dashboard, a terminal that supports UTF-8 and colors

## Install

Download `uniform-matrix-server-<version>-linux-x64.tar.gz` (or `linux-arm64`, or `win-x64.zip` and `win-arm64.zip` for Windows) from the [releases](https://github.com/tagcode/Uniform.Matrix/releases) and unpack it anywhere. It needs nothing else installed. The `docs` folder in it has this guide; open `docs/index.html` in a browser.

To build from source instead, install the [.NET 10 SDK](https://dotnet.microsoft.com/download); then use `dotnet run --project src/Uniform.Matrix.Server.Host --` from the repository root in place of the program in the examples below.
