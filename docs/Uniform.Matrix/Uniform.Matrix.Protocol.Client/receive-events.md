# Receive events

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
