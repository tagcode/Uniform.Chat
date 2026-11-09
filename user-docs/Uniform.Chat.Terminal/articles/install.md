---
title: Installing
uid: Uniform.Chat.Terminal.Install.Article
---
# Installing

The client runs on Linux and Windows, on x64 and arm64 processors. It brings everything it needs; no .NET or other runtime has to be installed.

## Apt install

On Debian and Ubuntu, x64 and arm64, install from the apt repository of Uniform Chat, which is kept in its releases on GitHub. Add the repository once, with the key its packages are signed with:

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://github.com/tagcode/Uniform.Chat/releases/latest/download/uniform-chat-archive-keyring.gpg | sudo tee /etc/apt/keyrings/uniform-chat.gpg > /dev/null
echo "deb [signed-by=/etc/apt/keyrings/uniform-chat.gpg] https://github.com/tagcode/Uniform.Chat/releases/ latest/download/" | sudo tee /etc/apt/sources.list.d/uniform-chat.list
sudo apt update
```

Then install the client:

```bash
sudo apt install uniform-chat-terminal
```

Start it with the command `uniform-chat-terminal`. `sudo apt upgrade` updates it with the rest of the system. `sudo apt remove uniform-chat-terminal` removes it, but not your account and messages in `~/.local/share/uniform-chat-terminal`.

## From the release archives

Download `uniform-chat-terminal-<version>-linux-x64.tar.gz` (or `linux-arm64`, or `win-x64.zip` and `win-arm64.zip` for Windows) from the [releases](https://github.com/tagcode/Uniform.Chat/releases) and unpack it anywhere. The program is `Uniform.Chat`, or `Uniform.Chat.exe` on Windows. The `docs` folder in it has this guide; open `docs/index.html` in a browser.

## From source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run `dotnet build` in the repository root; then use `dotnet run --project src/Uniform.Chat.Terminal --` in place of the program in this guide.
