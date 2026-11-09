---
title: Running
uid: Uniform.Stun.Server.Running.Article
---
# Running

## Ports

The server must be reachable from the internet on these ports. Behind a home router, forward them to the server's computer:

| Port | Protocol | Use |
|---|---|---|
| 3478 | UDP and TCP | STUN and TURN, `Turn:Port` |
| 49152–65535 | UDP | Relayed calls, `Turn:MinRelayPort` to `Turn:MaxRelayPort`; each call uses one or two |
| 5349, if set | TCP | TURN over TLS, `Turn:TlsPort` |

## Install

Unpack the release anywhere. On Debian and Ubuntu, the releases are also an apt repository, for x64 and arm64: add it once, with its signing key, then install the server, which `sudo apt upgrade` keeps up to date. The command is then `uniform-stun-server` in place of `./uniform-stun-server` below.

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://github.com/tagcode/Uniform.Chat/releases/latest/download/uniform-chat-archive-keyring.gpg | sudo tee /etc/apt/keyrings/uniform-chat.gpg > /dev/null
echo "deb [signed-by=/etc/apt/keyrings/uniform-chat.gpg] https://github.com/tagcode/Uniform.Chat/releases/ latest/download/" | sudo tee /etc/apt/sources.list.d/uniform-chat.list
sudo apt update
```

```bash
sudo apt install uniform-stun-server
```

## First start

Give the server its public address and a shared secret. Put the secret in the settings file or an environment variable, not on the command line, where other users of the computer can see it:

```bash
export Turn__SharedSecrets='a-long-random-secret'
./uniform-stun-server --Turn:ExternalAddress=203.0.113.5 --Turn:Realm=example.org
```

`Turn:ExternalAddress` is the public address peers send relayed calls to: the router's public address when the server is behind NAT. When it changes now and then, e.g. on a home connection, give a dynamic DNS name instead, which is resolved again every minute, or `stun:host:port` of a STUN server, which is asked every minute. Relays made after a change get the new address.

The log tells where it listens:

```text
TURN server listening on udp 0.0.0.0:3478, tcp 0.0.0.0:3478, relaying from 203.0.113.5 ports 49152-65535
```

When a setting cannot work, the server prints why and stops, e.g. without credentials or without an external address.

Settings come from, later ones overriding earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/uniform-stun-server/settings.json`
3. Environment variables, with `__` between parts: `Turn__Port=3478`
4. The command line: `--Turn:Port=3478`

Its log files are in `~/.local/share/uniform-stun-server/logs`, one per start.

## As a system service

Unpack it in `/opt/uniform-stun-server`, create a user for it, and write the settings, with the secret, to `/etc/uniform-stun-server/settings.json`:

```bash
sudo useradd --system --home-dir /var/lib/uniform-stun-server --shell /usr/sbin/nologin uniform-stun-server
sudo mkdir -p /opt/uniform-stun-server /etc/uniform-stun-server
sudo tar -xzf uniform-stun-server-<version>-linux-x64.tar.gz -C /opt/uniform-stun-server --strip-components=1
sudo nano /etc/uniform-stun-server/settings.json
sudo chown -R uniform-stun-server:uniform-stun-server /etc/uniform-stun-server
sudo chmod 600 /etc/uniform-stun-server/settings.json
```

```json
{
  "Turn": {
    "Realm": "example.org",
    "ExternalAddress": "203.0.113.5",
    "SharedSecrets": "a-long-random-secret"
  }
}
```

Then the unit file `/etc/systemd/system/uniform-stun-server.service`:

```ini
[Unit]
Description=Uniform STUN and TURN server
After=network-online.target
Wants=network-online.target

[Service]
User=uniform-stun-server
Group=uniform-stun-server
ExecStart=/opt/uniform-stun-server/uniform-stun-server
Environment=XDG_CONFIG_HOME=/etc
Environment=XDG_DATA_HOME=/var/lib
StateDirectory=uniform-stun-server
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/uniform-stun-server

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now uniform-stun-server
journalctl -u uniform-stun-server -f
```

For TURN over TLS on port 443, which is below 1024, add `AmbientCapabilities=CAP_NET_BIND_SERVICE` to the `[Service]` section, and make the certificate readable by the user.
