---
title: Getting started
uid: Uniform.Matrix.Client.Android.GettingStarted.Article
---
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

## Create an account

On the sign-in form, choose *Create an account*. Enter the user ID you want, such as `@alice:example.org`, where the part after `:` is the server, and a password of your own. Many servers do not let apps create accounts: then make the account on the server's website and sign in here.

When the server needs more, the form asks for it on the first try:

- a **registration token**, which the server's admin gives to the people they invite;
- its **policies**, such as terms of service: open them from their links, then tick *I have read and accept these policies*.

The app then creates the account and signs in to it.

## Delete the account

*⋮ → Security and recovery…*, then *Delete account…* at the end. Confirm with your password. You leave every room, your name and picture are removed, and every device is signed out. The account cannot be used again, and nobody can take its user ID.

Next, see [Rooms](xref:Uniform.Matrix.Client.Android.Rooms.Article) and [Messages and files](xref:Uniform.Matrix.Client.Android.Messages.Article).

## Where things are kept

Messages, encryption keys, files, the remembered account and the settings are in the app's private storage, which other apps cannot read. Uninstalling removes everything. Android backups are turned off, so encryption keys never leave the phone.
