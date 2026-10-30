---
title: Security
uid: Uniform.Matrix.Client.Desktop.Security.Article
---
# Security

[!include[](../../Uniform.Matrix.Core/encryption.md)]

*⋮ → Security and recovery* at the top of the room list shows this device's encryption keys and the account's state, and offers what is missing:

- **Set up recovery**, when the account has none yet. The server may ask for your password first.
- **Unlock encrypted history**: messages sent before this device signed in are encrypted with keys the device does not have. Enter the recovery key or passphrase, from this app or another one such as Element, and choose *Recover*. The app restores the keys from the backup, shows the messages, and verifies this device.
- **Verify with another device** where you are signed in: accept the request there, and when both show the same emoji, choose *They match* on both.

When another device asks to verify this one, the app shows the request; choose *Accept* and compare the emoji the same way.

Below that, *Devices* lists where you are signed in. *Verify* asks a device that is not verified yet to compare emoji, and *Sign out…* signs a lost or old device out after you enter your password.

To verify another person, open the room's *⋮* menu and choose *Verify* next to their name. They accept on their device, and you both compare the emoji.

At the end, *Blocked users* lists the people you have blocked, with *Unblock*, and *Delete account…* deletes your account, as Getting started tells.
