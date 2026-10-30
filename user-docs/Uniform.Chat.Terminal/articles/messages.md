---
title: Messages
uid: Uniform.Chat.Terminal.Messages.Article
---
# Messages

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
| Open link | L | Messages with room or user links, such as `https://matrix.to/#/#room:server`. Opens a room you are in or a direct chat you have, or offers to join the room or start a chat. With several links, pick one. |
| Delete | D | Your own messages only, after confirmation. Deletes it for everyone. |
| Report | P | Others' messages. Sends a report, with the reason you pick, to the admins of your server. |
| Block sender | B | Others' messages. Hides the sender's messages and invites, as `/ignore` does. |

Esc returns to the input line. Replies show what they answer, like `<bob> [re alice: lunch at noon?] sure`.

## Reporting and blocking

Report messages, people and rooms that break the rules: spam, harassment, sexual or violent content, or anything illegal. Reports go to the admins of **your** server; the person reported is not told. Report a message from its actions, a person with `/report @user:server`, and the open room with `/report`.

`/ignore @user:server` blocks someone: their messages and invites no longer show, on all your devices, and they are not told. `/ignore` alone lists whom you block, and `/unignore @user:server` shows their new messages again.
