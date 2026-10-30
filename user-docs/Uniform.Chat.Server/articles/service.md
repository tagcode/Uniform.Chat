---
title: Run without the dashboard
uid: Uniform.Chat.Server.Service.Article
---
# Run without the dashboard

Without a terminal, for example as a service, the server runs without the dashboard and also writes its log to standard output:

```bash
./uniform-chat-server --Host:Ui=None
```

`Host:Ui` chooses the mode:

| Value | Mode |
|---|---|
| `Auto` (default) | The dashboard when input and output are a terminal; none when either is redirected, as under systemd or with `> server.log` |
| `Dashboard` | Dashboard for monitoring server status and for perusing logs. |
| `None` | No dashboard. |

Stop the server with Ctrl+C, or with `SIGTERM` from a service manager.

## As a user service

To run the server as your own user, put the program in a folder of its own, e.g. `~/uniform-chat-server`, then run it from a user unit. Its settings are in `~/.config/uniform-chat-server` and its data in `~/.local/share/uniform-chat-server`.

Unpack the downloaded release there:

```bash
mkdir -p ~/uniform-chat-server
tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C ~/uniform-chat-server --strip-components=1
```

Or, from the source, publish it there:

```bash
dotnet publish src/Uniform.Chat.Server -c Release -o ~/uniform-chat-server
```

Then write the unit file:

```ini
# ~/.config/systemd/user/uniform-chat-server.service
[Unit]
Description=Uniform Chat homeserver

[Service]
ExecStart=%h/uniform-chat-server/uniform-chat-server
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now uniform-chat-server
journalctl --user -u uniform-chat-server -f
```

systemd connects the server's input to `/dev/null` and its output to the journal, so `Auto` runs without the dashboard there and the log goes to the journal. A user service stops when you log out, unless lingering is on: `sudo loginctl enable-linger $USER`.

## As a system service (Ubuntu)

On a server, run it as a system service named `uniform-chat-server`: it starts at boot, runs as a user of its own, and is controlled with `sudo systemctl`. The steps below put the program in `/opt/uniform-chat-server`, the settings in `/etc/uniform-chat-server`, and the data in `/var/lib/uniform-chat-server`.

1. Create a system user for the server, with no login:

   ```bash
   sudo useradd --system --home-dir /var/lib/uniform-chat-server --shell /usr/sbin/nologin uniform-chat-server
   ```

2. Unpack the downloaded release in `/opt/uniform-chat-server`; or, from the source, publish it there:

   ```bash
   sudo mkdir -p /opt/uniform-chat-server
   sudo tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C /opt/uniform-chat-server --strip-components=1
   ```

   ```bash
   sudo dotnet publish src/Uniform.Chat.Server -c Release -o /opt/uniform-chat-server
   ```

3. Write the settings to `/etc/uniform-chat-server/settings.json`, readable only by the server, as it may hold secrets such as the TURN secret. For example, behind nginx on the same machine:

   ```bash
   sudo mkdir -p /etc/uniform-chat-server
   sudo nano /etc/uniform-chat-server/settings.json
   sudo chown -R uniform-chat-server:uniform-chat-server /etc/uniform-chat-server
   sudo chmod 600 /etc/uniform-chat-server/settings.json
   ```

   ```json
   {
     "Urls": "http://127.0.0.1:8008",
     "Server": {
       "ServerName": "example.org",
       "PublicBaseUrl": "https://example.org",
       "TrustedProxies": "127.0.0.1",
       "Registration": "Token",
       "RegistrationTokens": "a-long-random-token"
     }
   }
   ```

   [Installing](xref:Uniform.Chat.Server.Install.Article) has a complete example with federation and calls, and the nginx site in front; [Settings](xref:Uniform.Chat.Server.Settings.Article) lists every option.

4. Write the unit file `/etc/systemd/system/uniform-chat-server.service`:

   ```ini
   [Unit]
   Description=Uniform Chat homeserver
   After=network-online.target
   Wants=network-online.target

   [Service]
   User=uniform-chat-server
   Group=uniform-chat-server
   ExecStart=/opt/uniform-chat-server/uniform-chat-server --Host:Ui=None
   WorkingDirectory=/var/lib/uniform-chat-server
   # Settings from /etc/uniform-chat-server, data in /var/lib/uniform-chat-server
   Environment=XDG_CONFIG_HOME=/etc
   Environment=XDG_DATA_HOME=/var/lib
   StateDirectory=uniform-chat-server
   StateDirectoryMode=0700
   Restart=on-failure
   RestartSec=5
   # Stop gracefully: SIGTERM, then wait for the database to close
   TimeoutStopSec=30
   # Hardening: no new privileges, read-only system, private /tmp
   NoNewPrivileges=true
   ProtectSystem=strict
   ProtectHome=true
   PrivateTmp=true
   ReadWritePaths=/var/lib/uniform-chat-server /etc/uniform-chat-server

   [Install]
   WantedBy=multi-user.target
   ```

   The server adds its own name to `XDG_CONFIG_HOME` and `XDG_DATA_HOME`, which gives the two folders above. `/etc/uniform-chat-server` is writable so that the settings window, if you ever run the dashboard as this user, can save there; take it out of `ReadWritePaths` to keep the settings read-only.

5. Start it, and have it start at boot:

   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now uniform-chat-server
   ```

Then control it with `systemctl`, and read its log from the journal:

| Command | Does |
|---|---|
| `sudo systemctl status uniform-chat-server` | Show whether it runs, and the latest log lines |
| `sudo systemctl stop uniform-chat-server` | Stop it |
| `sudo systemctl start uniform-chat-server` | Start it |
| `sudo systemctl restart uniform-chat-server` | Restart it, e.g. after changing the settings |
| `sudo systemctl disable uniform-chat-server` | Not start it at boot any more |
| `journalctl -u uniform-chat-server -f` | Follow the log |
| `journalctl -u uniform-chat-server --since today` | Today's log |

The server also keeps its own log files in `/var/lib/uniform-chat-server/logs`, with its database and signing key; see [Files](xref:Uniform.Chat.Server.Files.Article). To update the server, see [Updating](xref:Uniform.Chat.Server.Updating.Article).

> [!NOTE]
> To serve HTTPS itself, without a proxy, the server needs to read the certificate and to listen on port 443:
> - Ports below 1024 need `AmbientCapabilities=CAP_NET_BIND_SERVICE` in the `[Service]` section; or listen on 8448 instead.
> - certbot keeps its files readable only by root. Copy them where the server can read them in a deploy hook, e.g. `/etc/letsencrypt/renewal-hooks/deploy/uniform-chat-server.sh`:
>
>   ```bash
>   #!/bin/sh
>   install -o uniform-chat-server -g uniform-chat-server -m 600 "$RENEWED_LINEAGE/fullchain.pem" "$RENEWED_LINEAGE/privkey.pem" /etc/uniform-chat-server/tls/
>   ```
>
>   and point `Tls:CertificatePath` and `Tls:KeyPath` at `/etc/uniform-chat-server/tls/`. The server picks up renewed files without a restart.
