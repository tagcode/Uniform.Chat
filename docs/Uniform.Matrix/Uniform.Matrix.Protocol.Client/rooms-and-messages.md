# Rooms and messages

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
