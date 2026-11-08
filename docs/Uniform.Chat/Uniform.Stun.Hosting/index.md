---
uid: Uniform.Stun.Hosting.Article
title: Uniform.Stun.Hosting
---
# Uniform.Stun.Hosting

[`AddTurnServer`](xref:Uniform.Stun.Hosting.TurnHostingExtensions.AddTurnServer*) binds [`TurnOptions`](xref:Uniform.Stun.Hosting.TurnOptions) to section `Turn`, lists it among the settings with [`AddOptionSection`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddOptionSection*), and runs [`TurnServerHost`](xref:Uniform.Stun.Hosting.TurnServerHost) as a hosted service. Every setting is restart-required, as the server binds its ports at start; the secrets are marked secret, so the settings windows neither show nor save them.

The host makes the server's options from the settings: it resolves a host name in `ExternalAddress`, reads the TLS certificate with [`PemCertificate`](xref:Uniform.Stun.Hosting.PemCertificate), which reads it again when a file changes, and combines the credentials of `SharedSecrets`, `Users` and every [`ITurnAuthenticator`](xref:Uniform.Turn.ITurnAuthenticator) in the host. Settings that cannot work are logged and kept in [`Error`](xref:Uniform.Stun.Hosting.TurnServerHost.Error), and the host runs on without the server, so a homeserver is not stopped by its TURN settings; [Uniform.Stun.Server](xref:Uniform.Stun.Server.Article) exits with that message instead.

[Uniform.Chat.Server](xref:Uniform.Chat.Server.Article) embeds it: `EmbeddedTurn` registers an authenticator of the homeserver's `Calls:TurnSharedSecret`, or of a random secret of the run when that is not set, and sets `Calls:TurnUris` to the server's own addresses.
