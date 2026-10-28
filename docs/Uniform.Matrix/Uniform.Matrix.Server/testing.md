---
title: Testing
uid: Uniform.Matrix.Server.Testing.Article
---
# Testing

`Uniform.Matrix.Server.Tests` start the whole server in memory with `TestHomeserver` (ASP.NET Core's test server, a temporary data directory) and drive it with our own [`MatrixClient`](xref:Uniform.Matrix.Protocol.Client.MatrixClient). Server logs go to the test output. `EncryptionTests` run two of our [`OlmMachine`](xref:Uniform.Matrix.Protocol.Crypto.OlmMachine)s against it: messages between users, recovery from backup, key forwarding and emoji verification.

```csharp
await using TestHomeserver server = await TestHomeserver.StartAsync(new Dictionary<string, string?> { ["Server:Registration"] = "Open" });
MatrixClient alice = server.CreateClient();
await alice.RegisterAsync("alice", "password");
string roomId = await alice.CreateRoomAsync(new CreateRoomRequest { Name = "Kitchen" });
```
