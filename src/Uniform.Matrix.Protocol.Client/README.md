# Uniform.Matrix.Protocol.Client

An HTTP client of the Matrix [Client-Server API](https://spec.matrix.org/latest/client-server-api/): discovery, login and registration, sync, rooms and messages, media, devices, end-to-end encryption keys and key backup.

```bash
dotnet add package Uniform.Matrix.Protocol.Client
```

## Log in

```csharp
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

using HttpClient http = new();

// Find the homeserver for the server part of the user ID
UserId userId = UserId.Parse("@alice:matrix.org");
Uri homeserver = await HomeserverDiscovery.DiscoverAsync(http, userId.ServerName);
MatrixClient client = new(http, homeserver);
await client.LoginWithPasswordAsync(userId.ToString(), password, deviceDisplayName: "My app");
```

Store `client.Session` to resume later without the password: `new MatrixClient(http, session)`.

## Receive and send messages

```csharp
await foreach (SyncResponse sync in client.SyncLoopAsync(cancellationToken: cancellationToken))
{
    foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join)
    {
        foreach (ClientEvent clientEvent in room.Timeline.Events)
        {
            if (clientEvent.Type != EventTypes.RoomMessage || !clientEvent.TryGetContent(out MessageContent? message)) continue;
            Console.WriteLine($"[{roomId}] {clientEvent.Sender}: {message.Body}");
        }
    }
}

string roomId = await client.JoinRoomAsync("#lobby:matrix.org");
await client.SendEventAsync(roomId, EventTypes.RoomMessage, new MessageContent { MessageType = MessageTypes.Text, Body = "Hello" });
```

`SyncLoopAsync` long-polls and retries network errors with backoff; store `NextBatch` with the session and pass it as `since` to continue where you left off.

## Notes

- One `MatrixClient` serves one homeserver and one session. The caller owns the `HttpClient`, so it works with `IHttpClientFactory`; keep its timeout longer than the sync long-poll.
- Rate-limited requests are retried after the server's `retry_after_ms`; other errors throw `MatrixException`.
- Endpoints that need [user-interactive auth](https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api), such as deleting devices, take the password and answer the server's challenge.
- For end-to-end encryption, see [Uniform.Matrix.Protocol.Crypto](https://www.nuget.org/packages/Uniform.Matrix.Protocol.Crypto); for a client with local storage and room state, [Uniform.Matrix.Client](https://www.nuget.org/packages/Uniform.Matrix.Client).

More in [the developer guide](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Protocol.Client/index.md).

## About

Part of [Uniform.Matrix](https://github.com/tagcode/Uniform.Matrix), a pure .NET implementation of the [Matrix specification](https://spec.matrix.org/latest/). Version 0.1 is an early pre-release: APIs may still change.

- [How it works](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Protocol.Client/index.md)
- [Building](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/building.md) and [testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/testing.md)
