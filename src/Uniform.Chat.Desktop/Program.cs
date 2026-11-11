namespace Uniform.Chat.Desktop;

using Uniform.Chat.Desktop.Cameras;
using global::Avalonia;
using Uniform.Chat.Desktop.Notifications;
using Uniform.Chat.Avalonia;
using Uniform.Matrix.Hosting;

/// <summary>Desktop entry point of the Uniform Chat app, for Windows and Linux.</summary>
public static class Program
{
    /// <summary>Start Avalonia with the shared app; nothing that needs Avalonia may run before this.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Notifications where the platform has them so far; a ring tone of our own, as desktops have none for calls
        App.Platform = GuiPlatform.Desktop() with
        {
            Notifier = OperatingSystem.IsLinux() ? new FreeDesktopNotifier() : null,
            Ring = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? _ => Uniform.Matrix.Audio.RingTone.Start(Uniform.Matrix.Audio.AudioDevices.ForThisPlatform()) : null,
            RingIsSoundOnly = true,
            Camera = V4l2Camera.Available() ? () => new V4l2Camera() : MediaFoundationCamera.Available() ? () => new MediaFoundationCamera() : null,
        };
        // Return exit code
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Configure Avalonia for the desktop; the XAML previewer of editors also calls this.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
