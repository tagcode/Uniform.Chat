# Desktop and Android app

Uniform.Matrix also comes as a graphical app for Windows, Linux and Android. It uses the same engine as the terminal client, so messages, encryption and the outbox work the same way.

![The desktop app with a room open](../images/desktop.png)

> [!NOTE]
> The graphical app is newer than the terminal client. It covers everyday chatting and encryption; notifications on Windows and Android are still to come.

## Install

### Windows

Download `uniform-matrix-desktop-<version>-win-x64.zip` (or `win-arm64`) from the [releases](https://github.com/tagcode/Uniform.Matrix/releases), unpack it anywhere and run `Uniform.Matrix.Client.Desktop.exe`. Windows may warn that the app is from an unknown publisher, because it is not signed yet; choose *More info → Run anyway*.

### Linux

Download `uniform-matrix-desktop-<version>-linux-x64.tar.gz` (or `linux-arm64`), unpack it and run the app:

```bash
tar -xzf uniform-matrix-desktop-*-linux-x64.tar.gz
cd uniform-matrix-desktop-*-linux-x64
./Uniform.Matrix.Client.Desktop
```

To add it to the application menu, run `./install-desktop-entry.sh` in the same folder. Run it again if you move the folder, and `./install-desktop-entry.sh --uninstall` removes the entry.

### Android

There is no store release yet. To try it, build and install it on a phone connected with USB debugging; see [Building and testing](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/dev/articles/building-and-testing.md).

### From source

```bash
dotnet run --project src/Uniform.Matrix.Client.Desktop
```

## Sign in

Enter your full user ID, such as `@alice:matrix.org`, and your password. The server is found from the user ID; give its address only when it has no discovery file, for example `http://localhost:8008` for a server on your own computer.

The app remembers the account, so the next start opens the chat without asking. *⋮ → Sign out* logs the device out on the server and forgets the account.

## Rooms

The room list shows invites first, then favourites, other rooms and low-priority rooms. A number shows unread messages; it is highlighted when someone mentions you.

The box above the list does three things, and its button tells which:

| Type | Button | Does |
|---|---|---|
| `#room:server` or `!id:server` | Join | Joins the room. |
| `@user:server` | Chat | Starts a direct chat and invites the user. |
| Anything else | Create | Creates a private room by that name. |

New private rooms and direct chats are end-to-end encrypted. The room opens once the server has it.

An invite opens with *Join* and *Decline* buttons.

The room's *⋮* menu sets the topic, invites people, lists the members, verifies them and leaves the room.

## Messages

Type in the box at the bottom and press Enter to send; Shift+Enter starts a new line. Markdown works: `**bold**`, `*italic*`, `` `code` ``, lists and quotes. `/me waves` sends an action.

Messages from the same person within a few minutes are grouped under one name. Edited messages say *(edited)*, deleted ones *Message deleted*, and reactions are counted under the message. A message waits as *Sending…* until the server has it; it is kept on the device, so it is sent later even if the connection drops or the app closes.

Scroll to the top to load older messages.

Click a link to open it in your browser. Links to Matrix rooms and people (`matrix.to`) stay in the app: a room you are in opens, and other rooms and people go into the box above the room list, ready for *Join* or *Chat*.

Right-click a message, or press and hold it on a phone, for its actions:

- *Reply* answers it; the reply shows a quote of the message. Esc or ✕ above the message box stops replying.
- *React* adds 👍, ❤️, 😂, 🎉, 😮 or 😢.
- *Edit* puts your own message back in the message box; Enter sends the new text.
- *Copy text* copies it.
- *Delete* removes your own message for everyone.

## Files

📎 next to the message box sends files. On the desktop you can also drag files onto the room, or paste copied files or an image, such as a screenshot, into the message box with Ctrl+V. In encrypted rooms files are encrypted before upload.

Images show a preview in the room; click it to open the image. Under a file or image, *Open* opens it in the app your system uses for its type, and *Save…* saves it where you choose. On Android, use *Save…*.

## Security

*⋮ → Security and recovery* at the top of the room list shows this device's encryption keys and the account's state, and offers what is missing:

- **Set up recovery**, when the account has none yet: it creates cross-signing keys, a backup of message keys and a recovery key. The server may ask for your password first. Write the recovery key down or copy it somewhere safe; it is shown only once.
- **Unlock encrypted history**: messages sent before this device signed in are encrypted with keys the device does not have. Enter the recovery key or passphrase, from this app or another one such as Element, and choose *Recover*. The app restores the keys from the backup, shows the messages, and verifies this device.
- **Verify with another device**: compare emoji with another device where you are signed in. Accept the request there, check that both show the same emoji in the same order, and choose *They match* on both.

When another device asks to verify this one, the app shows the request; choose *Accept* and compare the emoji the same way.

Below that, *Devices* lists where you are signed in. *Verify* asks a device that is not verified yet to compare emoji, and *Sign out…* signs a lost or old device out after you enter your password.

To verify another person, open the room's *⋮* menu and choose *Verify* next to their name. They accept on their device, and you both compare the emoji.

## Notifications

On Linux, the app shows a notification for direct messages, messages that mention you and new invites while its window is in the background. A newer message in the same chat replaces the previous notification. The window title counts rooms with unread messages, such as *Uniform.Matrix (2)*.

Windows and Android do not show notifications yet.

## Settings

*⋮ → Settings* at the top of the room list sets the theme (follow the system, light or dark), the text size, and whether to show notifications. Changes are saved right away.

## Phones and narrow windows

On phones, and in windows narrower than 720 pixels, the room list and the room take the whole width in turn. Back, or *‹* in the room header, returns to the list.

## Where things are kept

| What | Desktop | Android |
|---|---|---|
| Messages, keys and files | `~/.local/share/uniform-matrix-client-desktop/` | the app's private storage |
| Remembered account and settings | `~/.config/uniform-matrix-client-desktop/settings.json` | the app's private storage |

On Windows, `~` is your user folder, for example `C:\Users\alice`. Uninstalling on Android removes everything; Android backups are turned off, so encryption keys never leave the phone.
