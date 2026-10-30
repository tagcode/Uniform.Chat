---
title: Uniform.Matrix.Client.Android
uid: Uniform.Matrix.Client.Android.Article
---
# Uniform.Matrix.Client.Android

The Android entry point of the graphical app in [Uniform.Matrix.Client.Avalonia.Core](xref:Uniform.Matrix.Client.Avalonia.Core.Article): the activity, the manifest and the resources.

`MainApplication` sets `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to the app's private files folder before anything else runs, so [`AppDirectories`](xref:Uniform.Matrix.Hosting.AppDirectories) and the engine need no Android code. The activity resizes for the keyboard. Android backup is off, so encryption keys stay on the device.

## Build and install

The Android app is in `Uniform.Matrix.slnx`, so building the whole solution needs the Android workload; it also needs the Android SDK and JDK 17:

```bash
dotnet workload install android
dotnet build src/Uniform.Matrix.Client.Android -t:Install \
  -p:AndroidSdkDirectory=$HOME/Android/Sdk -p:JavaSdkDirectory=/usr/lib/jvm/java-17-openjdk-amd64
```

`-t:Install` installs the debug build on the phone connected with USB debugging (`adb devices` lists it). To reach a server running on the computer, forward its port and sign in with `http://localhost:8008`:

```bash
adb reverse tcp:8008 tcp:8008
```

## Background work

Two foreground services keep the app working off screen, each with a notification:

- `SyncForegroundService` keeps syncing while the app is in the background or the screen is off; without it, Android's restricted networking cuts the app off. Its type is `specialUse`, as no other type fits a connection to the user's own server, with the reason in the `android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE` property; Google Play reviews that reason.
- `CallForegroundService` keeps a call going: of the types `phoneCall` and `microphone`, which needs the `MANAGE_OWN_CALLS` permission.

Google Play asks for each type to be declared under *App content → Foreground service permissions*, with a short video of the user starting it, and for `USE_FULL_SCREEN_INTENT`, which rings incoming calls over the lock screen.

## Call audio

The microphone and speaker of a call share Android's communication mode for the length of the call (`CommunicationMode`): in it, the phone routes voice to the earpiece and runs its echo cancellation between the speaker and the microphone. The microphone records from the voice communication source and also turns on the platform's echo canceller and noise suppressor, kept for the whole recording, since a collected effect is released.

## Policies

The sign-in form links to the [user policy](https://github.com/tagcode/Uniform.Matrix/blob/main/user-docs/Uniform.Matrix.Client.Android/user-policy.md) and [privacy policy](https://github.com/tagcode/Uniform.Matrix/blob/main/user-docs/Uniform.Matrix.Client.Android/privacy-policy.md) in the user guide, at their GitHub addresses; Google Play needs the privacy policy's address too.

