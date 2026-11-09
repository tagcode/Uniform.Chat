namespace Uniform.Chat.Android;

using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using Uniform.Chat.Avalonia.Services;

/// <summary>Push messages through a <see href="https://unifiedpush.org/developers/spec/android/">UnifiedPush</see> distributor app such as ntfy, which keeps one connection for all its apps: broadcasts between the apps register, give the endpoint, and deliver messages. The choice and the endpoint are kept in the app's preferences.</summary>
internal sealed class AndroidPushService : IPushService
{
    /// <summary>Broadcast to a distributor: register.</summary>
    public const string Register = "org.unifiedpush.android.distributor.REGISTER";
    /// <summary>Broadcast to a distributor: unregister.</summary>
    public const string UnregisterAction = "org.unifiedpush.android.distributor.UNREGISTER";
    /// <summary>Broadcast to a distributor: a message arrived.</summary>
    public const string MessageAck = "org.unifiedpush.android.distributor.MESSAGE_ACK";
    /// <summary>Broadcast from a distributor: the endpoint.</summary>
    public const string NewEndpoint = "org.unifiedpush.android.connector.NEW_ENDPOINT";
    /// <summary>Broadcast from a distributor: registering failed.</summary>
    public const string RegistrationFailed = "org.unifiedpush.android.connector.REGISTRATION_FAILED";
    /// <summary>Broadcast from a distributor: no longer registered.</summary>
    public const string Unregistered = "org.unifiedpush.android.connector.UNREGISTERED";
    /// <summary>Broadcast from a distributor: a push message.</summary>
    public const string Message = "org.unifiedpush.android.connector.MESSAGE";
    private const string Preferences = "unifiedpush";
    private readonly Context context;
    private readonly ISharedPreferences preferences;

    /// <summary>Create the service of the application.</summary>
    public AndroidPushService(Context context)
    {
        // Preferences of the app's own
        this.context = context;
        preferences = context.GetSharedPreferences(Preferences, FileCreationMode.Private)!;
        Current = this;
    }

    /// <summary>The service of the running app, for the receiver.</summary>
    public static AndroidPushService? Current { get; private set; }

    /// <summary>The instance token, which tells the distributor which registration of this app is meant.</summary>
    public string Token
    {
        get
        {
            // Made once
            if (preferences.GetString(nameof(Token), null) is string token) return token;
            token = Guid.NewGuid().ToString("N");
            preferences.Edit()!.PutString(nameof(Token), token)!.Apply();
            // Return token
            return token;
        }
    }

    /// <summary>The chosen distributor's package, or null.</summary>
    public string? Distributor => preferences.GetString(nameof(Distributor), null);

    /// <summary>The endpoint the distributor gave, or null.</summary>
    public string? Endpoint => preferences.GetString(nameof(Endpoint), null);

    /// <summary>Raised when the distributor or the endpoint changes.</summary>
    public event Action? Changed;

    /// <summary>Handles push messages: the signed-in app's sync.</summary>
    public Func<byte[], Task>? MessageHandler { get; set; }

    /// <summary>Return the apps that take UnifiedPush registrations, other than this one.</summary>
    public IReadOnlyList<PushDistributor> GetDistributors()
    {
        // Receivers of the register broadcast; Android 11 and later show them through the manifest's queries
        PackageManager packages = context.PackageManager!;
#pragma warning disable CA1422 // The flags as a number serve Android 12 and older
        IList<ResolveInfo> receivers = OperatingSystem.IsAndroidVersionAtLeast(33) ? packages.QueryBroadcastReceivers(new Intent(Register), PackageManager.ResolveInfoFlags.Of(0)) : packages.QueryBroadcastReceivers(new Intent(Register), 0);
#pragma warning restore CA1422
        // Return them by name
        return [.. receivers
            .Select(r => r.ActivityInfo?.PackageName)
            .OfType<string>()
            .Where(p => p != context.PackageName)
            .Distinct()
            .Select(p => new PushDistributor(p, Label(packages, p)))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Register with <paramref name="distributor"/>; the endpoint comes in a broadcast.</summary>
    void IPushService.Register(string distributor)
    {
        // Another distributor first lets the old one go
        if (Distributor is string old && old != distributor) SendUnregister(old);
        preferences.Edit()!.PutString(nameof(Distributor), distributor)!.Remove(nameof(Endpoint))!.Apply();
        Changed?.Invoke();

        // The registration, with a pending intent that tells the distributor which app asks
        Intent register = new(Register);
        register.SetPackage(distributor);
        register.PutExtra("token", Token);
        register.PutExtra("application", context.PackageName);
        register.PutExtra("message", "Uniform Chat messages and calls");
        register.PutExtra("pi", PendingIntent.GetBroadcast(context, 0, new Intent("org.unifiedpush.dummy_app"), PendingIntentFlags.Immutable));
        // Done
        context.SendBroadcast(register);
    }

    /// <summary>Unregister from the distributor, and use none.</summary>
    public void Unregister()
    {
        // Tell it, then forget it
        if (Distributor is string distributor) SendUnregister(distributor);
        Forget();
    }

    /// <summary>A distributor's broadcast for this app's token: keep the endpoint, or forget the registration.</summary>
    internal void Receive(string action, Intent intent)
    {
        // Only our registration's
        if (intent.GetStringExtra("token") != Token) return;
        switch (action)
        {
            case NewEndpoint when intent.GetStringExtra("endpoint") is string endpoint && endpoint != Endpoint:
                preferences.Edit()!.PutString(nameof(Endpoint), endpoint)!.Apply();
                Changed?.Invoke();
                break;
            case RegistrationFailed or Unregistered:
                Forget();
                break;
        }
    }

    /// <summary>Tell the distributor that the message <paramref name="id"/> arrived, so it stops keeping it.</summary>
    internal void Acknowledge(string id)
    {
        // To the chosen distributor
        if (Distributor is not string distributor) return;
        Intent ack = new(MessageAck);
        ack.SetPackage(distributor);
        ack.PutExtra("token", Token);
        ack.PutExtra("id", id);
        // Done
        context.SendBroadcast(ack);
    }

    /// <summary>Forget the distributor and the endpoint.</summary>
    private void Forget()
    {
        // Both, then tell
        preferences.Edit()!.Remove(nameof(Distributor))!.Remove(nameof(Endpoint))!.Apply();
        Changed?.Invoke();
    }

    /// <summary>Send the unregister broadcast to <paramref name="distributor"/>.</summary>
    private void SendUnregister(string distributor)
    {
        // With the token
        Intent unregister = new(UnregisterAction);
        unregister.SetPackage(distributor);
        unregister.PutExtra("token", Token);
        // Done
        context.SendBroadcast(unregister);
    }

    /// <summary>Return an app's name, or its package when it has none.</summary>
    private static string Label(PackageManager packages, string package)
    {
        // The launcher's name
        try
        {
#pragma warning disable CA1422 // The flags as a number serve Android 12 and older
            ApplicationInfo info = OperatingSystem.IsAndroidVersionAtLeast(33) ? packages.GetApplicationInfo(package, PackageManager.ApplicationInfoFlags.Of(0)) : packages.GetApplicationInfo(package, 0);
#pragma warning restore CA1422
            return packages.GetApplicationLabel(info);
        }
        catch (PackageManager.NameNotFoundException)
        {
            // On an app removed meanwhile
            return package;
        }
    }
}

/// <summary>Receives the UnifiedPush distributor's broadcasts: the endpoint, its loss, and push messages, which wake the app's sync. Android starts the app's process for them when it is not running.</summary>
[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter([AndroidPushService.NewEndpoint, AndroidPushService.RegistrationFailed, AndroidPushService.Unregistered, AndroidPushService.Message])]
public sealed class UnifiedPushReceiver : BroadcastReceiver
{
    /// <summary>Longest wait for the app to sign in after Android started it for a message.</summary>
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(8);

    /// <summary>Pass the broadcast on; a message keeps the receiver alive while the app syncs.</summary>
    public override void OnReceive(Context? context, Intent? intent)
    {
        // The service of the running app
        if (context is null || intent?.Action is not string action || AndroidPushService.Current is not AndroidPushService push) return;
        if (action != AndroidPushService.Message)
        {
            push.Receive(action, intent);
            return;
        }

        // A message of our registration, acknowledged, then handled in the background
        if (intent.GetStringExtra("token") != push.Token) return;
        if (intent.GetStringExtra("id") is string id) push.Acknowledge(id);
        byte[] message = intent.GetByteArrayExtra("bytesMessage") ?? System.Text.Encoding.UTF8.GetBytes(intent.GetStringExtra("message") ?? "");
        PendingResult? pending = GoAsync();
        _ = HandleAsync(context, push, message, pending);
    }

    /// <summary>Let the signed-in app sync, waiting a little for it to sign in when Android has just started it; without it, show a plain notification.</summary>
    private static async Task HandleAsync(Context context, AndroidPushService push, byte[] message, PendingResult? pending)
    {
        // The handler, once there
        try
        {
            DateTime until = DateTime.UtcNow + HandlerWait;
            while (push.MessageHandler is null && DateTime.UtcNow < until) await Task.Delay(250);
            if (push.MessageHandler is Func<byte[], Task> handler) await handler(message);
            else await new AndroidNotifier(context).ShowAsync("Uniform Chat", "New message");
        }
        finally
        {
            pending?.Finish();
        }
    }
}
