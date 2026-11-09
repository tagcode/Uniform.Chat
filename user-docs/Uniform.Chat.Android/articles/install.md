---
title: Installing
uid: Uniform.Chat.Android.Install.Article
---
# Installing

Uniform Chat is installed with [F-Droid](https://f-droid.org), an app store for free and open-source apps, from the app's own F-Droid repository. F-Droid then also keeps the app up to date. The app needs Android 6 or later, and works without Google's services.

## F-Droid

1. Install F-Droid from [F-Droid](https://f-droid.org).
2. Select: Settings / Repositories / +. Add [https://tagcode.github.io/fdroid/repo](https://tagcode.github.io/fdroid/repo), or scan QR code.
3. Search for *Uniform Chat* in F-Droid and install it.

### ntfy

You may optionally also install [ntfy](https://f-droid.org/packages/io.heckel.ntfy/) to conserve battery life,
if your phone runs multiple other apps that support [UnifiedPush](https://unifiedpush.org).

1. Install [ntfy](https://f-droid.org/packages/io.heckel.ntfy/) from F-Droid.
2. In Uniform Chat, open *⋮ → Settings… → In the background* and choose *ntfy*.

ntfy uses its public server `ntfy.sh` unless you set another one in ntfy's settings. That server learns when you get messages, but never what they say or who sent them; [Notifications](xref:Uniform.Chat.Android.Notifications.Article) tells more.

## Without F-Droid

Each [release](https://github.com/tagcode/Uniform.Chat/releases) also has the app as `uniform-chat-android-<version>.apk`, signed with the same key: download it on the phone and open it to install. Updates then don't come by themselves; install each new version the same way.
