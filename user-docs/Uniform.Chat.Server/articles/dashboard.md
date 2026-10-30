---
title: Run with the dashboard
uid: Uniform.Chat.Server.Dashboard.Article
---
# Run with the dashboard

In the unpacked folder:

```bash
./uniform-chat-server
```

When started from a terminal, the server opens its dashboard and serves at `http://localhost:8008`:

```text
┌Matrix server localhost───────────────────────────────────────────────────────────┐
│ File  Server                                                                     │
│ Listening:  http://localhost:8008                                                │
│ Uptime:     0:12:31                                                              │
│ Requests:   1520 answered, 3 in progress, 2 failed                               │
│ Accounts:   4 users, 3 rooms                                                     │
│                                                                                  │
│ Data:       /home/alice/.local/share/uniform-chat-server                         │
│ Log:        /home/alice/.local/share/uniform-chat-server/logs/2026-10-26…        │
│                                                                                  │
│ F9 or Alt+F for the menu; Server → Users to create accounts. Ctrl+Q stops it.    │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Listening** shows the addresses clients connect to.
- **Requests** counts client requests: answered, being answered now (syncing clients wait here for news), and failed with a server error.
- **Accounts** counts users and rooms, every few seconds.
- **Data** is the folder with the database and signing key; see [Files](xref:Uniform.Chat.Server.Files.Article).
- **Log** is this session's log file.

The dashboard refreshes every second. The server keeps serving while menus and windows are open.

| Key | Action |
|---|---|
| F9 or Alt+F | Open the File menu |
| File → Settings… | View and change the server's settings |
| File → Logs… | Read this session's log and earlier ones |
| Server → Users… | Create users, reset passwords, give admin rights, deactivate accounts |
| Server → Rooms… | See rooms and list them in the public room directory |
| Server → Reports… | Review what users reported |
| Ctrl+Q or File → Quit | Stop the server |

## Settings window

File → Settings lists every option with its current value, where the value comes from, and a description of the selected option.

- **Edit** (or Enter) changes the selected option; options with fixed values offer a list.
- **Apply** uses a change at once, until the server stops. Options marked ↻, such as the server name and the data folder, take effect only after a restart.
- **Save** writes the changes to `~/.config/uniform-chat-server/settings.json`, which is read at every start.
- **Reset** undoes one step at a time: first an edit that is not applied, then an applied change that is not saved. Pressed again, it removes the option from the settings file on save, so the default applies again.
- Edited options are marked `*`, applied but unsaved ones `+`. Values given on the command line override the settings file.
- Registration tokens are secret: they are never shown in the window.

To open registration for a while, for example: select `Server:Registration`, choose `Open` and Apply. New users can register at once. Set it back to `Disabled` and Apply when they are done. Nothing is saved unless you choose Save.

## Users window

Server → Users lists every account with its display name, logged-in devices, joined rooms, and whether it is an admin or deactivated.

- **New** asks for the user name, such as `alice` for `@alice:localhost`, the password twice, and whether the user is a server admin. Registration does not need to be open for this.
- **Password** sets a new password for the selected user and logs out all their devices, for example when they forgot it.
- **Admin** gives or takes server admin rights. Admins may delete any room alias and change any room's directory listing.
- **Deactivate** logs out every device of the user and stops them logging in; pressed again, it reactivates the account. Their rooms and messages stay.

## Rooms window

Server → Rooms lists every room with its name, alias, joined members, and whether it is public (listed in the directory) or encrypted.

- **Publish** lists the selected room in the public room directory, or removes it. Listing a room does not change who may join; that follows the room's own join rule.
- Messages of encrypted rooms are encrypted on the members' devices, so the server cannot show or read them.

## Reports window

Users can report a message, a room or another user from their client. Server → Reports lists the open reports, newest first, with who reported, what about and why; the dashboard shows how many are open.

- The details below the list show the whole reason, and the reported message's text when the server has it. Messages of encrypted rooms cannot be read by the server.
- **Resolve** marks the selected report as dealt with, so it leaves the list; pressed on a resolved report, it opens it again.
- **Show resolved** shows or hides the resolved reports.
- **Delete** removes a report for good, after confirmation.

## Logs window

File → Logs shows the log of this session, and lets you open earlier sessions. Each start writes its own file in `logs` under the data folder. The newest 20 are kept. Changes made in the Users and Rooms windows are logged, such as `Admin created user @alice:localhost`, so the log also shows what was done; passwords never appear in it.
