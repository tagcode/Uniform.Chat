namespace Uniform.Chat.Desktop;

using Uniform.Chat.Desktop.Cameras;
using global::Avalonia;
using Uniform.Chat.Desktop.Notifications;
using Uniform.Chat.Avalonia;
using Uniform.Chat.Avalonia.Localization;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Audio;
using Uniform.Chat.Avalonia.Services;
using Uniform.Chat.Desktop.Video;

/// <summary>Desktop entry point of the Uniform Chat app, for Windows and Linux.</summary>
public static class Program
{
    /// <summary>Start Avalonia with the shared app; nothing that needs Avalonia may run before this.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Notifications where the platform has them so far; a ring tone of our own, as desktops have none for calls; video through GStreamer on Linux where it is installed, else WebM in C#
        App.Platform = GuiPlatform.Desktop() with
        {
            Notifier = OperatingSystem.IsLinux() ? new FreeDesktopNotifier() : null,
            Ring = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? _ => Uniform.Matrix.Audio.RingTone.Start(Uniform.Matrix.Audio.AudioDevices.ForThisPlatform()) : null,
            RingIsSoundOnly = true,
            Camera = V4l2Camera.Available() ? () => new V4l2Camera() : MediaFoundationCamera.Available() ? () => new MediaFoundationCamera() : null,
            VideoPlayback = OperatingSystem.IsLinux() && GStreamer.IsAvailable ? () => new DecodedVideoPlayback(path => new GStreamerVideoSource(path), () => AudioDevices.ForThisPlatform()!.OpenMediaSpeaker()) : null,
            PlaysVideo = OperatingSystem.IsLinux() && GStreamer.IsAvailable ? IsVideo : null,
        };
        // Return exit code
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Return whether a file of a MIME type and name is a video for GStreamer to play.</summary>
    private static bool IsVideo(string? mimeType, string? fileName) => mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true || Path.GetExtension(fileName ?? "").ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".3gp" or ".webm" or ".mkv" or ".avi" or ".ogv";

    /// <summary>Configure Avalonia for the desktop; the XAML previewer of editors also calls this.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithScriptFonts().LogToTrace();
}
