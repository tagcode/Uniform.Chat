# Uniform.Matrix.Client.Android

The Android entry point of the graphical app in [Uniform.Matrix.Client.Avalonia.Core](../Uniform.Matrix.Client.Avalonia.Core/index.md): the activity, the manifest and the resources.

`MainApplication` sets `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to the app's private files folder before anything else runs, so `AppDirectories` and the engine need no Android code. The activity resizes for the keyboard. The app has internet permission only, and Android backup is off, so encryption keys stay on the device.

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
