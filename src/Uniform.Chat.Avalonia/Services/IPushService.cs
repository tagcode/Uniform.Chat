namespace Uniform.Chat.Avalonia.Services;

/// <summary>A <see href="https://unifiedpush.org">UnifiedPush</see> distributor app on the device, e.g. ntfy.</summary>
/// <param name="Id">Its identifier, e.g. the Android package name.</param>
/// <param name="Name">Its name for people.</param>
public sealed record PushDistributor(string Id, string Name);

/// <summary>Push messages through a <see href="https://unifiedpush.org">UnifiedPush</see> distributor, which keeps one connection for all its apps: registering gives an endpoint URL, which the homeserver's pusher posts to through the distributor's gateway, and messages wake the app.</summary>
public interface IPushService
{
    /// <summary>The distributors installed now.</summary>
    IReadOnlyList<PushDistributor> GetDistributors();

    /// <summary>The distributor chosen, or null for none.</summary>
    string? Distributor { get; }

    /// <summary>The endpoint the distributor gave, or null while not registered.</summary>
    string? Endpoint { get; }

    /// <summary>Raised when <see cref="Distributor"/> or <see cref="Endpoint"/> changes, possibly off the UI thread.</summary>
    event Action? Changed;

    /// <summary>Handles a push message: wakes the app to sync. While none is set, the platform shows a plain notification itself.</summary>
    Func<byte[], Task>? MessageHandler { get; set; }

    /// <summary>Register with <paramref name="distributor"/>; <see cref="Changed"/> tells when the endpoint comes.</summary>
    void Register(string distributor);

    /// <summary>Unregister, and use no distributor.</summary>
    void Unregister();
}
