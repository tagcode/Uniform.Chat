---
title: Run without the dashboard
uid: Uniform.Chat.Server.Service.Article
---
# Run without the dashboard

Without a terminal, for example as a service, the server runs without the dashboard and also writes its log to standard output:

```bash
./Uniform.Chat.Server --Host:Ui=None
```

`Host:Ui` chooses the mode:

| Value | Mode |
|---|---|
| `Auto` (default) | The dashboard when input and output are a terminal; none when either is redirected, as under systemd or with `> server.log` |
| `Dashboard` | Dashboard for monitoring server status and for perusing logs. |
| `None` | No dashboard. |

Stop the server with Ctrl+C, or with `SIGTERM` from a service manager.

## As a user service

To run the server as your own user, put the program in a folder of its own, e.g. `~/matrix-server`, then run it from a user unit.

Unpack the downloaded release there:

```bash
mkdir -p ~/matrix-server
tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C ~/matrix-server --strip-components=1
```

Or, from the source, publish it there:

```bash
dotnet publish src/Uniform.Chat.Server -c Release -o ~/matrix-server
```

Then write the unit file:

```ini
# ~/.config/systemd/user/matrix-server.service
[Unit]
Description=Matrix homeserver

[Service]
ExecStart=%h/matrix-server/Uniform.Chat.Server
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now matrix-server
journalctl --user -u matrix-server -f
```

systemd connects the server's input to `/dev/null` and its output to the journal, so `Auto` runs without the dashboard there and the log goes to the journal. A user service stops when you log out, unless lingering is on: `sudo loginctl enable-linger $USER`.

## As a system service (Ubuntu)

On a server, run it as a system service: it starts at boot, runs as a user of its own, and is controlled with `sudo systemctl`. The steps below put the program in `/opt/uniform-matrix`, the settings in `/etc/uniform-matrix`, and the data in `/var/lib/uniform-matrix`.

1. Create a system user for the server, with no login:

   ```bash
   sudo useradd --system --home-dir /var/lib/uniform-matrix --shell /usr/sbin/nologin uniform-matrix
   ```

2. Unpack the downloaded release in `/opt/uniform-matrix`; or, from the source, publish it there:

   ```bash
   sudo mkdir -p /opt/uniform-matrix
   sudo tar -xzf uniform-chat-server-<version>-linux-x64.tar.gz -C /opt/uniform-matrix --strip-components=1
   ```

   ```bash
   sudo dotnet publish src/Uniform.Chat.Server -c Release -o /opt/uniform-matrix
   ```

3. Write the settings to `/etc/uniform-matrix/uniform-matrix-server-host/settings.json`, readable only by the server, as it may hold secrets such as the TURN secret. For example, behind nginx on the same machine:

   ```bash
   sudo mkdir -p /etc/uniform-matrix/uniform-matrix-server-host
   sudo nano /etc/uniform-matrix/uniform-matrix-server-host/settings.json
   sudo chown -R uniform-matrix:uniform-matrix /etc/uniform-matrix
   sudo chmod 600 /etc/uniform-matrix/uniform-matrix-server-host/settings.json
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

4. Write the unit file `/etc/systemd/system/uniform-matrix.service`:

   ```ini
   [Unit]
   Description=Uniform Chat homeserver
   After=network-online.target
   Wants=network-online.target

   [Service]
   User=uniform-matrix
   Group=uniform-matrix
   ExecStart=/opt/uniform-matrix/Uniform.Chat.Server --Host:Ui=None
   WorkingDirectory=/var/lib/uniform-matrix
   # Settings from /etc/uniform-matrix, data in /var/lib/uniform-matrix
   Environment=XDG_CONFIG_HOME=/etc/uniform-matrix
   Environment=XDG_DATA_HOME=/var/lib/uniform-matrix
   StateDirectory=uniform-matrix
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
   ReadWritePaths=/var/lib/uniform-matrix /etc/uniform-matrix

   [Install]
   WantedBy=multi-user.target
   ```

   `/etc/uniform-matrix` is writable so that the settings window, if you ever run the dashboard as this user, can save there; take it out of `ReadWritePaths` to keep the settings read-only.

5. Start it, and have it start at boot:

   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now uniform-matrix
   ```

Then control it with `systemctl`, and read its log from the journal:

| Command | Does |
|---|---|
| `sudo systemctl status uniform-matrix` | Show whether it runs, and the latest log lines |
| `sudo systemctl stop uniform-matrix` | Stop it |
| `sudo systemctl start uniform-matrix` | Start it |
| `sudo systemctl restart uniform-matrix` | Restart it, e.g. after changing the settings |
| `sudo systemctl disable uniform-matrix` | Not start it at boot any more |
| `journalctl -u uniform-matrix -f` | Follow the log |
| `journalctl -u uniform-matrix --since today` | Today's log |

The server also keeps its own log files in `/var/lib/uniform-matrix/uniform-matrix-server-host/logs`, with its database and signing key; see [Files](xref:Uniform.Chat.Server.Files.Article).

To update, stop the server, unpack or publish the new version over `/opt/uniform-matrix`, and start it; it updates its database at start. Back up `/var/lib/uniform-matrix` first.

Versions before the Uniform Chat name had the program `Uniform.Matrix.Server.Host`. When updating from one, change `ExecStart` in the unit file to `/opt/uniform-matrix/Uniform.Chat.Server`, run `sudo systemctl daemon-reload`, and remove the old program from `/opt/uniform-matrix`. The settings and data folders are the same as before.

> [!NOTE]
> To serve HTTPS itself, without a proxy, the server needs to read the certificate and to listen on port 443:
> - Ports below 1024 need `AmbientCapabilities=CAP_NET_BIND_SERVICE` in the `[Service]` section; or listen on 8448 instead.
> - certbot keeps its files readable only by root. Copy them where the server can read them in a deploy hook, e.g. `/etc/letsencrypt/renewal-hooks/deploy/uniform-matrix.sh`:
>
>   ```bash
>   #!/bin/sh
>   install -o uniform-matrix -g uniform-matrix -m 600 "$RENEWED_LINEAGE/fullchain.pem" "$RENEWED_LINEAGE/privkey.pem" /etc/uniform-matrix/tls/
>   ```
>
>   and point `Tls:CertificatePath` and `Tls:KeyPath` at `/etc/uniform-matrix/tls/`. The server picks up renewed files without a restart.
