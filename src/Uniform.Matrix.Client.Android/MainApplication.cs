namespace Uniform.Matrix.Client.Android;

using global::Avalonia;
using global::Avalonia.Android;
using Uniform.Matrix.Client.Android.Audio;
using Uniform.Matrix.Client.Avalonia.Core;
using global::Android.App;
using global::Android.Runtime;

/// <summary>The Android application: points the app's data and settings folders into its private storage, then starts the shared Avalonia app.</summary>
[Application(Label = "Uniform.Matrix", Icon = "@drawable/icon")]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    /// <summary>Create the application; Android calls this.</summary>
    public MainApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    /// <summary>Configure the shared app for phones before Avalonia starts.</summary>
    public override void OnCreate()
    {
        // XDG folders inside the app's private storage, which the client engine uses for its database and settings
        string files = FilesDir!.AbsolutePath;
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(files, "data"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(files, "config"));
        App.Platform = new GuiPlatform
        {
            ApplicationName = "Uniform.Matrix.Client.Android",
            DeviceName = "Uniform.Matrix on Android",
            SinglePane = true,
            Audio = loudspeaker => new AndroidAudioDevices(this, loudspeaker),
            HasEarpiece = true,
            RequestMicrophoneAsync = MainActivity.RequestMicrophoneAsync,
            Notifier = new AndroidNotifier(this),
            KeepCallAlive = peer => CallForegroundService.Start(this, peer),
            KeepConnected = () => SyncForegroundService.Start(this),
            Ring = peer => new Ringer(this, peer),
            ScreenOffNearEar = () => new NearEarScreenOff(this),
        };
        base.OnCreate();
    }

    /// <summary>Add the Inter font, as on desktops.</summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
