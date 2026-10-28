namespace Uniform.Matrix.Client.Android;

using global::Avalonia.Android;
using global::Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Views;

/// <summary>The app's one screen; Avalonia draws the shared user interface into it.</summary>
[Activity(
    Label = "Uniform.Matrix",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int MicrophoneRequest = 1;
    private static MainActivity? current;
    private TaskCompletionSource<bool>? microphoneAnswer;

    /// <summary>Remember the running activity, which asks for permissions.</summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Before the base, so the app can ask at once
        current = this;
        base.OnCreate(savedInstanceState);
    }

    /// <summary>Ask for the microphone permission, unless granted already; return whether it is granted.</summary>
    public static Task<bool> RequestMicrophoneAsync()
    {
        // Granted already, or no activity to ask from
        if (current is not MainActivity activity) return Task.FromResult(false);
        if (activity.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted) return Task.FromResult(true);

        // Ask; the answer comes to OnRequestPermissionsResult
        activity.microphoneAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions([Manifest.Permission.RecordAudio], MicrophoneRequest);
        // Return the answer to come
        return activity.microphoneAnswer.Task;
    }

    /// <summary>Pass on the answer to a permission request.</summary>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        // The microphone's answer
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != MicrophoneRequest) return;
        microphoneAnswer?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        microphoneAnswer = null;
    }
}
