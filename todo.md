# Look and feel

- Material.Avalonia 
  - https://avaloniacommunity.github.io/Material.Avalonia/
  - https://github.com/AvaloniaCommunity/material.avalonia
  - 3 colors, shapes, ripples
  - Desktop: keep Fluent, own accent colour and dark mode following the system.
  - One shared set of style resources, with small differences per platform.
  - Typography: the system font (Roboto on Android, Segoe UI on Windows, the desktop's own font on Linux), with clear sizes for room names, message text and timestamps.

- Spacing and density:
  - Phones: larger touch targets, at least 48 dp.
  - Desktop: denser lists.
  - Everywhere: consistent padding.
  - Much of the "unpolished" impression comes from this.

- Chat-specific look:
  - message bubbles
  - grouping of consecutive messages from the same sender
  - avatars with initials as fallback;
  - unread badges;
  - a proper composer bar.
  - Motion: short transitions between room list and room on phones, smooth scrolling, and a typing indicator that animates.

- Android conventions:
  - edge-to-edge layout with the right system bar colours; the back gesture; the keyboard pushing the composer up;

# UserManagement 

- Separate user management class libraries: Uniform.UserManagement that is agnostic of Uniform.Matrix.*
- Create Uniform.UserManagement.Gui.Terminal
- Have Matrix server include Uniform.UserManagement.Gui.Terminal

# Documentations

- Clean up package README.md:s.
- Rewrite and reorganize documents.

# Android/Flutter

- Prototype flutter GUI with Avalonia. Avalonia Flutter support isn't ready yet, perhaps in 2027.

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

# Encryption

- Settings for encryption strength. Key sizes

# Local encryption

- A) SQLCipher (the SQLitePCLRaw.bundle_e_sqlcipher package) encrypts the whole database. The key goes into the Android Keystore, Windows DPAPI, or the Linux Secret Service.
- B) Own encryption: AES-GCM on the message and outbox contents with a key from the same keystores. No new dependency.

# Uniform.Matrix.Gui.Terminal

Rename to Uniform.Gui.Terminal.

# Room VOIP
- MSC3401

# IzzyOnDroid
  - github releases should include signed .apk:s.
  - IzzyOnDroid limits APK size to around 30 MB. Our APK is about 80 MB, because it contains the .NET runtime for all four processor types. 
     - One APK per processor type, would halve it (40MB). Still not small enough. Cannot be published on IzzyOnDroid.

# Adroind client 

- Suspended-account banner and server notices
- F-Droid / IzzyOnDroid store screenshots
- Admin pages (reports, accounts, tokens)

# Server local socket based dashboard

- Test it.
- If socket dashboard works well enough, the server dashboard mode can be removed?