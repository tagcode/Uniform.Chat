namespace Uniform.Chat.Android;

using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.OS;
using Uniform.Chat.Avalonia.Services;

/// <summary>The app's notification channels, and notifications that open the app when tapped.</summary>
internal static class Notifications
{
    /// <summary>Channel of new messages and incoming calls.</summary>
    public const string Messages = "messages";
    /// <summary>Channel of the ongoing call, quiet.</summary>
    public const string OngoingCall = "ongoing_call";
    /// <summary>Channel of the connection notification of <see cref="SyncForegroundService"/>, the quietest.</summary>
    public const string Connection = "connection";
    /// <summary>Channel of incoming calls: urgent, but silent, as the ringer plays the ringtone.</summary>
    public const string IncomingCall = "incoming_call";

    /// <summary>Create the channels, where Android has them; creating again changes nothing.</summary>
    public static void EnsureChannels(Context context)
    {
        // Android 8 and later
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        manager.CreateNotificationChannel(new NotificationChannel(Messages, "Messages", NotificationImportance.High));
        NotificationChannel incoming = new(IncomingCall, "Incoming calls", NotificationImportance.High);
        incoming.SetSound(null, null);
        incoming.EnableVibration(false);
        manager.CreateNotificationChannel(incoming);
        manager.CreateNotificationChannel(new NotificationChannel(Connection, "Connection", NotificationImportance.Min));
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

    /// <summary>Return the notification of an incoming call: a call that opens the app, over the lock screen and with the screen turned on when locked.</summary>
    public static Notification BuildIncomingCall(Context context, string peer)
    {
        // The usual notification, as a call with a full-screen intent
        Intent open = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
        PendingIntent? screen = PendingIntent.GetActivity(context, 1, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1422 // The builder without a channel serves Android 7 and older
        Notification.Builder builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(context, IncomingCall) : new Notification.Builder(context);
#pragma warning restore CA1422
        builder.SetSmallIcon(Resource.Drawable.icon).SetContentTitle("Incoming call").SetContentText($"{peer} is calling").SetContentIntent(screen).SetFullScreenIntent(screen, true).SetCategory(Notification.CategoryCall).SetOngoing(true);
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

/// <summary>Keeps a call going while the app is in the background: a foreground service of the phone call type, for voice over IP, and the microphone type, which Android requires for recording then, with an ongoing notification.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypePhoneCall | ForegroundService.TypeMicrophone)]
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
        // Ongoing notification, typed as a call with the microphone where Android asks; Android 10 knows only the call type
        string peer = intent?.GetStringExtra(PeerExtra) ?? "";
        Notification notification = Notifications.Build(this, Notifications.OngoingCall, "Voice call", $"In a call with {peer}", ongoing: true);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) StartForeground(NotificationId, notification, ForegroundService.TypePhoneCall | ForegroundService.TypeMicrophone);
        else if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypePhoneCall);
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

/// <summary>Keeps the engine syncing while the app is off screen or the screen is off: a foreground service, without which Android's restricted networking cuts the app off, so that no message or call arrives. Push notifications would replace it.</summary>
/// <remarks>Of the special use type, as no other fits a connection to the user's own server; the subtype tells Google Play's review why.</remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[Property("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "Keeps the connection to the Matrix homeserver the user chose open, to receive messages and incoming voice calls in real time without Google push services; Matrix servers of other organisations cannot send through Firebase Cloud Messaging.")]
public sealed class SyncForegroundService : Service
{
    private const int NotificationId = 4;

    /// <summary>Start the service; return what stops it.</summary>
    public static IDisposable Start(Context context)
    {
        // Started while the app is on screen, as Android requires
        Notifications.EnsureChannels(context);
        Intent intent = new(context, typeof(SyncForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
        // Return the stopper
        return new Stopper(context);
    }

    /// <summary>Not bound.</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Go to the foreground with the quiet connection notification.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Typed as special use where Android asks
        Notification notification = Notifications.Build(this, Notifications.Connection, "Uniform Chat", "Connected, for messages and calls", ongoing: true);
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
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
            context.StopService(new Intent(context, typeof(SyncForegroundService)));
        }
    }
}
