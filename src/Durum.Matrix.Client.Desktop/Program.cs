namespace Durum.Matrix.Client.Desktop;

using Avalonia;
using Durum.Matrix.Client.Desktop.Notifications;
using Durum.Matrix.Client.Gui;

/// <summary>Desktop entry point of the Durum chat app, for Windows and Linux.</summary>
public static class Program
{
    /// <summary>Start Avalonia with the shared app; nothing that needs Avalonia may run before this.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Notifications where the platform has them so far
        App.Platform = GuiPlatform.Desktop() with { Notifier = OperatingSystem.IsLinux() ? new FreeDesktopNotifier() : null };
        // Return exit code
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Configure Avalonia for the desktop; the XAML previewer of editors also calls this.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
