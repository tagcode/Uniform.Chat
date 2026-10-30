---
title: Joining rooms
uid: Uniform.Chat.Terminal.Joining.Article
---
# Joining rooms

Rooms can be on any Matrix server; your server joins them for you. You can join a room by its alias, its room ID or a link, find it in a room directory, or follow a link in a message.

## By alias, ID or link

Press **Ctrl+O**, or type `/join` followed by the room:

| You give | Example |
|---|---|
| An alias | `/join #matrix:matrix.org` |
| A room ID | `/join !abc123:example.org` |
| A link | `/join https://matrix.to/#/#matrix:matrix.org` or `/join matrix:r/matrix:matrix.org` |

Links can carry the servers to join through, as `?via=` in a matrix.to link. You can also give the servers after the room yourself:

```text
/join !abc123 matrix.org example.org
```

Rooms of newer versions have IDs without a server, like `!abc123`; joining them needs at least one server that is in the room. Aliases don't need servers, as the alias's server tells where the room is.

A room you are already in opens instead.

## Room directory

Press **Ctrl+R**, or type `/list`, to browse the public rooms of your server. Type a search in **Search** and press Enter.

To browse another server, type its name in **Server**, e.g. `matrix.org`, and press Enter; `/list matrix.org` opens the directory on that server directly. Leave **Server** empty for your own. The directory remembers the last server until you quit. Choosing **Join** joins the selected room through the server that listed it.

## Links in messages

Select a message with **Ctrl+Up** and press Enter. **Open link** follows its room or user link: a room you are in opens, and other rooms are offered to join. A user link opens your direct chat with them, or offers to start one. See [Acting on messages](xref:Uniform.Chat.Terminal.Messages.Article#acting-on-messages).
