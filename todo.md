# UserManagement 

- Separate user management out into Uniform.UserManagement that is agnostic of Uniform.Matrix.*
- Create Uniform.UserManagement.Gui.Terminal
- Have Matrix server include Uniform.UserManagement.Gui.Terminal

# Uniform.Chat.Desktop

- The look and feel of avalonia based GUI is kinda off. Perhaps it can be worked on, or perhaps MAUI or WinUI has better touch?
A theme that fits each platform.

Android: Material.Avalonia, with Material 3 colours, shapes and ripples.
Desktop: keep Fluent, but with our own accent colour and dark mode following the system.
One shared set of style resources, with small differences per platform.
Typography: the system font (Roboto on Android, Segoe UI on Windows, the desktop's own font on Linux), with clear sizes for room names, message text and timestamps.

Spacing and density:

Phones: larger touch targets, at least 48 dp.
Desktop: denser lists.
Everywhere: consistent padding.
Much of the "unpolished" impression comes from this.

Chat-specific look:

message bubbles;
grouping of consecutive messages from the same sender;
avatars with initials as fallback;
unread badges;
a proper composer bar.
Motion: short transitions between room list and room on phones, smooth scrolling, and a typing indicator that animates.

Android conventions:

edge-to-edge layout with the right system bar colours;
the back gesture;
the keyboard pushing the composer up;


# README.md

- Clean up package README.md.

# Android/Flutter

- Prototype flutter GUI with Avalonia.

# F-Droid

- build.gradle file
- instructions to build the app
- https://gitlab.com/fdroid/fdroiddata?utm_source=chatgpt.com


# Google Play

- Background sync service: The app keeps syncing with the screen off through a foreground service of type remoteMessaging. Play allows that type only for relaying texts to another device, such as a web client paired with the phone. A permanent sync connection doesn't fit, and reviewers check this against a demo video. Ways out:
  - Use push notifications through a push gateway. This is what Play-store Matrix clients do; it's the push work your approved-packages list already allows.
  - Keep the service only in a separate build for direct APK or F-Droid releases.
  - Declare it as specialUse with a written justification, which Google reviews case by case and may refuse.
- Service type for calls. Calls now use the microphone type. The type Google intends for voice-over-IP calls is phoneCall, together with microphone and the MANAGE_OWN_CALLS permission. Ideally the app would also register its calls with Android's telephony system. The demo video is simple: "user starts a call".
- Microphone permission timing: the app asks for it at startup. Asking the first time a call starts is better practice, and it's what Google's permission guidance recommends.
- on Android 13 and later, the app asks for notification permission only at the first call. Until then, message notifications stay off.
- The name: "Matrix" is a trademark of The Matrix.org Foundation, and the Metadata and Intellectual Property policies apply to store names. 
  - Foundation's trademark guidelines
  - Listing title like "Uniform — Matrix chat", before you publish.
- Registering calls with Android's telephony system (ConnectionService).

# User management

Admin user manages other users through matrix protocol.

# Encryption

- Settings for encryption strength. Key size

# Local encryption

- A) SQLCipher (the SQLitePCLRaw.bundle_e_sqlcipher package) encrypts the whole database. The key goes into the Android Keystore, Windows DPAPI, or the Linux Secret Service.
- B) Own encryption: AES-GCM on the message and outbox contents with a key from the same keystores. No new dependency.

# Uniform.Matrix.Gui.Terminal

Rename to Uniform.Gui.Terminal.

# Room VOIP
- MSC3401

# IzzyOnDroid
  - github releases should include signed .apk:s.
  - IzzyOnDroid limits APK size to around 30 MB, unless they grant an exception. Our APK is about 80 MB, because it contains the .NET runtime for all four processor types. One APK per processor type,   mainly arm64-v8a for modern phones, would roughly halve it. Check their current limit when you ask.

# Adroidn client 
- Suspended-account banner and server notices, from my earlier list. They're small and visible to every user.
- F-Droid / IzzyOnDroid store screenshots, still missing. I'd take them with the @note test accounts once Edict is reachable again.
- Admin pages (reports, accounts, tokens), if you want to administer the server from the phone.

# Server administration

- a dashboard to running server services for managing users and server settings.

