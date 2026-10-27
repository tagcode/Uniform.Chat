# Settings

Settings come from these places; later ones override earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/uniform-matrix-server-host/settings.json`, written by the settings window
3. Environment variables, with `__` between parts: `Server__Registration=Open`
4. The command line: `--Server:Registration=Open`
5. Changes applied in the settings window, until the server stops

| Option | Default | Meaning |
|---|---|---|
| `Urls` | `http://localhost:8008` | Addresses to listen on, separated by `;`. ↻ |
| `Server:ServerName` | `localhost` | The name in user IDs and room aliases, such as `example.org` in `@alice:example.org`. Choose it before creating accounts: changing it later leaves every existing account and room behind. ↻ |
| `Server:PublicBaseUrl` | not set | The address clients should use, such as `https://matrix.example.org`. When set, the server announces it at `/.well-known/matrix/client`. |
| `Server:Registration` | `Disabled` | Who can create accounts: `Disabled`, `Open` (anyone), or `Token` (anyone with a registration token). |
| `Server:RegistrationTokens` | not set | Tokens for `Token` registration, separated by commas. |
| `Media:MaxUploadSize` | `52428800` | Largest file users can upload, in bytes (50 MiB). |
| `Media:AllowUnauthenticatedDownloads` | `false` | Also serve files at the old `/_matrix/media/v3` download addresses, which need no login, for older clients. Anyone with a link can then fetch a file, so leave it off unless a client needs it. |
| `Media:PendingUploadLifetime` | `1.00:00:00` | How long a client may take to upload a file it reserved an address for first. |
| `Media:MaxPendingUploads` | `10` | How many reserved addresses a user may have waiting for their files. |
| `Push:AllowPrivateGateways` | `false` | Send push notifications also to gateways at private and local network addresses, such as a push gateway on this machine. Off, so accounts cannot make the server reach machines inside your network. |
| `Push:GatewayTimeout` | `00:00:10` | How long to wait for a push gateway to answer. |
| `UrlPreviews:Enabled` | `false` | Make previews of linked pages (title, description and image) for clients that ask. Off by default: fetching tells the linked site that someone on this server posted the link, and in encrypted rooms it shows the server what was linked. |
| `UrlPreviews:AllowPrivateAddresses` | `false` | Also preview pages at private and local network addresses. Off, so accounts cannot make the server reach machines inside your network. |
| `UrlPreviews:MaxSize` | `2097152` | Most bytes read of a page or its image (2 MiB). |
| `UrlPreviews:Timeout` | `00:00:10` | How long to wait for a page. |
| `UrlPreviews:CacheDuration` | `01:00:00` | How long a preview is reused for the same address. |
| `RateLimits:Enabled` | `true` | Limit how fast clients may write and log in. Clients that go over get an error telling them how long to wait; well-behaved clients wait and retry. ↻ |
| `RateLimits:WriteBurst` | `50` | Writes per login allowed at once: messages, state changes, invites, new rooms and uploads. ↻ |
| `RateLimits:WritesPerSecond` | `10` | Writes per login allowed each second after the burst. ↻ |
| `RateLimits:LoginBurst` | `20` | Logins and registrations from one address allowed at once. Behind a reverse proxy every request comes from the proxy's address, so these limits are shared by everyone. ↻ |
| `RateLimits:LoginsPerMinute` | `10` | Logins and registrations from one address allowed each minute after the burst. ↻ |
| `Federation:Enabled` | `false` | Talk to other Matrix servers. Unfinished: today it serves this server's signing key, answers profile queries, shows users of other servers their profiles, and lets users join rooms of other servers by room ID and talk in them; other servers' users can join rooms here. Rooms of version 12, which Synapse now creates by default, can be joined, but not yet created here. Needs HTTPS with a real certificate. |
| `Federation:AllowPrivateAddresses` | `false` | Also reach servers at private and local network addresses. Off, so remote server names cannot make the server reach machines inside your network. |
| `Federation:RequestTimeout` | `00:00:30` | How long to wait for another server to answer. |
| `Federation:KeyValidity` | `1.00:00:00` | How long other servers may keep this server's signing key before asking again. |
| `Tls:CertificatePath` | not set | Certificate file for `https://` addresses in `Urls`, with its intermediate certificates, such as [certbot](https://github.com/certbot/certbot)'s `fullchain.pem`. A path may start with `~/` for the home folder, also where the shell leaves it alone, as in `--Tls:CertificatePath=~/tls/fullchain.pem`. A renewed file is picked up within a minute, without a restart. ↻ |
| `Tls:KeyPath` | not set | Private key of the certificate, such as [certbot](https://github.com/certbot/certbot)'s `privkey.pem`. Keep it readable only by the account that runs the server. ↻ |
| `Storage:DataDirectory` | `~/.local/share/uniform-matrix-server-host` | Folder for the database, signing key, media and logs. ↻ |
| `Storage:LogFilesKept` | `20` | How many session logs to keep. ↻ |
| `Host:Ui` | `Auto` | `Auto`, `Dashboard` or `None`, see [Run without the dashboard](service.md). ↻ |

↻ marks options that take effect after a restart.

> [!WARNING]
> Without `Tls:*` the server speaks plain HTTP. Keep it on `localhost`, or give it a certificate, or put a reverse proxy such as Caddy or nginx in front of it, before letting other computers connect. Listening on all interfaces (`--Urls=http://0.0.0.0:8008`) without HTTPS sends passwords unencrypted.

To serve HTTPS itself, point the server at a certificate, for example [certbot](https://github.com/certbot/certbot)'s files, and use `https://` addresses:

```bash
./Uniform.Matrix.Server.Host --Urls="https://0.0.0.0:8448" \
  --Tls:CertificatePath=/path/to/fullchain.pem --Tls:KeyPath=/path/to/privkey.pem
```
