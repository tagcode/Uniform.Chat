---
title: Commands
uid: Uniform.Matrix.Client.Terminal.Commands.Article
---
# Commands

| Command | Action |
|---|---|
| `/me text` | Action, shown as `* you text` |
| `/plain text` | Send without Markdown formatting |
| `/join #room:server` | Join a room by alias, room ID or link; see [Joining rooms](xref:Uniform.Matrix.Client.Terminal.Joining.Article) |
| `/leave` | Leave the current room |
| `/topic text` | Set the topic |
| `/invite @user:server` | Invite a user |
| `/name text` | Set the room name |
| `/avatar [file]` | Set the room avatar from an image; `/avatar none` removes it |
| `/kick @user:server [reason]` | Remove a member |
| `/ban @user:server [reason]` | Remove a member and stop them joining again |
| `/unban @user:server` | Let a banned user back |
| `/power @user:server level` | Set a power level: a number, or `admin`, `moderator` or `member` |
| `/op @user:server`, `/deop @user:server` | Make a member admin, or an ordinary member |
| `/msg @user:server` | Start a direct chat |
| `/nick name` | Set your display name |
| `/list [server]` | Browse the public rooms of your server, or of another server |
| `/create name` | Create a private room |
| `/upload` | Send a file |
| `/devices` | Manage your sessions |
| `/settings` | View and change options |
| `/logs` | Read this and earlier session logs |
| `/encryption` | Show encryption, recovery and backup state |
| `/recover` | Unlock older messages with the recovery key |
| `/verify [@user:server]` | Verify this device with your other devices, or a user, with emoji |
| `/quit` | Exit |
