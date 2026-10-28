---
title: Interface
uid: Uniform.Matrix.Client.Terminal.Interface.Article
---
# Interface

```text
 File  Room  View  Help
┌Rooms─────────────────┐┌General — Welcome to the general chat────────────┐┌Members (3)──────┐
│ 1  General (2)       ││12:01 <alice> hi all                             ││alice            │
│ 2 @bob               ││12:02 <bob> hello! this is a long message that   ││bob              │
│ 3 +Secret project    ││            wraps under the text                 ││carol            │
│                      ││12:02 -!- carol joined                           ││                 │
│                      ││12:03  * alice waves                             ││                 │
│                      ││12:04 <you> on my way  [sending…]                ││                 │
└──────────────────────┘└─────────────────────────────────────────────────┘└─────────────────┘
 hello everyone_
 F1 Help │ Ctrl+N Rooms │ Ctrl+O Join │ Ctrl+Q Quit │ Connected as @you:matrix.org   bob typing…
```

- **Menu bar** (F10 or the mouse) has everything the client can do: File, Room, View and Help.
- **Rooms** lists invites (`+`), favourites, other rooms and low-priority rooms. Direct chats are marked `@`. The number is the Alt key for the room; unread counts are in brackets, and rooms that mention you are yellow.
- **Message log** shows the selected room, with its name and topic in the frame. Long lines wrap under the text, own messages are cyan, and messages still being sent say `[sending…]`. A day change is marked with a line.
- **Members** lists who is in the room, admins (`@`) and moderators (`%`) first, as in irssi; invited users are marked `+`. F2 hides it.
- **Input line** sends what you type to the room. Lines starting with `/` are commands.
- **Status bar** shows key hints, the connection state and who is typing. Others see when you are typing too.

Messages you write are stored first and sent in order, so they survive a lost connection or a restart. If the server refuses one, for example because you left the room, it is shown in red with the reason.

## Menus

| Menu | Items |
|---|---|
| File | Encryption, Verify this device, Devices, Settings, Log out, Quit |
| Room | Join, Room directory, New room, Direct message, Send file, Invite, Set topic, Set name, Set avatar, Power level, Kick, Ban, Unban, Enable encryption, Leave |
| View | Older messages, Members, Select message, Logs, Next room, Previous room |
| Help | Commands and keys, About |

## Keys

| Key | Action |
|---|---|
| Alt+1 … Alt+9 | Go to room 1–9 |
| Ctrl+N, Ctrl+P | Next, previous room |
| Up, Down in the room list | Look through rooms; focus stays in the list (click the list, or press Tab on an empty input line, to get there) |
| Enter in the room list | Type in the selected room; for an invite, join or reject it |
| PageUp, PageDown | Scroll messages |
| Ctrl+PageUp | Load older messages |
| Ctrl+O | Join a room |
| Ctrl+R | Room directory, also of other servers |
| F1 | Help |
| F2 | Show or hide members |
| Ctrl+Up, Enter | Select a message and act on it |
| Tab | Complete a member's name; at the start of the line it adds `: ` |
| Esc | Cancel a reply or edit; leave the message log |
| F10 | Open the menu |
| Ctrl+Q | Quit |
