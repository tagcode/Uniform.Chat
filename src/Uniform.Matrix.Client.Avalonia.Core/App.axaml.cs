namespace Uniform.Matrix.Client.Avalonia.Core;

using global::Avalonia;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Styling;
using Uniform.Matrix.Client.Avalonia.Core.Services;
using Uniform.Matrix.Client.Avalonia.Core.ViewModels;
using Uniform.Matrix.Client.Avalonia.Core.Views;

/// <summary>The Uniform.Matrix chat app: one shared user interface for the desktop and Android apps.</summary>
public partial class App : Application
{
    private MainViewModel? model;

    /// <summary>Platform of the running app; the Android app sets it before Avalonia starts.</summary>
    public static GuiPlatform Platform { get; set; } = GuiPlatform.Desktop();

    /// <summary>Load the XAML of the application.</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Create the main window or view and resume the remembered account.</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // One app state, shown in a window on desktops and as the activity's view on Android
        model = new MainViewModel(Platform, new AvaloniaUiDispatcher());
        ApplySettings(model.Settings);
        model.Settings.PropertyChanged += (_, _) => ApplySettings(model.Settings);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = model };
            desktop.Exit += (_, _) => Task.Run(model.StopAsync).Wait(TimeSpan.FromSeconds(5));
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView { DataContext = model };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView { DataContext = model };
        }

        // Resume in the background
        _ = model.StartAsync();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Apply the theme and text size; Fluent controls size their text by <c>ControlContentThemeFontSize</c>.</summary>
    private void ApplySettings(SettingsViewModel settings)
    {
        // Theme variant, and text size for controls and plain text
        RequestedThemeVariant = settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        Resources["ControlContentThemeFontSize"] = settings.TextSize;
    }
}
