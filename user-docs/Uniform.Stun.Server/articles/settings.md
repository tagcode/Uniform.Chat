---
title: Settings
uid: Uniform.Stun.Server.Settings.Article
---
# Settings

All settings are in section `Turn`. ↻ marks that a change takes effect after a restart; all of them do, as the server binds its ports at start.

| Option | Default | Meaning |
|---|---|---|
| `Turn:Enabled` | `false` | Run the server; this program's `appsettings.json` sets it to `true`. ↻ |
| `Turn:Realm` | not set | Realm of the credentials, usually your domain; `turn` when not set. ↻ |
| `Turn:ListenAddress` | `0.0.0.0` | Address the TURN server listens on. ↻ |
| `Turn:Port` | `3478` | Port of STUN and TURN over UDP and TCP. ↻ |
| `Turn:Tcp` | `true` | Also listen over TCP on that port, for networks that block UDP. ↻ |
| `Turn:TlsPort` | not set | Port of TURN over TLS, such as `5349`; none when not set. ↻ |
| `Turn:CertificatePath` | not set | Certificate file of TURN over TLS, with its intermediate certificates, such as certbot's `fullchain.pem`; a renewed file is picked up without a restart. ↻ |
| `Turn:KeyPath` | not set | Private key of that certificate, such as certbot's `privkey.pem`. ↻ |
| `Turn:ExternalAddress` | not set | Public IPv4 address that relayed data is sent to, the router's public address when the server is behind NAT: a fixed address, a host name that is resolved again at every check, e.g. of a dynamic DNS service, or `stun:host:port` to ask that STUN server. Needed unless `Turn:ListenAddress` is that address. A changed address, e.g. after the router's DHCP lease, goes to new relays. ↻ |
| `Turn:AddressCheckInterval` | `00:01:00` | How often the server checks its local and public addresses for changes, besides when the system reports one. ↻ |
| `Turn:RelayAddress` | not set | Address the relay ports bind; the listen address when not set. ↻ |
| `Turn:MinRelayPort` | `49152` | First UDP port of relayed addresses. ↻ |
| `Turn:MaxRelayPort` | `65535` | Last UDP port of relayed addresses; behind NAT, forward the whole range. ↻ |
| `Turn:MaxAllocations` | `1000` | Most relays at once; a call uses one or two. ↻ |
| `Turn:MaxAllocationsPerUser` | `10` | Most relays at once per user. ↻ |
| `Turn:AllowedPeers` | not set | Private addresses relayed to anyway, separated by commas, such as `192.168.1.0/24`; private, loopback and link-local addresses are refused otherwise, so outsiders cannot reach your network through the server. ↻ |
| `Turn:SharedSecrets` | not set | Shared secrets of time-limited credentials, separated by commas, the same as the homeserver's, see [Homeservers](xref:Uniform.Stun.Server.Homeservers.Article). Keep them out of command lines. ↻ |
| `Turn:Users` | not set | Fixed users as `name:password`, separated by commas. Keep them out of command lines. ↻ |
| `Logging:LogLevel:Default` | `Information` | Least important log entries written: `Debug` adds refused credentials. |
