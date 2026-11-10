---
title: Settings
uid: Uniform.Chat.Server.Settings.Article
---
# Settings

Settings come from these places; later ones override earlier ones:

1. `appsettings.json` next to the program
2. `~/.config/uniform-chat-server/settings.json`, written by the settings window
3. Environment variables, with `__` between parts: `Server__Registration=Open`
4. The command line: `--Server:Registration=Open`
5. Changes applied in the settings window, until the server stops

| Option | Default | Meaning |
|---|---|---|
| `Urls` | `http://localhost:8008` | Addresses to listen on, separated by `;`. ↻ |
| `Server:ServerName` | `localhost` | The name in user IDs and room aliases, such as `example.org` in `@alice:example.org`. Choose it before creating accounts: changing it later leaves every existing account and room behind. ↻ |
| `Server:PublicBaseUrl` | not set | The address clients should use, such as `https://matrix.example.org`. When set, the server announces it at `/.well-known/matrix/client`. |
| `Server:Registration` | `Disabled` | Who can create accounts: `Disabled`, `Open` (anyone), or `Token` (anyone with a registration token). |
| `Server:TrustedProxies` | not set | Addresses of reverse proxies, such as nginx, whose `X-Forwarded-For` header gives the client's address, separated by commas. Set it when the server runs behind a proxy, so rate limits count each client instead of the proxy. Other senders' headers are ignored. ↻ |
| `Server:RegistrationTokens` | not set | Tokens for `Token` registration, separated by commas; admins can also [create tokens](xref:Uniform.Chat.Server.RemoteAdmin.Article#registration-tokens) with limited uses remotely. |
| `Server:TermsUrl` | not set | Address of terms of service that new users accept when registering. |
| `Server:PrivacyPolicyUrl` | not set | Address of a privacy policy that new users accept when registering. |
| `Server:PoliciesVersion` | `1.0` | Version of the terms and privacy policy; raise it when they change. |
| `Server:ServerNoticesLocalpart` | `notices` | Name of the account that sends [server notices](xref:Uniform.Chat.Server.RemoteAdmin.Article#server-notices), created without a password on the first notice. |
| `Media:MaxUploadSize` | `52428800` | Largest file users can upload, in bytes (50 MiB). |
| `Media:MaxThumbnailSourcePixels` | `40000000` | Largest image, in pixels, that thumbnails are made from; larger images get none. |
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
| `Federation:Enabled` | `false` | Talk to other Matrix servers: profiles, joining their rooms by ID or alias, invites both ways, messages, files and images, end-to-end encryption, typing, read receipts and presence. Still being built: received events are checked against the room's current state only, and state differences between servers are not resolved yet. Needs HTTPS with a real certificate. |
| `Federation:AllowPrivateAddresses` | `false` | Also reach servers at private and local network addresses. Off, so remote server names cannot make the server reach machines inside your network. |
| `Federation:TrustedKeyServers` | not set | Key servers to ask for other servers' signing keys when those servers cannot be reached, separated by commas, such as `matrix.org`. Asking tells the key server which servers this one talks to, so none are asked by default. With one set, joining a large room is much faster, as the keys of all its servers come in a few requests. |
| `Federation:RequestTimeout` | `00:00:30` | How long to wait for another server to answer. |
| `Federation:JoinTimeout` | `00:03:00` | How long to wait for another server to answer a join, which sends the whole state of the room. |
| `Federation:KeyRequestTimeout` | `00:00:10` | How long to wait for a server's signing keys, from it or a key server. |
| `Federation:KeyValidity` | `1.00:00:00` | How long other servers may keep this server's signing key before asking again. |
| `Federation:WellKnownServer` | not set | Where other servers should connect, served as `/.well-known/matrix/server`, such as `example.org:443` when a reverse proxy on port 443 forwards to this server. Needed when the server name has no port and the server is not on port 8448. |
| `Calls:LiveKitServiceUrl` | not set | Address of the MatrixRTC authorization service (lk-jwt-service) in front of a LiveKit server, for calls in Element X and other MatrixRTC clients. Announced to clients through `rtc/transports` and `/.well-known/matrix/client`. |
| `Calls:TurnUris` | not set | TURN server addresses for older 1:1 calls, separated by commas, such as `turn:turn.example.org:3478?transport=udp`. |
| `Calls:TurnSharedSecret` | not set | Shared secret of the TURN server, such as coturn's `static-auth-secret`. Keep it out of command lines; set it in the settings file or as the environment variable `Calls__TurnSharedSecret`. |
| `Calls:TurnUserLifetime` | `1.00:00:00` | How long TURN credentials hold. |
| `Calls:MaxEventDelay` | `1.00:00:00` | Longest delay of a delayed event, which calls use to end the membership of a client that disappears. |
| `Calls:OpenIdTokenLifetime` | `01:00:00` | How long an OpenID token holds; call services use these tokens to check who a user is. |
| `Turn:Enabled` | `false` | Run the built-in STUN and TURN server, which relays calls between users whose networks would block them; calls then use it unless `Calls:TurnUris` names another. See [Calls through firewalls](xref:Uniform.Chat.Server.Turn.Article). ↻ |
| `Turn:Realm` | not set | Realm of the TURN credentials; the server name when not set. ↻ |
| `Turn:ListenAddress` | `0.0.0.0` | Address the TURN server listens on. ↻ |
| `Turn:Port` | `3478` | Port of STUN and TURN over UDP and TCP. ↻ |
| `Turn:Tcp` | `true` | Also listen over TCP on that port, for networks that block UDP. ↻ |
| `Turn:TlsPort` | not set | Port of TURN over TLS, such as `5349`; none when not set. ↻ |
| `Turn:CertificatePath` | not set | Certificate file of TURN over TLS; that of `Tls:CertificatePath` when not set. ↻ |
| `Turn:KeyPath` | not set | Private key of that certificate; that of `Tls:KeyPath` when not set. ↻ |
| `Turn:ExternalAddress` | not set | Public IPv4 address that relayed data is sent to, the router's public address when the server is behind NAT: a fixed address, a host name that is resolved again at every check, or `stun:host:port` to ask that STUN server. The host of `Server:PublicBaseUrl`, or else the server name, when not set. A changed address, e.g. after the router's DHCP lease, goes to new relays. ↻ |
| `Turn:AddressCheckInterval` | `00:01:00` | How often the TURN server checks its local and public addresses for changes, besides when the system reports one. ↻ |
| `Turn:RelayAddress` | not set | Address the relay ports bind; the listen address when not set. ↻ |
| `Turn:MinRelayPort` | `49152` | First UDP port of relayed addresses. ↻ |
| `Turn:MaxRelayPort` | `65535` | Last UDP port of relayed addresses; behind NAT, forward the whole range. ↻ |
| `Turn:MaxAllocations` | `1000` | Most relays at once; a call uses one or two. ↻ |
| `Turn:MaxAllocationsPerUser` | `10` | Most relays at once per user. ↻ |
| `Turn:AllowedPeers` | not set | Private addresses relayed to anyway, separated by commas, such as `192.168.1.0/24`; private, loopback and link-local addresses are refused otherwise, so outsiders cannot reach your network through the server. ↻ |
| `Turn:SharedSecrets` | not set | Further shared secrets of time-limited credentials, for other services than this homeserver. ↻ |
| `Turn:Users` | not set | Fixed users as `name:password`, separated by commas. Keep them out of command lines. ↻ |
| `Tls:CertificatePath` | not set | Certificate file for `https://` addresses in `Urls`, with its intermediate certificates, such as [certbot](https://github.com/certbot/certbot)'s `fullchain.pem`. A path may start with `~/` for the home folder, also where the shell leaves it alone, as in `--Tls:CertificatePath=~/tls/fullchain.pem`. A renewed file is picked up within a minute, without a restart. ↻ |
| `Tls:KeyPath` | not set | Private key of the certificate, such as [certbot](https://github.com/certbot/certbot)'s `privkey.pem`. Keep it readable only by the account that runs the server. ↻ |
| `Storage:DataDirectory` | `~/.local/share/uniform-chat-server` | Folder for the database, signing key, media and logs. ↻ |
| `Storage:DatabaseFileName` | `server.db` | Database file inside the data folder. ↻ |
| `Storage:MediaDirectoryName` | `media` | Folder inside the data folder for uploaded files. ↻ |
| `Storage:LogDirectoryName` | `logs` | Folder inside the data folder with one log file per session. ↻ |
| `Storage:LogFilesKept` | `20` | How many session logs to keep. ↻ |
| `Host:Ui` | `Auto` | `Auto`, `Dashboard` or `None`, see [Run without the dashboard](xref:Uniform.Chat.Server.Service.Article). ↻ |
| `Host:AdminSocket` | none | A local socket for `uniform-chat-server manage --socket=PATH`, e.g. `/run/uniform-chat-server/admin.sock`, which the service uses. Whoever may open it manages the server as an admin, without an account; the server lets only its own user and group open it. ↻ |
| `Logging:LogLevel:Default` | `Information` | Lowest level written to the log: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` or `None`. |

↻ marks options that take effect after a restart.

> [!WARNING]
> Without `Tls:*` the server speaks plain HTTP. Keep it on `localhost`, or give it a certificate, or put a reverse proxy such as Caddy or nginx in front of it, before letting other computers connect. Listening on all interfaces (`--Urls=http://0.0.0.0:8008`) without HTTPS sends passwords unencrypted.

To serve HTTPS itself, point the server at a certificate, for example [certbot](https://github.com/certbot/certbot)'s files, and use `https://` addresses:

```bash
./uniform-chat-server --Urls="https://0.0.0.0:8448" \
  --Tls:CertificatePath=/path/to/fullchain.pem --Tls:KeyPath=/path/to/privkey.pem
```
