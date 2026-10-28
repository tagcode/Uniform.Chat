# Uniform.Matrix.Client.Desktop

The Windows and Linux entry point of the graphical app in [Uniform.Matrix.Client.Avalonia.Core](../Uniform.Matrix.Client.Avalonia.Core/index.md): it starts Avalonia with a desktop window, and on Linux sets `FreeDesktopNotifier` as the app's notifier and ships `install-desktop-entry.sh`, which adds the app to the application menu.

## Build and run

The desktop app builds with the rest of the solution:

```bash
dotnet run --project src/Uniform.Matrix.Client.Desktop
```

Releases publish it as a self-contained single file for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`; see [Building](../building.md).
