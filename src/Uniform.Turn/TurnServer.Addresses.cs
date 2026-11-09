namespace Uniform.Turn;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

// UDP listeners per address, and following changes of the local and public addresses

public sealed partial class TurnServer
{
    /// <summary>Released when the computer's network addresses change, to check at once instead of at the next interval.</summary>
    private readonly SemaphoreSlim addressesChanged = new(0, 1);

    /// <summary>Bind UDP on the listen address, or on each local IPv4 address when it is 0.0.0.0; throw when none can be bound.</summary>
    private void StartUdpListeners()
    {
        // The first socket chooses the port when it is 0, the others use it
        foreach (IPAddress address in UdpAddresses()) AddUdpListener(address, UdpEndPoint?.Port ?? options.Port);
        if (udpListeners.IsEmpty) throw new InvalidOperationException("The TURN server found no IPv4 address to listen on.");
        // Done
        if (listensOnAll) UdpEndPoint = new IPEndPoint(IPAddress.Any, UdpEndPoint!.Port);
    }

    /// <summary>Bind a UDP listener on <paramref name="address"/> and start reading it.</summary>
    private void AddUdpListener(IPAddress address, int port)
    {
        // Bind, then read until removed or stopped
        Socket udp = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) udp.DualMode = true;
            udp.Bind(new IPEndPoint(address, port));
        }
        catch
        {
            udp.Dispose();
            throw;
        }
        udpListeners[address] = udp;
        UdpEndPoint ??= (IPEndPoint)udp.LocalEndPoint!;
        // Done
        loops.Add(ReceiveUdpAsync(udp, stopping.Token));
    }

    /// <summary>Return the addresses UDP listens on: the listen address, or each IPv4 address of the interfaces that are up when it is 0.0.0.0, as replies must leave from the address a request came to and a socket on 0.0.0.0 lets the routing choose another on computers with several networks.</summary>
    private IEnumerable<IPAddress> UdpAddresses()
    {
        // One address as configured, else the interfaces', or the test's
        if (!listensOnAll) return [options.ListenAddress];
        if (options.LocalAddresses is { } local) return local();
        // Return each IPv4 address that is up
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up || i.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();
    }

    /// <summary>Check the addresses every <see cref="TurnServerOptions.AddressCheckInterval"/>, and when the computer's addresses change, until stopped.</summary>
    private async Task WatchAddressesAsync(CancellationToken cancellationToken)
    {
        // Changes reported by the system wake the loop early
        NetworkAddressChangedEventHandler changed = (_, _) => { if (addressesChanged.CurrentCount == 0) addressesChanged.Release(); };
        NetworkChange.NetworkAddressChanged += changed;
        try
        {
            // One wait for the signal at a time, kept across intervals, so no release is lost
            Task? signal = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                signal ??= addressesChanged.WaitAsync(cancellationToken);
                Task woke = await Task.WhenAny(signal, Task.Delay(options.AddressCheckInterval, time, cancellationToken));
                if (woke == signal) signal = null;
                cancellationToken.ThrowIfCancellationRequested();
                await CheckAddressesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // On stop
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= changed;
        }
    }

    /// <summary>Follow changes of the local and public addresses now: listen on new local addresses, end what used vanished ones, and give new allocations the current public address.</summary>
    internal async Task CheckAddressesAsync(CancellationToken cancellationToken = default)
    {
        // Local first, as a new lease brings both
        RefreshUdpListeners();
        await RefreshExternalAddressAsync(cancellationToken);
    }

    /// <summary>When listening on 0.0.0.0, bind UDP on addresses that appeared and close it on those that went, ending the allocations that used them.</summary>
    private void RefreshUdpListeners()
    {
        // Only when listening on every address
        if (!listensOnAll || UdpEndPoint is null) return;
        HashSet<IPAddress> current = [.. UdpAddresses()];

        // New addresses
        foreach (IPAddress address in current.Where(a => !udpListeners.ContainsKey(a)))
        {
            try
            {
                AddUdpListener(address, UdpEndPoint.Port);
                logger.LogInformation("TURN server listening on new address udp {Address}:{Port}", address, UdpEndPoint.Port);
            }
            catch (SocketException ex)
            {
                logger.LogWarning("The TURN server cannot listen on new address {Address}:{Port}: {Error}", address, UdpEndPoint.Port, ex.Message);
            }
        }

        // Gone addresses, and the allocations on them, whose clients allocate again
        foreach (IPAddress address in udpListeners.Keys.Where(a => !current.Contains(a)).ToList())
        {
            if (udpListeners.TryRemove(address, out Socket? udp)) udp.Dispose();
            logger.LogInformation("TURN server stopped listening on {Address}, which the computer no longer has", address);
            foreach (Allocation allocation in allocations.Values.Where(a => a.Client.Local.Address.Equals(address) || (a.Relay.LocalEndPoint as IPEndPoint)?.Address.Equals(address) == true)) Remove(allocation, $"address {address} gone");
        }
    }

    /// <summary>Ask <see cref="TurnServerOptions.ExternalAddressSource"/> for the public address, and give new allocations it when it changed; failures keep the current one.</summary>
    private async Task RefreshExternalAddressAsync(CancellationToken cancellationToken)
    {
        // A source, and an IPv4 answer
        if (options.ExternalAddress is not null || options.ExternalAddressSource is not { } source) return;
        IPAddress? found;
        try
        {
            found = await source(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Cannot check the TURN server's public address, keeping {Address}: {Error}", externalAddress, ex.Message);
            return;
        }
        if (found is null || found.AddressFamily != AddressFamily.InterNetwork || found.Equals(externalAddress)) return;
        // Done
        logger.LogWarning("The TURN server's public address changed from {Old} to {New}; new allocations get the new one", externalAddress, found);
        externalAddress = found;
    }
}
