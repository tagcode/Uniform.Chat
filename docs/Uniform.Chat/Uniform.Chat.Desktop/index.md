---
title: Uniform.Chat.Desktop
uid: Uniform.Chat.Desktop.Article
---
# Uniform.Chat.Desktop

The Windows and Linux entry point of the graphical app in [Uniform.Chat.Avalonia](xref:Uniform.Chat.Avalonia.Article): it starts Avalonia with a desktop window, and on Linux sets [`FreeDesktopNotifier`](xref:Uniform.Chat.Desktop.Notifications.FreeDesktopNotifier) as the app's notifier and ships `install-desktop-entry.sh`, which adds the app to the application menu.

## Build and run

The desktop app builds with the rest of the solution:

```bash
dotnet run --project src/Uniform.Chat.Desktop
```

The program file is `Uniform.Chat` (`Uniform.Chat.exe` on Windows), not the project's name: file managers on Linux match names ending in `.Desktop` to `.desktop` launchers regardless of case, and would not run it.

Releases publish it as a self-contained single file for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`; see [Building](xref:Uniform.Matrix.Building.Article).
