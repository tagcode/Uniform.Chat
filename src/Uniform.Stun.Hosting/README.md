# Uniform.Stun.Hosting

Runs the Uniform.Turn STUN and TURN server in a .NET host:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddTurnServer();
await builder.Build().RunAsync();
```

It starts when `Turn:Enabled` is true, with the settings of section `Turn`: ports, the external address, relay ports, limits, TLS certificate files, and credentials from shared secrets (`Turn:SharedSecrets`) or fixed users (`Turn:Users`). Register an `ITurnAuthenticator` in the host to add credentials of its own, as Uniform Chat Server does for its homeserver.
