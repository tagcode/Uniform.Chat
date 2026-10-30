---
title: Privacy policy
uid: Uniform.Chat.Android.PrivacyPolicy.Article
---
# Privacy policy

*Last updated: 30 October 2026*

This policy covers the Uniform Chat apps: the Android app, the desktop app and the terminal client. They are free, open-source [Matrix](https://matrix.org) chat clients, developed in the open at [github.com/tagcode/Uniform.Chat](https://github.com/tagcode/Uniform.Chat).

## In short

The developer of the apps collects nothing. The apps have no analytics, advertising, crash reporting or tracking, and send nothing to the developer. What you write and say goes to the Matrix server you choose, and from there to the people you talk with.

## What the apps send, and where

The apps talk only to:

- **Your Matrix server**, the homeserver of your user ID, which you choose when you sign in or create an account. It receives what any Matrix app sends: your user ID and password when you sign in, your messages, files, reactions and profile, the rooms you join, read receipts and typing notices, the names of your devices, and your encryption keys' public parts. Your server keeps them and passes them on to the members of your rooms, who may be on other servers.
- **Call relays** (TURN servers) that your server names, during voice calls, when the two phones cannot reach each other directly. Call audio is encrypted between the two ends; the relay cannot listen to it.
- **Media**: pictures and files in your rooms are downloaded through your server.

In encrypted rooms, which are the default for new private rooms and direct chats, messages and files are end-to-end encrypted: only the members' devices can read them, not the servers. The servers still see who talks with whom, and when.

What your server does with the data is set by its operator's own privacy policy and terms, which some servers show when you create an account. The developer of the apps does not run your server, unless you chose a server run by the developer, in which case its own policy applies.

## What stays on your device

The apps store your session, your messages, your encryption keys and your settings on your device, so that they work offline and start quickly. The encryption keys are stored encrypted. Signing out ends the session on the server; uninstalling the Android app removes everything it stored.

The apps keep a log of their own activity on the device, to help you find problems. It stays on the device unless you share it yourself.

## Permissions on Android

- **Internet and network state**: to reach your server, and to reconnect when the phone changes networks.
- **Microphone**: asked for the first time you place or answer a voice call, and used only during calls.
- **Notifications**: to show new messages, incoming calls and an ongoing call.
- **Foreground services**: to keep receiving messages and calls while the app is in the background, and to keep a call going with the screen off. A notification shows while they run.
- **Full-screen notifications, vibration and keeping the screen on**: to ring for an incoming call, also on the lock screen.

## Deleting your data

To delete your account, choose *Delete account* in the app: in *⋮ → Security and recovery* on Android and the desktop, or *File → Delete account* in the terminal client. You leave every room, your name and picture are removed from the server, and every device is signed out. Messages you sent before stay with the people who received them, as with email. For anything else your server keeps, ask its operator.

## Children

The apps are not meant for children under 13.

## Changes and contact

Changes to this policy are published here, with a new date at the top. Questions about the apps and this policy are welcome as an issue at [github.com/tagcode/Uniform.Chat/issues](https://github.com/tagcode/Uniform.Chat/issues).
