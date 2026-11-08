# Uniform.Turn

A STUN and TURN server in pure C#, following [RFC 8489](https://www.rfc-editor.org/rfc/rfc8489) and [RFC 8656](https://www.rfc-editor.org/rfc/rfc8656):

- Answers STUN Binding requests, so clients learn their public address.
- Relays UDP for clients that reach it over UDP, TCP or TLS: allocations, permissions, channels, Send and Data indications, refreshes.
- Long-term credentials with stateless nonces: time-limited credentials from shared secrets, as Matrix homeservers hand out (`SharedSecretAuthenticator`), or fixed users (`StaticUserAuthenticator`).
- Refuses to relay to private, loopback and other non-public addresses by default, limits allocations per user and in total, and ends what is not refreshed.
- Relays between two allocations of the same server directly, so clients behind the same router as the server need no NAT hairpinning.

```csharp
TurnServerOptions options = new() { Realm = "example.org", ExternalAddress = IPAddress.Parse("203.0.113.5") };
await using TurnServer server = new(options, new SharedSecretAuthenticator([secret], TimeProvider.System), logger);
await server.StartAsync();
```

Uniform.Stun.Hosting runs it in a .NET host with settings.
