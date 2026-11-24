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

## Video messages

On Linux, video messages play through [GStreamer](https://gstreamer.freedesktop.org/), the system's media framework, when it is installed: `GStreamerVideoSource` gives [`DecodedVideoPlayback`](xref:Uniform.Chat.Avalonia.Services.DecodedVideoPlayback) the file's pictures and sound, so whatever the installed plugins decode plays, such as MP4 with H.264, H.265 and AAC, WebM and Matroska. It calls GStreamer's C API through P/Invoke, with no bindings package.

- The picture pipeline decodes with `decodebin` to I420, turns the pictures upright by the file's orientation with `videoflip video-direction=auto`, and ends in an `appsink` pulled from as pictures are due. Seeking is flushing and accurate. Pictures wider than 640 are scaled down while copied, and their times are converted to stream time, as an MP4's edit list may start its own times later.
- The sound pipeline decodes the sound whole, at 48 kHz stereo, when the video opens; it plays on the media speaker and keeps the time.
- Each pipeline names the codecs of the other kind it leaves encoded in `decodebin`'s `caps`, so the pictures are not decoded twice; `(ANY)` takes decoders' output whatever its memory.

The Debian package recommends `gstreamer1.0-plugins-good` and `gstreamer1.0-libav`. Without GStreamer, or on Windows for now, video plays as WebM with VP8 in C#. `VideoTests` in Uniform.Chat.Desktop.Tests plays an MP4 through GStreamer, and skips itself where it is not installed.
