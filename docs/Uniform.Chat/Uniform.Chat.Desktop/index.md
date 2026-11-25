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

The Debian package recommends `gstreamer1.0-plugins-good` and `gstreamer1.0-libav`. Without GStreamer, video plays as WebM with VP8 in C#.

On Windows, video messages play through [Media Foundation](https://learn.microsoft.com/windows/win32/medfound/media-foundation-programming-guide)'s [source reader](https://learn.microsoft.com/windows/win32/medfound/source-reader): `MediaFoundationVideoSource` gives `DecodedVideoPlayback` what Windows decodes, such as MP4 with H.264 and AAC, and H.265, VP9 or AV1 where their extensions from the Microsoft Store are installed. `MediaFoundation` holds its calls and COM interfaces, source-generated with `[GeneratedComInterface]` and `[LibraryImport]`, which the Windows camera, `MediaFoundationCamera`, uses too; each thread that calls them joins the multithreaded apartment first. Returned interfaces are unique instances, through `UniqueComInterfaceMarshaller<T>`, so releasing a sample gives it back to its decoder at once: a cached wrapper is released only when collected, and the AV1 decoder, whose samples are few, then stops with `MF_E_SAMPLEALLOCATOR_EMPTY` while a seek drops pictures.

- The picture reader decodes the first video stream to NV12, with advanced video processing, so that e.g. 10-bit H.265 converts too. Each picture is copied to I420 from its buffer's own rows through `IMF2DBuffer2`, or rows of `MF_MT_DEFAULT_STRIDE`, cropped to `MF_MT_MINIMUM_DISPLAY_APERTURE`, e.g. 1080 of 1088 rows, and scaled down to at most 640 wide like GStreamer's. `MF_MT_VIDEO_ROTATION` goes with each picture as its `Rotation`, which the view turns it by.
- Times count from the first picture, so an MP4 whose edit list Windows does not apply still starts at 0. Seeking sets the position, which reads from the keyframe before it, and drops the pictures before it.
- A second reader decodes the first audio stream whole to 16-bit PCM at 48 kHz stereo, through the resampler; a file without one has no sound.
- A file without video, or whose video Windows has no decoder of, is refused with a `NotSupportedException`, whose message names the codec and where its decoder comes from: H.265 needs HEVC Video Extensions, and Windows N editions the Media Feature Pack. Without Media Foundation at all, as on Windows N without it, video plays as WebM with VP8 in C#.

`VideoTests` in Uniform.Chat.Desktop.Tests plays an MP4 through GStreamer on Linux and through Media Foundation on Windows, and skips each where it is not there.
