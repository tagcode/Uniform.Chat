namespace Uniform.Stun;

using System.Net;
using System.Net.Sockets;
using Uniform.Stun.Protocol;

/// <summary>Finds the public address of this computer, as a STUN server sees it: e.g. the router's address behind NAT.</summary>
public static class StunDiscovery
{
    /// <summary>Return the address <paramref name="server"/> sees requests from <paramref name="local"/> come from, or null when it does not answer.</summary>
    /// <param name="server">The STUN server, e.g. port 3478 of a TURN server.</param>
    /// <param name="local">Local address to send from, as on computers with several networks the route depends on it; any when null.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<IPAddress?> GetPublicAddressAsync(IPEndPoint server, IPAddress? local = null, CancellationToken cancellationToken = default)
    {
        // A socket of its own, read until the response
        using UdpClient udp = new(new IPEndPoint(local ?? IPAddress.Any, 0));
        StunClient stun = new((datagram, to, token) => new ValueTask(udp.SendAsync(datagram, to, token).AsTask())) { MaxSends = 4 };
        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task read = Task.Run(async () =>
        {
            while (!reading.IsCancellationRequested) stun.Receive((await udp.ReceiveAsync(reading.Token)).Buffer);
        }, reading.Token);

        // Binding, then the mapped address
        StunMessage? response = await stun.RequestAsync(new StunMessage(StunMethod.Binding, StunClass.Request), server, cancellationToken: cancellationToken);
        await reading.CancelAsync();
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (response?.Get(StunAttributeTypes.XorMappedAddress) is not byte[] mapped) return null;
        // Return address
        return StunAddress.DecodeXor(mapped, response.TransactionId)?.Address;
    }
}
