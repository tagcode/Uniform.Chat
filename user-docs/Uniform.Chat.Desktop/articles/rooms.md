---
title: Rooms
uid: Uniform.Chat.Desktop.Rooms.Article
---
# Rooms

The room list shows invites first, then favourites, other rooms and low-priority rooms. A number shows unread messages; it is highlighted when someone mentions you.

Right-click a room in the list, or press and hold it on a phone, for its menu, without opening it. On a phone, a sheet shows the room's address, topic, members, encryption and ID above the actions; on the desktop, *Details…* at the end of the menu shows it.

- *Mark as read* clears the unread count.
- *Favourite* lists the room first, *Low priority* last; a room is one or the other, and *Remove from favourites* or *Remove low priority* puts it back among the others. Your other devices and Matrix apps sort it the same way.
- *Copy link* copies a `matrix.to` link to the room, for inviting people.
- *Report room* reports it to the admins of your server.
- *Leave room* leaves it.
- For an invite: *Join* or *Decline*.

The box above the list does three things, and its button tells which:

| Type | Button | Does |
|---|---|---|
| `#room:server`, `!id:server` or a room link | Join | Joins the room. |
| `@user:server` | Chat | Starts a direct chat and invites the user. |
| Anything else | Create | Creates a private room by that name. |

New private rooms and direct chats are end-to-end encrypted. The room opens once the server has it.

Rooms of newer versions have IDs without a server, like `!abc123`. To join one by ID, give a server that is in the room after it, separated by a space:

```text
!abc123 matrix.org
```

Links to such rooms carry their servers, and fill them in for you.

An invite opens with *Join* and *Decline* buttons.

The room's *⋮* menu sets the topic, invites people, lists the members, verifies them and leaves the room.

## Room directory

*🔍* next to the box, or *Room directory…* in the *⋮* menu at the top, lists the public rooms of your server. Type in *Search* and press Enter to find rooms by name, topic or address. To browse another server, type its name in *Server*, such as `matrix.org`; leave it empty for your own. *Join* joins the room, which then opens.
