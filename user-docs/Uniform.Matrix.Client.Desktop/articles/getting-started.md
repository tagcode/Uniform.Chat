# Getting started

The Uniform.Matrix app for Windows and Linux: rooms, messages, files and end-to-end encryption in a graphical window. In narrow windows it shows one pane at a time.

![The desktop app with a room open](../images/desktop.png)

> [!NOTE]
> The app is in early development. It covers everyday chatting and encryption; notifications on Windows are still to come.

## Requirements

- Windows 10 or later, or Linux
- An account on a Matrix homeserver, for example `@alice:matrix.org`

## Install

### Windows

Download `uniform-matrix-desktop-<version>-win-x64.zip` (or `win-arm64`) from the [releases](https://github.com/tagcode/Uniform.Matrix/releases) and unpack it anywhere; the `docs` folder in it has this guide (open `docs/index.html`). Then run `Uniform.Matrix.Client.Desktop.exe`. Windows may warn that the app is from an unknown publisher, because it is not signed yet; choose *More info → Run anyway*.

### Linux

Download `uniform-matrix-desktop-<version>-linux-x64.tar.gz` (or `linux-arm64`), unpack it and run the app:

```bash
tar -xzf uniform-matrix-desktop-*-linux-x64.tar.gz
cd uniform-matrix-desktop-*-linux-x64
./Uniform.Matrix.Client.Desktop
```

To add it to the application menu, run `./install-desktop-entry.sh` in the same folder. Run it again if you move the folder, and `./install-desktop-entry.sh --uninstall` removes the entry.

### From source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download), from the repository root:

```bash
dotnet run --project src/Uniform.Matrix.Client.Desktop
```

## Sign in

Enter your full user ID, such as `@alice:matrix.org`, and your password. The server is found from the user ID; give its address only when it has no discovery file, for example `http://localhost:8008` for a server on your own computer.

The app remembers the account, so the next start opens the chat without asking. *⋮ → Sign out* logs the device out on the server and forgets the account.

Next, see [Rooms](rooms.md) and [Messages and files](messages.md).

## Where things are kept

| What | Where |
|---|---|
| Messages, keys and files | `~/.local/share/uniform-matrix-client-desktop/` |
| Remembered account and settings | `~/.config/uniform-matrix-client-desktop/settings.json` |

On Windows, `~` is your user folder, for example `C:\Users\alice`.
