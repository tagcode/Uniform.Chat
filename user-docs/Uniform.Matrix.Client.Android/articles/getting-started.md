# Getting started

The Uniform.Matrix app for Android: rooms, messages, files and end-to-end encryption. It shows the room list and a room one at a time.

> [!NOTE]
> The app is in early development. It covers everyday chatting and encryption; notifications are still to come.

## Requirements

- Android 6 or later
- An account on a Matrix homeserver, for example `@alice:matrix.org`

## Install

There is no store release yet. To try it, build and install it on a phone connected with USB debugging; see the [developer notes of the Android app](https://github.com/tagcode/Uniform.Matrix/blob/main/docs/Uniform.Matrix/Uniform.Matrix.Client.Android/index.md).

## Sign in

Enter your full user ID, such as `@alice:matrix.org`, and your password. The server is found from the user ID; give its address only when it has no discovery file.

The app remembers the account, so the next start opens the chat without asking. *⋮ → Sign out* logs the device out on the server and forgets the account.

Next, see [Rooms](rooms.md) and [Messages and files](messages.md).

## Where things are kept

Messages, encryption keys, files, the remembered account and the settings are in the app's private storage, which other apps cannot read. Uninstalling removes everything. Android backups are turned off, so encryption keys never leave the phone.
