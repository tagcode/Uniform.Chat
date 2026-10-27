using Avalonia;
using Avalonia.Headless;
using Durum.Matrix.Client.Gui;

[assembly: AvaloniaTestApplication(typeof(Durum.Matrix.Client.Gui.Tests.TestApp))]

namespace Durum.Matrix.Client.Gui.Tests;

/// <summary>Avalonia without a screen for the tests, drawing with Skia so views can be captured; settings and data go to a temporary folder.</summary>
public static class TestApp
{
    /// <summary>Temporary home of the tests' XDG data and config folders.</summary>
    public static string Home { get; } = Directory.CreateTempSubdirectory("durum-gui-tests-").FullName;

    /// <summary>Configure the headless app; called once by the test framework.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        // Keep the user's own settings and data out of reach
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Home, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Home, "data"));
        App.Platform = new GuiPlatform { ApplicationName = "Durum.Matrix.Client.Gui.Tests", DeviceName = "Durum tests" };
        // Return builder
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
