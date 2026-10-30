---
title: Encryption and devices
uid: Uniform.Chat.Terminal.Encryption.Article
---
# Encryption and devices

## Encryption

[!include[](../../Uniform.Matrix.Core/encryption.md)]

In the terminal client, the room's frame shows `[encrypted]`.

- Room → Enable encryption turns it on for an existing room.
- File → Encryption shows this device's ID and fingerprint, and whether cross-signing, key backup and the recovery key are set up. Other clients, such as Element, list the same fingerprint for this session.
- A message that arrived before its key shows `[encrypted message; the key has not arrived]` and is decrypted as soon as the key comes. Your other verified devices are asked for missing keys automatically.
- Files sent to encrypted rooms are encrypted too; previews and Save file decrypt them on this device.
- If encryption is not available on this computer, messages to encrypted rooms are not sent; they are shown in red with the reason, so nothing is ever sent unencrypted by mistake.

### Recovery key and key backup

- **Set up recovery** (File → Encryption) is offered only when your account has no cross-signing yet. The server may ask for your password.
- While a backup is on, new room keys are uploaded to it.
- **Recover** (File → Encryption, or `/recover`) on a new sign-in asks for the recovery key, or the passphrase if you set one in another client. It verifies this device, restores the key backup and decrypts the stored messages that were waiting for keys.

### Verifying devices

- File → Verify this device (or `/verify`) asks your other devices to verify this one: accept on a device that is already trusted and compare the emoji. *Verify selected* in File → Devices verifies the device you pick. `/verify @user:server` verifies another person.
- File → Devices marks devices that your current identity has not signed as *Not trusted*, on red. Only a trusted device can vouch for another, so verify an untrusted device with a trusted one, or use your recovery key.
- Accept on the other device. When both show the same emoji, choose **They match** on both; if they differ, choose **They don't match**.
- File → Devices shows each device as verified, cross-signed (verified through your cross-signing keys) or unverified. Room keys and secrets are shared only with your verified or cross-signed devices.

## Devices

File → Devices (or `/devices`) lists your sessions with their last activity; this one is marked `*`. You can rename a device, or log out one or all other devices, which asks for your password. To end this session, use File → Log out.
