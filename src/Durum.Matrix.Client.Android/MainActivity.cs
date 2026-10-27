namespace Durum.Matrix.Client.Android;

using Avalonia.Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.Views;

/// <summary>The app's one screen; Avalonia draws the shared user interface into it.</summary>
[Activity(
    Label = "Durum",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
