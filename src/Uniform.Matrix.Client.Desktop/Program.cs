namespace Uniform.Matrix.Client.Desktop;

using global::Avalonia;
using Uniform.Matrix.Client.Desktop.Notifications;
using Uniform.Matrix.Client.Avalonia.Core;
using Uniform.Matrix.Hosting;

/// <summary>Desktop entry point of the Uniform.Matrix chat app, for Windows and Linux.</summary>
public static class Program
{
    /// <summary>Start Avalonia with the shared app; nothing that needs Avalonia may run before this.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Data and settings of the app's earlier name move to the new name once
        foreach (string move in AppDirectories.MoveFromPreviousName("Durum.Matrix.Client.Desktop", "Uniform.Matrix.Client.Desktop")) Console.Error.WriteLine($"Moved {move}");

        // Notifications where the platform has them so far
        App.Platform = GuiPlatform.Desktop() with { Notifier = OperatingSystem.IsLinux() ? new FreeDesktopNotifier() : null };
        // Return exit code
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Configure Avalonia for the desktop; the XAML previewer of editors also calls this.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
