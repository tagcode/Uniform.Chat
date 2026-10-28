# Track rooms

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
