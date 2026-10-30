namespace Uniform.Chat.Android;

using global::Android.Content;
using global::Android.Net;
using Uniform.Matrix.Client.Services;

/// <summary>Reports the phone's network changes, e.g. from one Wi-Fi to another or to mobile data, to the client engine, whose sync and calls move to the new network at once; Android does not let .NET see them.</summary>
internal sealed class NetworkWatcher : ConnectivityManager.NetworkCallback
{
    private Network? current;

    /// <summary>Follow the default network.</summary>
    public static void Start(Context context)
    {
        // The network apps use by default, which Android 7 and later follow
        if (!OperatingSystem.IsAndroidVersionAtLeast(24) || context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager connectivity) return;
        connectivity.RegisterDefaultNetworkCallback(new NetworkWatcher());
    }

    /// <summary>A default network came up; a change unless it is the one there was.</summary>
    public override void OnAvailable(Network network)
    {
        // Another network
        bool changed = current is not null && !network.Equals(current);
        current = network;
        if (changed) NetworkMonitor.NotifySystemChanged();
    }

    /// <summary>The default network's addresses changed, e.g. a new IPv6 address.</summary>
    public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties)
    {
        // Same network, new addresses
        if (network.Equals(current)) NetworkMonitor.NotifySystemChanged();
    }

    /// <summary>The default network went away.</summary>
    public override void OnLost(Network network)
    {
        // Gone; the next one is a change
        if (!network.Equals(current)) return;
        NetworkMonitor.NotifySystemChanged();
    }
}
