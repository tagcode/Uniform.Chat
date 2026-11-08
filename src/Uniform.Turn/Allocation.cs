namespace Uniform.Turn;

using System.Net;
using System.Net.Sockets;

/// <summary>A client's allocation: its relay socket and relayed address, the user it belongs to, and its permissions and channels with their expiry. Members other than the immutable ones are used under <see cref="Gate"/>.</summary>
internal sealed class Allocation
{
    /// <summary>How long a permission lasts unless refreshed.</summary>
    public static readonly TimeSpan PermissionLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long a channel binding lasts unless refreshed.</summary>
    public static readonly TimeSpan ChannelLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Create an allocation on <paramref name="relay"/>.</summary>
    public Allocation(ClientConnection client, string username, byte[] key, Socket relay, IPEndPoint relayedAddress, DateTimeOffset expires)
    {
        // Immutable parts, then the lifetime
        Client = client;
        Username = username;
        Key = key;
        Relay = relay;
        RelayedAddress = relayedAddress;
        Expires = expires;
    }

    /// <summary>The client it belongs to.</summary>
    public ClientConnection Client { get; }

    /// <summary>The user name it was made with; refreshes must use the same.</summary>
    public string Username { get; }

    /// <summary>The key of the user's credentials, for the integrity of responses.</summary>
    public byte[] Key { get; set; }

    /// <summary>The relay socket peers send to.</summary>
    public Socket Relay { get; }

    /// <summary>The relayed address clients and peers see.</summary>
    public IPEndPoint RelayedAddress { get; }

    /// <summary>Guards the mutable state.</summary>
    public Lock Gate { get; } = new();

    /// <summary>When it ends unless refreshed.</summary>
    public DateTimeOffset Expires { get; set; }

    /// <summary>Permitted peer addresses and when each permission ends.</summary>
    public Dictionary<IPAddress, DateTimeOffset> Permissions { get; } = [];

    /// <summary>Bound channels: the peer and when the binding ends.</summary>
    public Dictionary<ushort, (IPEndPoint Peer, DateTimeOffset Expires)> Channels { get; } = [];

    /// <summary>The channel of each bound peer.</summary>
    public Dictionary<IPEndPoint, ushort> ChannelOfPeer { get; } = [];

    /// <summary>Transaction ID of the Allocate request that made it, to answer its retransmissions.</summary>
    public byte[] AllocateTransaction { get; set; } = [];

    /// <summary>The response to that request.</summary>
    public byte[] AllocateResponse { get; set; } = [];

    /// <summary>Bytes relayed both ways.</summary>
    public long BytesRelayed;

    /// <summary>Stops the relay loop.</summary>
    public CancellationTokenSource Stopping { get; } = new();

    /// <summary>Return whether <paramref name="peer"/> may send or be sent to now; call under <see cref="Gate"/>.</summary>
    public bool IsPermitted(IPAddress peer, DateTimeOffset now) => Permissions.TryGetValue(peer, out DateTimeOffset until) && until > now;

    /// <summary>Return the channel bound to <paramref name="peer"/> now, or null; call under <see cref="Gate"/>.</summary>
    public ushort? ChannelOf(IPEndPoint peer, DateTimeOffset now) => ChannelOfPeer.TryGetValue(peer, out ushort channel) && Channels[channel].Expires > now ? channel : null;

    /// <summary>Remove expired permissions and channels; call under <see cref="Gate"/>.</summary>
    public void RemoveExpired(DateTimeOffset now)
    {
        // Permissions, then channels and their peers
        foreach (IPAddress peer in Permissions.Where(p => p.Value <= now).Select(p => p.Key).ToList()) Permissions.Remove(peer);
        foreach ((ushort channel, (IPEndPoint peer, _)) in Channels.Where(c => c.Value.Expires <= now).ToList())
        {
            Channels.Remove(channel);
            ChannelOfPeer.Remove(peer);
        }
    }
}
