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
- **Members** lists who is in the room; F2 hides it.
- **Input line** sends what you type to the room. Lines starting with `/` are commands.
- **Status bar** shows key hints, the connection state and who is typing. Others see when you are typing too.

Messages you write are stored first and sent in order, so they survive a lost connection or a restart. If the server refuses one, for example because you left the room, it is shown in red with the reason.

## Encryption

Messages in encrypted rooms are encrypted on your device before they leave it, and only the devices of the people in the room can read them; the server only sees ciphertext. The room's frame shows `[encrypted]`.

- New rooms and direct chats are encrypted by default. Room → Enable encryption turns it on for an existing room; it cannot be turned off again.
- File → Encryption shows this device's ID and fingerprint, and whether cross-signing, key backup and the recovery key are set up. Other clients, such as Element, list the same fingerprint for this session.
- A message that arrived before its key shows `[encrypted message; the key has not arrived]` and is decrypted as soon as the key comes. Your other verified devices are asked for missing keys automatically.
- Files sent to encrypted rooms are encrypted too; previews and Save file decrypt them on this device.
- If encryption is not available on this computer, messages to encrypted rooms are not sent; they are shown in red with the reason, so nothing is ever sent unencrypted by mistake.

### Recovery key and key backup

The recovery key lets a new login prove it is you and read messages from before it existed.

- **Set up recovery** (File → Encryption) creates your cross-signing keys, a key backup on the server and the recovery key. It is offered only when your account has no cross-signing yet; if another client set it up, use that client's recovery key instead. The server may ask for your password. Write the recovery key down or copy it: it is shown once.
- While a backup is on, new room keys are uploaded to it, encrypted so the server cannot read them.
- **Recover** (File → Encryption, or `/recover`) on a new login asks for the recovery key, or the passphrase if you set one in another client. It verifies this device, restores the key backup and decrypts the stored messages that were waiting for keys.

### Verifying devices

Verification compares seven emoji on two devices to prove that no one is in between.

- File → Verify, `/verify`, or Verify in File → Devices starts verifying your other device. `/verify @user:server` verifies another person.
- Accept on the other device. Both show the same emoji; choose **They match** on both. If they differ, choose **They don't match**; the verification is cancelled and nothing is trusted.
- After verifying your own device, it is signed with your cross-signing key, and a new device receives the cross-signing and backup keys, so it does not need the recovery key.
- File → Devices shows each device as verified, cross-signed (verified through your cross-signing keys) or unverified. Room keys and secrets are shared only with your verified or cross-signed devices.

## Formatting

Type Markdown and it is sent formatted, so other clients such as Element show bold, links and code:

| You type | Others see |
|---|---|
| `**bold**`, `_italic_`, `~~strike~~` | **bold**, _italic_, ~~strike~~ |
| `` `code` `` | `code` |
| `[the spec](https://spec.matrix.org)` | a link named "the spec" |
| `https://example.org` | a clickable link |

Names completed with Tab become mentions, which notify that person. HTML you type is shown as text, not run as markup. Start a line with `/plain` to send it exactly as typed.

Formatted messages from others are shown as text: `*bold*`, `_italic_`, `~strike~`, `` `code` ``, `• list items`, `> quotes`, code blocks as they are, links as `name (address)`, mentions by display name, and spoilers as `[spoiler]`.

## Acting on messages

Press **Ctrl+Up** to select the newest message, move with the arrow keys, and press **Enter** for its actions:

| Action | Key | What it does |
|---|---|---|
| Reply | R | The input line shows `[re nick]`; your next message is a reply. Esc cancels. |
| React | A | Pick a common emoji or type your own reaction. |
| Edit | E | Your own messages only. The text moves to the input line, `[edit]` is shown; Enter saves, Esc cancels. |
| View image | V | Images only. Opens the full image in a viewer. |
| Save file | S | Attachments only. Downloads the file to `~/Downloads`. |
| Delete | D | Your own messages only, after confirmation. Deletes it for everyone. |

Esc returns to the input line. Replies show what they answer, like `<bob> [re alice: lunch at noon?] sure`.

## Files

Room → Send file (or `/upload`) opens a file browser; the file is uploaded and sent as an image, video, audio or file message. Attachments show as `[image: cat.png, 120 KB]`; save them with the Save file action.

## Images

Images in the log get a preview under the message, drawn with shade blocks:

```text
20:01 <alice> [image: ring.png, 1 KB]
      ░▒▓▓█████▓▓▒░
    ░▓██▓▒░    ░▒▓██▓░
    ▓██░          ░██▓
    ▓██░          ░██▓
    ░▓██▓▒░    ░▒▓██▓░
      ░▒▓▓█████▓▓▒░
```

The preview grows with the window: by default it uses the full width of the message log and up to three quarters of its height, and it is redrawn when you resize the terminal. It is made from an 800×600 thumbnail, so it loads quickly.

The View image action opens the full image: terminals that support the Kitty or Sixel graphics protocols (kitty, WezTerm, foot, Konsole, recent xterm) show real pixels; others show colored blocks. PNG, JPEG, GIF, BMP and TGA are supported; other formats such as WebP show `[no preview]` and can still be saved.

Previews are set in `appsettings.json`:

```json
"Ui": {
  "InlineImages": true,
  "ImagePreviewColumns": 0,
  "ImagePreviewRows": 0,
  "ImagePreviewStyle": "Blocks",
  "DarkBackground": true
}
```

| Setting | Meaning |
|---|---|
| `StartRoom` | Room ID or alias to open at start; the first room when unset |
| `InlineImages` | `false` turns previews off |
| `ImagePreviewColumns`, `ImagePreviewRows` | Largest preview in cells; `0` follows the window |
| `ImagePreviewStyle` | `Blocks` (` ░▒▓█`, dithered), `Ascii` (70 characters from ` .` to `@$`) or `Braille` (2×4 dots per cell, the most detail) |
| `DarkBackground` | `false` on a light terminal, so dark parts of the image are drawn |

## Devices

File → Devices (or `/devices`) lists your sessions with their last activity; this one is marked `*`. You can rename a device, or log out one or all other devices, which asks for your password. To end this session, use File → Log out.

## Settings

File → Settings (or `/settings`) lists every option with its current value, where the value comes from, and a description of the selected option.

- **Edit** (or Enter) changes the selected option; options with fixed values, such as yes/no or the image preview style, offer a list.
- **Apply** uses the changes now, until the client exits. Options marked ↻ take effect only after a restart.
- **Save** writes the changes to `~/.config/uniform-matrix-client-terminal/settings.json` (under `$XDG_CONFIG_HOME` when set), which is read at every start.
- **Reset** undoes one step at a time: first an edit that is not applied, then an applied change that is not saved. Pressed again, it removes the option from the settings file on save, so the default applies again.
- **Load** reads another settings file as changes to apply or save; **Export** writes the current settings to a file.

Edited options are marked `*`, applied but unsaved ones `+`. Values given on the command line override the settings file; the source column shows `command line` for them. The password is never shown or saved.

## Logs

View → Logs (or `/logs`) shows the log of this run and earlier ones; each run writes its own file, and the oldest beyond `Storage:LogFilesKept` are deleted. Pick a run on the left. **Level** cycles the lowest level shown, Alt+F focuses the text filter, and Enter shows a whole entry, such as an error with its stack trace. Warnings are yellow and errors red; the current run's log follows new lines while scrolled to the end.

## Menus

| Menu | Items |
|---|---|
| File | Encryption, Verify, Devices, Settings, Log out, Quit |
| Room | Join, Room directory, New room, Direct message, Send file, Invite, Set topic, Enable encryption, Leave |
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
| Ctrl+R | Room directory |
| F1 | Help |
| F2 | Show or hide members |
| Ctrl+Up, Enter | Select a message and act on it |
| Tab | Complete a member's name; at the start of the line it adds `: ` |
| Esc | Cancel a reply or edit; leave the message log |
| F10 | Open the menu |
| Ctrl+Q | Quit |

## Commands

| Command | Action |
|---|---|
| `/me text` | Action, shown as `* you text` |
| `/plain text` | Send without Markdown formatting |
| `/join #room:server` | Join a room |
| `/leave` | Leave the current room |
| `/topic text` | Set the topic |
| `/invite @user:server` | Invite a user |
| `/msg @user:server` | Start a direct chat |
| `/nick name` | Set your display name |
| `/list` | Browse public rooms |
| `/create name` | Create a private room |
| `/upload` | Send a file |
| `/devices` | Manage your sessions |
| `/settings` | View and change options |
| `/logs` | Read this and earlier session logs |
| `/encryption` | Show encryption, recovery and backup state |
| `/recover` | Unlock older messages with the recovery key |
| `/verify [@user:server]` | Verify your other device, or a user, with emoji |
| `/quit` | Exit |
