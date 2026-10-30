---
title: Log in
uid: Uniform.Matrix.Protocol.Client.LogIn.Article
---
# Log in

```csharp
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

using HttpClient http = new();

// Find the homeserver for the server part of the user ID
UserId userId = UserId.Parse("@alice:matrix.org");
Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, userId.ServerName);
MatrixClient client = new(http, homeserver);

// Log in with a full user ID or a localpart
await client.LoginWithPasswordAsync(userId.ToString(), password, deviceDisplayName: "My app");
```

Save `client.Session` to resume later without the password:

```csharp
MatrixClient resumed = new(http, storedSession);
```

[LogoutAsync](xref:Uniform.Matrix.Protocol.Client.MatrixClient.LogoutAsync*) invalidates the token and deletes the device.
