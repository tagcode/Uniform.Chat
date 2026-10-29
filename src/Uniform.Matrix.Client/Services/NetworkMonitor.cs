namespace Uniform.Matrix.Client.Services;

using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

/// <summary>Tells when the device's network changed, e.g. another Wi-Fi or mobile data, so that the sync and calls can move to it at once instead of waiting for requests on the old network to time out.</summary>
/// <remarks>Follows .NET's <see cref="NetworkChange.NetworkAddressChanged"/> where the platform supports it; apps whose platform reports changes otherwise, as Android does, call <see cref="NotifySystemChanged"/>, which every monitor follows. Changes within <see cref="Settle"/> of each other are told once.</remarks>
public sealed class NetworkMonitor : IDisposable
{
    /// <summary>How long to wait for a change to settle before telling, as one switch changes several addresses.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private readonly ILogger<NetworkMonitor> logger;
    private readonly bool followsSystem;
    private readonly Lock gate = new();
    private Timer? pending;

    /// <summary>Follow the system's address changes where supported.</summary>
    public NetworkMonitor(ILogger<NetworkMonitor> logger)
    {
        // The app's reports, and the system's event where there is one
        this.logger = logger;
        SystemChanged += NotifyChanged;
        try
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            followsSystem = true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
        {
            logger.LogInformation("Network changes are not reported by this platform; the app reports them");
        }
    }

    /// <summary>Raised by <see cref="NotifySystemChanged"/>.</summary>
    private static event Action? SystemChanged;

    /// <summary>Tell every monitor that the network changed, e.g. from Android's connectivity callbacks, which run outside the client's services.</summary>
    public static void NotifySystemChanged() => SystemChanged?.Invoke();

    /// <summary>Raised once a change settled; on a thread pool thread.</summary>
    public event Action? Changed;

    /// <summary>Tell that the network changed, e.g. from Android's connectivity callbacks.</summary>
    public void NotifyChanged()
    {
        // Restart the settle timer
        lock (gate)
        {
            pending?.Dispose();
            pending = new Timer(_ => Raise(), null, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stop following the system.</summary>
    public void Dispose()
    {
        // Events and timer
        SystemChanged -= NotifyChanged;
        if (followsSystem) NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (gate) pending?.Dispose();
    }

    /// <summary>Pass on the system's change.</summary>
    private void OnAddressChanged(object? sender, EventArgs e) => NotifyChanged();

    /// <summary>Tell the listeners.</summary>
    private void Raise()
    {
        // Logged, then raised
        logger.LogInformation("Network changed");
        Changed?.Invoke();
    }
}
