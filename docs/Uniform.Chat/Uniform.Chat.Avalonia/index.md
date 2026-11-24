---
title: Uniform.Chat.Avalonia
uid: Uniform.Chat.Avalonia.Article
---
# Uniform.Chat.Avalonia

The graphical app, in Avalonia 12 with the Fluent theme and CommunityToolkit.Mvvm. The same views run in a desktop window and as the single view of the Android activity; [`GuiPlatform`](xref:Uniform.Chat.Avalonia.GuiPlatform) carries what differs: the application name (and so the data folders), the device name shown to other sessions, whether the screen is always one pane, and extra services.

## Hosts

[`AccountPaths`](xref:Uniform.Matrix.Client.Configuration.AccountPaths) needs `Matrix:UserId` when the services are built, so the app has no host until someone signs in. [`ClientConnection.StartAsync`](xref:Uniform.Chat.Avalonia.Services.ClientConnection.StartAsync*) builds a [`HostApplicationBuilder`](xref:Microsoft.Extensions.Hosting.HostApplicationBuilder) per sign-in: [`AddSettings`](xref:Uniform.Matrix.Hosting.HostingExtensions.AddSettings*), the user ID and server from the form as in-memory configuration, [`AddMatrixClient`](xref:Uniform.Matrix.Client.ClientServiceExtensions.AddMatrixClient*), and `GuiCredentialPrompt`, which returns the form's password or throws [`PasswordRequiredException`](xref:Uniform.Chat.Avalonia.Services.PasswordRequiredException) when a resumed session needs one. Signing out or in again disposes the host and builds a new one. [`AccountMemory`](xref:Uniform.Chat.Avalonia.Services.AccountMemory) keeps the last user ID in the settings file, so the next start resumes without the form. [`SettingsViewModel`](xref:Uniform.Chat.Avalonia.ViewModels.SettingsViewModel) keeps the theme, text size and notification switch in its `Ui` section and saves on every change; [`App`](xref:Uniform.Chat.Avalonia.App) applies the theme as `RequestedThemeVariant` and the text size as Fluent's `ControlContentThemeFontSize`.

## Audio and video messages

Audio and video messages play in the message list. [`MediaMessagePlayer`](xref:Uniform.Chat.Avalonia.ViewModels.MediaMessagePlayer) plays one at a time: it downloads the file through the media cache, opens it with an [`IMediaPlayback`](xref:Uniform.Chat.Avalonia.Services.IMediaPlayback), and shows the position, and a video's pictures in a [`VideoFeed`](xref:Uniform.Chat.Avalonia.ViewModels.VideoFeed), on the [`MessageViewModel`](xref:Uniform.Chat.Avalonia.ViewModels.MessageViewModel), whose slider seeks. The message list makes new items when a room opens again; the player follows the playing message by its event ID, and stops when it is deleted or another room opens.

| Kind | Playback | Formats |
|---|---|---|
| Audio | [`AudioPlayback`](xref:Uniform.Chat.Avalonia.Services.AudioPlayback), with [Uniform.Matrix.Audio](xref:Uniform.Matrix.Audio.Article) in C# | WAV, MP3, FLAC, Ogg Opus, Ogg Vorbis |
| Video, Linux | [`DecodedVideoPlayback`](xref:Uniform.Chat.Avalonia.Services.DecodedVideoPlayback) of `GStreamerVideoSource`, through [`GuiPlatform.VideoPlayback`](xref:Uniform.Chat.Avalonia.GuiPlatform.VideoPlayback) | what the installed GStreamer plugins decode: MP4 with H.264, H.265 and AAC, WebM, Matroska, and more |
| Video, Android | [`DecodedVideoPlayback`](xref:Uniform.Chat.Avalonia.Services.DecodedVideoPlayback) of `AndroidVideoSource`, Android's `MediaExtractor` and `MediaCodec` | what the phone decodes: MP4 with H.264, H.265 and AAC, WebM, and more |
| Video, otherwise | [`WebmPlayback`](xref:Uniform.Chat.Avalonia.Services.WebmPlayback), in C# | WebM with VP8, and Opus sound |

[`DecodedVideoPlayback`](xref:Uniform.Chat.Avalonia.Services.DecodedVideoPlayback) plays what a platform decodes through an [`IVideoSource`](xref:Uniform.Chat.Avalonia.Services.IVideoSource): pictures with their times, pulled as they are due, and the sound decoded whole, which plays on the media speaker and keeps the time.

`WebmPlayback` reads the whole file and decodes its sound when opened; its pictures are decoded as they are due, timed by the sound on the media speaker, or by a clock of its own when there is none. It jumps to the latest keyframe due when decoding falls more than half a second behind, and seeking decodes from the keyframe before the position. Where neither GStreamer nor another platform decoder is there, as on Windows for now, MP4 videos keep *Open*, which plays them in the system's player.

Audio messages played are saved in the client's store, [`ClientStore.MarkListenedAsync`](xref:Uniform.Matrix.Client.Storage.ClientStore.MarkListenedAsync*), and marked 🎧.

## Not yet covered

- Renaming devices, and notifications on Windows and Android.
