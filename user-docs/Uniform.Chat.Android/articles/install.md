---
title: Installing
uid: Uniform.Chat.Android.Install.Article
---
# Installing

Uniform Chat is installed with [F-Droid](https://f-droid.org), an app store for free and open-source apps, from the app's own F-Droid repository. F-Droid then also keeps the app up to date. The app needs Android 6 or later, and works without Google's services.

## F-Droid

1. On the phone, download and install F-Droid from [f-droid.org](https://f-droid.org). Android asks you to allow installing apps from the browser; allow it for this install.
2. Add the Uniform Chat repository to F-Droid. Open [https://tagcode.github.io/fdroid/repo](https://tagcode.github.io/fdroid/repo) in the phone's browser and tap the repository's link there, or scan its QR code from another device; F-Droid opens and offers to add it. The link carries the fingerprint of the repository's key, so F-Droid checks that the app comes from Uniform Chat.

   Alternatively, in F-Droid choose *Settings → Repositories → +* and enter `https://tagcode.github.io/fdroid/repo`.
3. Search for *Uniform Chat* in F-Droid and install it. F-Droid may ask again to be allowed to install apps.

F-Droid tells you of new versions and installs them when you allow it. The repository has only the newest version of the app.

### ntfy

You may optionally also install [ntfy](https://f-droid.org/packages/io.heckel.ntfy/) to conserve battery life,
if your phone runs multiple other apps that support [UnifiedPush](https://unifiedpush.org).

1. Install [ntfy](https://f-droid.org/packages/io.heckel.ntfy/) from F-Droid.
2. In Uniform Chat, open *⋮ → Settings… → In the background* and choose *ntfy*.

ntfy uses its public server `ntfy.sh` unless you set another one in ntfy's settings. That server learns when you get messages, but never what they say or who sent them; [Notifications](xref:Uniform.Chat.Android.Notifications.Article) tells more.

## Without F-Droid

Each [release](https://github.com/tagcode/Uniform.Chat/releases) also has the app as `uniform-chat-android-<version>.apk`, signed with the same key: download it on the phone and open it to install. Updates then don't come by themselves; install each new version the same way.
