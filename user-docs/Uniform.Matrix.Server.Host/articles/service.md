---
title: Run without the dashboard
uid: Uniform.Matrix.Server.Host.Service.Article
---
# Run without the dashboard

Without a terminal, for example as a service, the server runs without the dashboard and also writes its log to standard output:

```bash
./Uniform.Matrix.Server.Host --Host:Ui=None
```

`Host:Ui` chooses the mode:

| Value | Mode |
|---|---|
| `Auto` (default) | The dashboard when input and output are a terminal; none when either is redirected, as under systemd or with `> server.log` |
| `Dashboard` | Dashboard for monitoring server status and for perusing logs. |
| `None` | No dashboard. |

Stop the server with Ctrl+C, or with `SIGTERM` from a service manager.

## As a systemd service

Publish the server once, then run the published program from a unit file:

```bash
dotnet publish src/Uniform.Matrix.Server.Host -c Release -o ~/matrix-server
```

```ini
# ~/.config/systemd/user/matrix-server.service
[Unit]
Description=Matrix homeserver

[Service]
ExecStart=%h/matrix-server/Uniform.Matrix.Server.Host
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now matrix-server
journalctl --user -u matrix-server -f
```

systemd connects the server's input to `/dev/null` and its output to the journal, so `Auto` runs without the dashboard there and the log goes to the journal.
