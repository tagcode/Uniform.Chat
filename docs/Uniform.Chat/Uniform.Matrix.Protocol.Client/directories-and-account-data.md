---
title: Directories and account data
uid: Uniform.Matrix.Protocol.Client.DirectoriesAndAccountData.Article
---
# Directories and account data

```csharp
PublicRoomsResponse rooms = await client.GetPublicRoomsAsync(searchTerm: "dotnet", limit: 20);   // like /list
UserDirectoryResponse users = await client.SearchUsersAsync("bob");

string dmRoomId = await client.CreateDirectRoomAsync("@bob:matrix.org");    // also updates m.direct
await client.SetTagAsync(roomId, RoomTags.Favourite);
await client.IgnoreUserAsync("@spammer:example.org");
```
