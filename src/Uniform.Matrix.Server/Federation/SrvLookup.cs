namespace Uniform.Matrix.Server.Federation;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Uniform.Matrix.Protocol.Server;

/// <summary>Looks up DNS SRV records over UDP at the system's name servers, for <see href="https://spec.matrix.org/latest/server-server-api/#resolving-server-names">resolving server names</see> that delegate with <c>_matrix-fed._tcp</c>.</summary>
/// <remarks>Each name server of the network interfaces that are up is asked in turn until one answers; truncated answers are not retried over TCP, as SRV answers are small.</remarks>
public class SrvLookup
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly ILogger<SrvLookup> logger;

    /// <summary>Create the lookup.</summary>
    public SrvLookup(ILogger<SrvLookup> logger)
    {
        // Store dependencies
        this.logger = logger;
    }

    /// <summary>Return the SRV records of <paramref name="name"/>, best first; empty when it has none or no name server answers.</summary>
    /// <param name="name">E.g. <c>_matrix-fed._tcp.example.org</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual async Task<IReadOnlyList<SrvRecord>> LookupAsync(string name, CancellationToken cancellationToken)
    {
        // Each name server until one answers
        ushort id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        byte[] query = DnsSrv.BuildQuery(id, name);
        foreach (IPAddress server in NameServers())
        {
            try
            {
                using UdpClient udp = new(server.AddressFamily);
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(Timeout);
                await udp.SendAsync(query, new IPEndPoint(server, 53), limit.Token);
                UdpReceiveResult result = await udp.ReceiveAsync(limit.Token);
                if (DnsSrv.ParseAnswer(result.Buffer, id) is IReadOnlyList<SrvRecord> records) return [.. records.Where(r => r.Target != ".")];
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Name server {Server} did not answer for {Name}: {Error}", server, name, ex.Message);
            }
        }
        // Return none
        return [];
    }

    /// <summary>Return the name servers of the network interfaces that are up, IPv4 first.</summary>
    private static IEnumerable<IPAddress> NameServers() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => !a.IsIPv6LinkLocal).Distinct().OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6);
}
