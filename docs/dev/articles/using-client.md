# Using the protocol client

## Log in

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

## Receive events

```csharp
Filter filter = new() { Room = new RoomFilter { Timeline = new RoomEventFilter { Limit = 50, LazyLoadMembers = true } } };
string filterId = await client.CreateFilterAsync(filter);

await foreach (SyncResponse sync in client.SyncLoopAsync(filter: filterId, onRetry: (exception, wait) => Console.Error.WriteLine($"Sync failed, retrying in {wait}: {exception.Message}"), cancellationToken: cancellationToken))
{
    foreach ((string roomId, JoinedRoom room) in sync.Rooms.Join)
    {
        foreach (ClientEvent clientEvent in room.Timeline.Events)
        {
            if (clientEvent.Type != EventTypes.RoomMessage) continue;
            if (!clientEvent.TryGetContent(out MessageContent? message)) continue;
            Console.WriteLine($"[{roomId}] {clientEvent.Sender}: {message.Body}");
        }
    }

    foreach ((string roomId, InvitedRoom invite) in sync.Rooms.Invite)
        Console.WriteLine($"Invited to {roomId}");
}
```

Store the last `NextBatch` together with the session and pass it as `since` to continue where you left off.

When `room.Timeline.Limited` is true, the server skipped events. Fetch them with [GetMessagesAsync](xref:Uniform.Matrix.Protocol.Client.MatrixClient.GetMessagesAsync*) starting from `room.Timeline.PrevBatch`.

## Track rooms

Room state is in the `Uniform.Matrix.Client` library (`using Uniform.Matrix.Client.State;`), which builds on the protocol client:

```csharp
RoomStore store = new(client.UserId!);

await foreach (SyncResponse sync in client.SyncLoopAsync(since: store.NextBatch, cancellationToken: cancellationToken))
{
    SyncChanges changes = store.Apply(sync);

    foreach (Room room in changes.Invited)
        Console.WriteLine($"{room.InviterUserId} invited you to {room.DisplayName}");

    foreach (TimelineBatch batch in changes.Timeline)
    {
        if (batch.Limited) Console.WriteLine($"[{batch.Room.DisplayName}] -- some messages skipped --");
        foreach (ClientEvent clientEvent in batch.Events)
        {
            if (!clientEvent.TryGetContent(out MessageContent? message) || message.Body is null) continue;
            Console.WriteLine($"[{batch.Room.DisplayName}] <{batch.Room.GetMemberDisplayName(clientEvent.Sender)}> {message.Body}");
        }
    }
}
```

Scroll back with `await store.BackfillAsync(client, room)`, which prepends older events to `room.Timeline` until `room.HasMoreHistory` is false.

## Rooms and messages

```csharp
string roomId = await client.JoinRoomAsync("#general:matrix.org");

await client.SendTextAsync(roomId, "hello");
await client.SendEmoteAsync(roomId, "waves");                // * alice waves
await client.SetRoomTopicAsync(roomId, "Friendly chat");

MessagesResponse page = await client.GetMessagesAsync(roomId, limit: 50);   // newest first
await client.SetReadMarkersAsync(roomId, page.Chunk[0].EventId, page.Chunk[0].EventId);

await client.LeaveRoomAsync(roomId);
```

Rich messages:

```csharp
await client.SendReplyAsync(roomId, originalEvent, "agreed");      // mentions the original sender
await client.SendEditAsync(roomId, myEventId, "fixed typo");
await client.SendReactionAsync(roomId, eventId, "👍");
await client.SendHtmlAsync(roomId, "hi Bob", "hi <b>Bob</b>", new Mentions { UserIds = ["@bob:matrix.org"] });
```

Sends get a fresh transaction ID each time. To retry a send safely after a network error, pass the same `transactionId` again; the server then returns the original event instead of sending a duplicate.

## Directories and account data

```csharp
PublicRoomsResponse rooms = await client.GetPublicRoomsAsync(searchTerm: "dotnet", limit: 20);   // like /list
UserDirectoryResponse users = await client.SearchUsersAsync("bob");

string dmRoomId = await client.CreateDirectRoomAsync("@bob:matrix.org");    // also updates m.direct
await client.SetTagAsync(roomId, RoomTags.Favourite);
await client.IgnoreUserAsync("@spammer:example.org");
```

## Files and devices

```csharp
await using FileStream file = File.OpenRead("cat.png");
await client.SendFileAsync(roomId, file, "cat.png", "image/png");         // uploads, then sends m.image

using MediaDownload media = await client.DownloadMediaAsync(MxcUri.Parse(message.Url!));
await using FileStream copy = File.Create(media.FileName ?? "download");
await media.Content.CopyToAsync(copy);

IReadOnlyList<Device> devices = await client.GetDevicesAsync();
try
{
    await client.DeleteDevicesAsync(["OLDDEVICE"], password: null);
}
catch (UserInteractiveAuthException ex) when (ex.AcceptsPassword)
{
    await client.DeleteDevicesAsync(["OLDDEVICE"], AskPassword());          // answers the challenge
}
```

## Errors

```csharp
try
{
    await client.JoinRoomAsync("#missing:matrix.org");
}
catch (MatrixException ex) when (ex.Errcode == ErrorCodes.NotFound)
{
    Console.Error.WriteLine("No such room");
}
catch (MatrixException ex) when (ex.Errcode == ErrorCodes.Forbidden)
{
    Console.Error.WriteLine($"Not allowed: {ex.Error}");
}
```

[MatrixException](xref:Uniform.Matrix.Protocol.Core.MatrixException) is thrown when the server returns a Matrix error body. Rate-limited requests are retried automatically before an exception is thrown, unless the server asks to wait longer than `MaxRateLimitDelay` (30 seconds); then `ex.RetryAfter` says how long to wait. Other failures, such as an HTML error page from a proxy, throw `HttpRequestException` with the HTTP status code.
