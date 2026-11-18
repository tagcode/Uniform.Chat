---
title: Running
uid: Uniform.MatrixRtc.Server.Running.Article
---
# Running

The service needs a LiveKit server with an API key, see [LiveKit's self-hosting guide](https://docs.livekit.io/home/self-hosting/local/). Turn off LiveKit's `room: auto_create`, so that only users of full-access homeservers can start calls; the service creates their rooms.

## Install

Unpack the release anywhere. On Debian and Ubuntu, the releases are also an apt repository, for x64 and arm64: add it once, with its signing key, then install the service, which `sudo apt upgrade` keeps up to date. The command is then `uniform-matrixrtc-server` in place of `./uniform-matrixrtc-server` below.

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://github.com/tagcode/Uniform.Chat/releases/latest/download/uniform-chat-archive-keyring.gpg | sudo tee /etc/apt/keyrings/uniform-chat.gpg > /dev/null
echo "deb [signed-by=/etc/apt/keyrings/uniform-chat.gpg] https://github.com/tagcode/Uniform.Chat/releases/ latest/download/" | sudo tee /etc/apt/sources.list.d/uniform-chat.list
sudo apt update
```

```bash
sudo apt install uniform-matrixrtc-server
```

## First start

Give the service LiveKit's address, its API key and secret, and your homeserver. Put the secret in the settings file or an environment variable, not on the command line, where other users of the computer can see it:

```bash
export MatrixRtc__ApiSecret='a-long-random-secret-of-at-least-32-characters'
./uniform-matrixrtc-server --MatrixRtc:Url=wss://livekit.example.org --MatrixRtc:ApiKey=homeserver --MatrixRtc:FullAccessHomeservers=example.org
```

It listens on port 8080. When a setting is missing, the service prints which one and stops.

Apps reach it over HTTPS, so put a reverse proxy with a certificate in front of it. The usual path is `/livekit/jwt` of the homeserver's domain. With Caddy, for example:

```text
example.org {
    handle_path /livekit/jwt/* {
        reverse_proxy localhost:8080
    }
}
```

`https://example.org/livekit/jwt/healthz` then answers with status 200.

Settings come from, later ones overriding earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/uniform-matrixrtc-server/settings.json`
3. The service's environment variables, such as `MATRIXRTC_SECRET`
4. Environment variables, with `__` between parts: `MatrixRtc__ApiSecret`
5. The command line: `--MatrixRtc:Url=wss://livekit.example.org`

Its log files are in `~/.local/share/uniform-matrixrtc-server/logs`, one per start.

## As a system service

Unpack it in `/opt/uniform-matrixrtc-server`, create a user for it, and write the settings, with the secret, to `/etc/uniform-matrixrtc-server/settings.json`:

```bash
sudo useradd --system --home-dir /var/lib/uniform-matrixrtc-server --shell /usr/sbin/nologin uniform-matrixrtc-server
sudo mkdir -p /opt/uniform-matrixrtc-server /etc/uniform-matrixrtc-server
sudo tar -xzf uniform-chat-matrixrtc-<version>-linux-x64.tar.gz -C /opt/uniform-matrixrtc-server --strip-components=1
sudo nano /etc/uniform-matrixrtc-server/settings.json
sudo chown -R uniform-matrixrtc-server:uniform-matrixrtc-server /etc/uniform-matrixrtc-server
sudo chmod 600 /etc/uniform-matrixrtc-server/settings.json
```

```json
{
  "MatrixRtc": {
    "Url": "wss://livekit.example.org",
    "ApiKey": "homeserver",
    "ApiSecret": "a-long-random-secret-of-at-least-32-characters",
    "FullAccessHomeservers": "example.org"
  }
}
```

Then the unit file `/etc/systemd/system/uniform-matrixrtc-server.service`:

```ini
[Unit]
Description=Uniform MatrixRTC authorization server
After=network-online.target
Wants=network-online.target

[Service]
User=uniform-matrixrtc-server
Group=uniform-matrixrtc-server
ExecStart=/opt/uniform-matrixrtc-server/uniform-matrixrtc-server
Environment=XDG_CONFIG_HOME=/etc
Environment=XDG_DATA_HOME=/var/lib
StateDirectory=uniform-matrixrtc-server
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/uniform-matrixrtc-server

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now uniform-matrixrtc-server
journalctl -u uniform-matrixrtc-server -f
```
