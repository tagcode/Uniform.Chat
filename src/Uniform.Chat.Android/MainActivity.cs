namespace Uniform.Chat.Android;

using global::Avalonia.Android;
using global::Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Views;

/// <summary>The app's one screen; Avalonia draws the shared user interface into it.</summary>
[Activity(
    Label = "Uniform Chat",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int MicrophoneRequest = 1;
    private const int CameraRequest = 3;
    private const int NotificationsRequest = 2;
    private static MainActivity? current;
    private static bool shownWhenLocked;
    private TaskCompletionSource<bool>? microphoneAnswer;
    private TaskCompletionSource<bool>? cameraAnswer;
    private TaskCompletionSource<bool>? notificationsAnswer;

    /// <summary>Whether the app is on screen, so that a ringing call needs no notification.</summary>
    public static bool IsInForeground { get; private set; }

    /// <summary>Show the app over the lock screen and turn the screen on, while a call rings; or stop.</summary>
    public static void SetShownWhenLocked(bool shown)
    {
        // Remembered for an activity created later, e.g. by the call notification
        shownWhenLocked = shown;
        current?.RunOnUiThread(() => current?.ApplyShownWhenLocked());
    }

    /// <summary>Remember the running activity, which asks for permissions.</summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Before the base, so the app can ask at once
        current = this;
        base.OnCreate(savedInstanceState);
        ApplyShownWhenLocked();
    }

    /// <summary>On screen: a ringing call's notification would cover the call bar's buttons, so remove it.</summary>
    protected override void OnResume()
    {
        // Foreground, without the notification
        base.OnResume();
        IsInForeground = true;
        Ringer.HideNotification(this);
    }

    /// <summary>Off screen.</summary>
    protected override void OnPause()
    {
        // Background
        IsInForeground = false;
        base.OnPause();
    }

    /// <summary>Apply <see cref="SetShownWhenLocked"/> to this activity.</summary>
    private void ApplyShownWhenLocked()
    {
        // Android 8.1 and later have the calls; older ones the window flags
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetShowWhenLocked(shownWhenLocked);
            SetTurnScreenOn(shownWhenLocked);
            return;
        }
#pragma warning disable CA1422 // The window flags serve Android 8 and older
        WindowManagerFlags flags = WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn;
        if (shownWhenLocked) Window?.AddFlags(flags);
        else Window?.ClearFlags(flags);
#pragma warning restore CA1422
    }

    /// <summary>Ask for the microphone permission, and on Android 13 the one for notifications, which calls show, unless granted already; return whether the microphone is granted.</summary>
    public static Task<bool> RequestMicrophoneAsync()
    {
        // Granted already, or no activity to ask from
        if (current is not MainActivity activity) return Task.FromResult(false);
        bool notifications = Notifications.Allowed(activity);
        if (activity.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted && notifications) return Task.FromResult(true);

        // Ask; the answer comes to OnRequestPermissionsResult
        activity.microphoneAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions(!notifications && OperatingSystem.IsAndroidVersionAtLeast(33) ? [Manifest.Permission.RecordAudio, Manifest.Permission.PostNotifications] : [Manifest.Permission.RecordAudio], MicrophoneRequest);
        // Return the answer to come
        return activity.microphoneAnswer.Task;
    }

    /// <summary>Ask for the camera permission, for answering with video, unless granted already; return whether granted.</summary>
    public static Task<bool> RequestCameraAsync()
    {
        // Granted already, or no activity to ask from
        if (current is not MainActivity activity) return Task.FromResult(false);
        if (activity.CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) return Task.FromResult(true);

        // Ask; the answer comes to OnRequestPermissionsResult
        activity.cameraAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions([Manifest.Permission.Camera], CameraRequest);
        // Return the answer to come
        return activity.cameraAnswer.Task;
    }

    /// <summary>Ask on Android 13 and later for the permission to notify, which an incoming call's screen needs, unless granted already; return whether notifications may be shown.</summary>
    public static Task<bool> RequestNotificationsAsync()
    {
        // Granted already, not needed, or no activity to ask from
        if (current is not MainActivity activity) return Task.FromResult(false);
        if (Notifications.Allowed(activity) || !OperatingSystem.IsAndroidVersionAtLeast(33)) return Task.FromResult(true);

        // Ask; Android answers at once, without asking, when the user has refused for good
        activity.notificationsAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions([Manifest.Permission.PostNotifications], NotificationsRequest);
        // Return the answer to come
        return activity.notificationsAnswer.Task;
    }

    /// <summary>Pass on the answer to a permission request.</summary>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        // The notifications' answer
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == NotificationsRequest)
        {
            notificationsAnswer?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
            notificationsAnswer = null;
            return;
        }

        // The camera's answer
        if (requestCode == CameraRequest)
        {
            cameraAnswer?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
            cameraAnswer = null;
            return;
        }

        // The microphone's answer
        if (requestCode != MicrophoneRequest) return;
        int microphone = Array.IndexOf(permissions, Manifest.Permission.RecordAudio);
        microphoneAnswer?.TrySetResult(microphone >= 0 && microphone < grantResults.Length && grantResults[microphone] == Permission.Granted);
        microphoneAnswer = null;
    }
}
