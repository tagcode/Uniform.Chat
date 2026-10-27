# Using the app

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

[!include[](../../shared/encryption.md)]

*⋮ → Security and recovery* at the top of the room list shows this device's encryption keys and the account's state, and offers what is missing:

- **Set up recovery**, when the account has none yet. The server may ask for your password first.
- **Unlock encrypted history**: messages sent before this device signed in are encrypted with keys the device does not have. Enter the recovery key or passphrase, from this app or another one such as Element, and choose *Recover*. The app restores the keys from the backup, shows the messages, and verifies this device.
- **Verify with another device** where you are signed in: accept the request there, and when both show the same emoji, choose *They match* on both.

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
