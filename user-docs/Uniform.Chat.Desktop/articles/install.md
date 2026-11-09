---
title: Installing
uid: Uniform.Chat.Desktop.Install.Article
---
# Installing

The app runs on Windows 10 or later and on Linux, on x64 and arm64 processors. It brings everything it needs; no .NET or other runtime has to be installed.

## Apt install

On Debian and Ubuntu, x64 and arm64, install from the apt repository of Uniform Chat, which is kept in its releases on GitHub. Add the repository once, with the key its packages are signed with:

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://github.com/tagcode/Uniform.Chat/releases/latest/download/uniform-chat-archive-keyring.gpg | sudo tee /etc/apt/keyrings/uniform-chat.gpg > /dev/null
echo "deb [signed-by=/etc/apt/keyrings/uniform-chat.gpg] https://github.com/tagcode/Uniform.Chat/releases/ latest/download/" | sudo tee /etc/apt/sources.list.d/uniform-chat.list
sudo apt update
```

Then install the app, which also adds it to the application menu:

```bash
sudo apt install uniform-chat
```

Start it from the menu, or with the command `uniform-chat`. `sudo apt upgrade` updates it with the rest of the system. `sudo apt remove uniform-chat` removes it, but not your account and messages in `~/.local/share/uniform-chat-desktop`.

## Windows

Download `uniform-chat-desktop-<version>-win-x64.zip` (or `win-arm64`) from the [releases](https://github.com/tagcode/Uniform.Chat/releases) and unpack it anywhere; the `docs` folder in it has this guide (open `docs/index.html`). Then run `Uniform.Chat.exe`. Windows may warn that the app is from an unknown publisher, because it is not signed yet; choose *More info → Run anyway*.

## Other Linux systems

Download `uniform-chat-desktop-<version>-linux-x64.tar.gz` (or `linux-arm64`) from the [releases](https://github.com/tagcode/Uniform.Chat/releases), unpack it and run the app:

```bash
tar -xzf uniform-chat-desktop-*-linux-x64.tar.gz
cd uniform-chat-desktop-*-linux-x64
./Uniform.Chat
```

You can also start it by double-clicking `Uniform.Chat` in the file manager.

To add it to the application menu, run `./install-desktop-entry.sh` in the same folder. Run it again if you move the folder, and `./install-desktop-entry.sh --uninstall` removes the entry.

## From source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download), from the repository root:

```bash
dotnet run --project src/Uniform.Chat.Desktop
```
