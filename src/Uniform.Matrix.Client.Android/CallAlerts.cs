namespace Uniform.Matrix.Client.Android;

using global::Android.App;
using global::Android.Content;
using global::Android.Media;
using global::Android.OS;

/// <summary>Rings for an incoming call as phone apps do: the ringtone and vibration as the phone's ringer mode allows, and, unless the app is on screen, a call notification that opens the app over the lock screen.</summary>
internal sealed class Ringer : IDisposable
{
    /// <summary>ID of the incoming call notification.</summary>
    public const int NotificationId = 3;
    private static readonly long[] VibrationPattern = [0, 800, 800];
    private readonly Context context;
    private readonly Ringtone? ringtone;
    private readonly Vibrator? vibrator;
    private int stopped;

    /// <summary>Start ringing for a call from <paramref name="peer"/>.</summary>
    public Ringer(Context context, string peer)
    {
        // Sound and vibration by the ringer mode
        this.context = context;
        AudioManager? audio = context.GetSystemService(Context.AudioService) as AudioManager;
        RingerMode mode = audio?.RingerMode ?? RingerMode.Normal;
        if (mode == RingerMode.Normal) ringtone = StartRingtone(context);
        if (mode != RingerMode.Silent) vibrator = StartVibration(context);

        // Over the lock screen; a notification when the app is not on screen
        MainActivity.SetShownWhenLocked(true);
        if (MainActivity.IsInForeground) return;
        Notifications.EnsureChannels(context);
        if (Notifications.Allowed(context) && context.GetSystemService(Context.NotificationService) is NotificationManager manager) manager.Notify(NotificationId, Notifications.BuildIncomingCall(context, peer));
    }

    /// <summary>Stop ringing, and remove the notification.</summary>
    public void Dispose()
    {
        // Once
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        ringtone?.Stop();
        vibrator?.Cancel();
        HideNotification(context);
        // Done
        MainActivity.SetShownWhenLocked(false);
    }

    /// <summary>Remove the incoming call notification, e.g. when the app comes on screen, whose bar has the buttons.</summary>
    public static void HideNotification(Context context) => (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Cancel(NotificationId);

    /// <summary>Play the default ringtone, looping where Android can.</summary>
    private static Ringtone? StartRingtone(Context context)
    {
        // The default ringtone as a ringtone
        if (RingtoneManager.GetDefaultUri(RingtoneType.Ringtone) is not global::Android.Net.Uri uri || RingtoneManager.GetRingtone(context, uri) is not Ringtone ringtone) return null;
        ringtone.AudioAttributes = new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.NotificationRingtone)!.SetContentType(AudioContentType.Sonification)!.Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) ringtone.Looping = true;
        ringtone.Play();
        // Return it, to stop
        return ringtone;
    }

    /// <summary>Vibrate in a repeating pattern.</summary>
    private static Vibrator? StartVibration(Context context)
    {
        // The default vibrator
#pragma warning disable CA1422 // The vibrator service serves Android 11 and older
        Vibrator? vibrator = OperatingSystem.IsAndroidVersionAtLeast(31) ? (context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator : context.GetSystemService(Context.VibratorService) as Vibrator;
        if (vibrator is null || !vibrator.HasVibrator) return null;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) vibrator.Vibrate(VibrationEffect.CreateWaveform(VibrationPattern, 0));
        else vibrator.Vibrate(VibrationPattern, 0);
#pragma warning restore CA1422
        // Return it, to stop
        return vibrator;
    }
}

/// <summary>Turns the screen off while the phone is near the ear, through Android's proximity wake lock.</summary>
internal sealed class NearEarScreenOff : IDisposable
{
    private readonly PowerManager.WakeLock? wakeLock;

    /// <summary>Hold the lock, where the phone has a proximity sensor.</summary>
    public NearEarScreenOff(Context context)
    {
        // The proximity level, when supported
        if (context.GetSystemService(Context.PowerService) is not PowerManager power || !power.IsWakeLockLevelSupported((int)WakeLockFlags.ProximityScreenOff)) return;
        wakeLock = power.NewWakeLock(WakeLockFlags.ProximityScreenOff, "Uniform.Matrix:call");
        wakeLock?.Acquire();
    }

    /// <summary>Release the lock, after the phone moves away from the ear.</summary>
    public void Dispose()
    {
        // Screen on again once nothing is near
        if (wakeLock?.IsHeld != true) return;
        wakeLock.Release(WakeLockFlags.ReleaseFlagWaitForNoProximity);
    }
}
