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

## Example setup

A typical server on the internet runs behind a reverse proxy, such as nginx, which holds the HTTPS certificate; the server itself listens on plain HTTP on the same machine only. The example below is for `example.org`, with federation, sign-up by registration token, and voice calls through a TURN server; change the names, and leave out what you do not use.

Write the settings to `~/.config/uniform-matrix-server-host/settings.json` of the user that runs the server, or, as a [system service](xref:Uniform.Matrix.Server.Host.Service.Article), to `/etc/uniform-matrix/uniform-matrix-server-host/settings.json`. Make the file readable only by that user, as it holds secrets.

```json
{
  "Host": { "Ui": "None" },
  "Urls": "http://127.0.0.1:8008",
  "Server": {
    "ServerName": "example.org",
    "PublicBaseUrl": "https://example.org",
    "TrustedProxies": "127.0.0.1",
    "Registration": "Token",
    "RegistrationTokens": "a-long-random-token"
  },
  "Federation": {
    "Enabled": true,
    "WellKnownServer": "example.org:443"
  },
  "Calls": {
    "TurnUris": "turn:turn.example.org:3478?transport=udp,turn:turn.example.org:3478?transport=tcp",
    "TurnSharedSecret": "the TURN server's static-auth-secret"
  }
}
```

| Part | Why |
|---|---|
| `Urls` | Plain HTTP on this machine only; nginx in front serves HTTPS. Another port than 8008 if something, such as Synapse, uses it. |
| `ServerName`, `PublicBaseUrl` | The name in user IDs, and the address clients reach; the server announces it in `/.well-known/matrix/client`. Choose the name before creating accounts. |
| `TrustedProxies` | nginx's address, so that rate limits and logs see each client's own address. |
| `Registration`, `RegistrationTokens` | Anyone with a token can sign up; share the token with the people you invite. `Open` lets anyone sign up, `Disabled` no one. |
| `Federation` | Talk with other Matrix servers. `WellKnownServer` is announced in `/.well-known/matrix/server`, so other servers reach this one on port 443 through nginx, and port 8448 need not be open. |
| `Calls` | TURN servers for 1:1 calls, e.g. coturn with `use-auth-secret` and the same `static-auth-secret`; the server hands clients time-limited credentials. Leave the section out without a TURN server. |

[Settings](xref:Uniform.Matrix.Server.Host.Settings.Article) lists every option.

The nginx site, e.g. `/etc/nginx/sites-available/example.org`, enabled with a link in `sites-enabled`, passes Matrix's paths to the server, with a certificate from certbot:

```nginx
# HTTP: certbot's challenges, everything else to HTTPS
server {
    listen 80;
    listen [::]:80;
    server_name example.org;

    location /.well-known/acme-challenge/ { root /var/www/html; }
    location / { return 301 https://$host$request_uri; }
}

# HTTPS: clients and other servers
server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;
    server_name example.org;

    ssl_certificate     /etc/letsencrypt/live/example.org/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/example.org/privkey.pem;

    # Files people upload; at least Media:MaxUploadSize
    client_max_body_size 100M;

    location ~ ^/(_matrix|\.well-known/matrix)/ {
        proxy_pass http://127.0.0.1:8008;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;

        # Sync waits for new messages, and joining rooms on other servers can be slow
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
        proxy_buffering off;
    }
}
```

To get the certificate, enable first only the HTTP part, and run:

```bash
sudo certbot certonly --webroot -w /var/www/html -d example.org
sudo nginx -t && sudo systemctl reload nginx
```

Then enable the HTTPS part, reload nginx again, and start the server. Check from another computer that `https://example.org/.well-known/matrix/server` answers `{"m.server":"example.org:443"}`; the [federation tester](https://federationtester.matrix.org) then shows whether other servers can reach yours. Leave out the `[::]` lines when the name has no IPv6 address.
