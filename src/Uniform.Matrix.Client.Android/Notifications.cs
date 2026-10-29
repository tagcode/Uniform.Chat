namespace Uniform.Matrix.Client.Android;

using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using Uniform.Matrix.Client.Avalonia.Core.Services;

/// <summary>The app's notification channels, and notifications that open the app when tapped.</summary>
internal static class Notifications
{
    /// <summary>Channel of new messages and incoming calls.</summary>
    public const string Messages = "messages";
    /// <summary>Channel of the ongoing call, quiet.</summary>
    public const string OngoingCall = "ongoing_call";

    /// <summary>Create the channels, where Android has them; creating again changes nothing.</summary>
    public static void EnsureChannels(Context context)
    {
        // Android 8 and later
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        manager.CreateNotificationChannel(new NotificationChannel(Messages, "Messages and calls", NotificationImportance.High));
        // Done
        manager.CreateNotificationChannel(new NotificationChannel(OngoingCall, "Ongoing call", NotificationImportance.Low));
    }

    /// <summary>Return a notification of a channel that opens the app when tapped.</summary>
    public static Notification Build(Context context, string channel, string title, string text, bool ongoing)
    {
        // Tapping brings the app's activity forward
        Intent open = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
        PendingIntent? tap = PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422 // The builder without a channel serves Android 7 and older
        Notification.Builder builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(context, channel) : new Notification.Builder(context);
#pragma warning restore CA1422
        builder.SetSmallIcon(Resource.Drawable.icon).SetContentTitle(title).SetContentText(text).SetContentIntent(tap).SetOngoing(ongoing).SetAutoCancel(!ongoing);
        // Return notification
        return builder.Build()!;
    }

    /// <summary>Return whether notifications may be shown: Android 13 asks for a permission.</summary>
    public static bool Allowed(Context context) => !OperatingSystem.IsAndroidVersionAtLeast(33) || context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted;
}

/// <summary>Notifications of new messages and incoming calls, in the messages channel.</summary>
/// <param name="context">The application.</param>
internal sealed class AndroidNotifier(Context context) : INotifier
{
    private int nextId = 100;

    /// <summary>Show a notification, unless the permission is missing.</summary>
    public Task ShowAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        // Channel and permission
        Notifications.EnsureChannels(context);
        if (!Notifications.Allowed(context) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return Task.CompletedTask;
        manager.Notify(Interlocked.Increment(ref nextId), Notifications.Build(context, Notifications.Messages, title, body, ongoing: false));
        // Done
        return Task.CompletedTask;
    }
}

/// <summary>Keeps a call going while the app is in the background: a foreground service of the microphone type, which Android requires for recording then, with an ongoing notification.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class CallForegroundService : Service
{
    private const string PeerExtra = "peer";
    private const int NotificationId = 2;

    /// <summary>Start the service for a call with <paramref name="peer"/>; return what stops it.</summary>
    public static IDisposable Start(Context context, string peer)
    {
        // The service, told who the call is with
        Notifications.EnsureChannels(context);
        Intent intent = new(context, typeof(CallForegroundService));
        intent.PutExtra(PeerExtra, peer);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
        // Return the stopper
        return new Stopper(context);
    }

    /// <summary>Not bound.</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Go to the foreground with the call's notification.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Ongoing notification, typed as a microphone service where Android asks
        string peer = intent?.GetStringExtra(PeerExtra) ?? "";
        Notification notification = Notifications.Build(this, Notifications.OngoingCall, "Voice call", $"In a call with {peer}", ongoing: true);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        else StartForeground(NotificationId, notification);
        // Done
        return StartCommandResult.NotSticky;
    }

    /// <summary>Stops the service once.</summary>
    private sealed class Stopper(Context context) : IDisposable
    {
        private int stopped;

        /// <summary>Stop the service.</summary>
        public void Dispose()
        {
            // Once
            if (Interlocked.Exchange(ref stopped, 1) == 1) return;
            context.StopService(new Intent(context, typeof(CallForegroundService)));
        }
    }
}
