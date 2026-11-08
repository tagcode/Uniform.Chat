namespace Uniform.Turn;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Uniform.Stun.Protocol;

// STUN and TURN requests, indications and channel data

public sealed partial class TurnServer
{
    /// <summary>The protocol number of UDP in REQUESTED-TRANSPORT.</summary>
    private const byte Udp = 17;
    /// <summary>The IPv4 family in REQUESTED-ADDRESS-FAMILY.</summary>
    private const byte FamilyIPv4 = 0x01;

    /// <summary>Comprehension-required attribute types the server understands; a request with another gets 420 Unknown Attribute.</summary>
    private static readonly HashSet<ushort> KnownAttributes =
    [
        StunAttributeTypes.MappedAddress, StunAttributeTypes.Username, StunAttributeTypes.MessageIntegrity, StunAttributeTypes.ErrorCode,
        StunAttributeTypes.ChannelNumber, StunAttributeTypes.Lifetime, StunAttributeTypes.XorPeerAddress, StunAttributeTypes.Data,
        StunAttributeTypes.Realm, StunAttributeTypes.Nonce, StunAttributeTypes.RequestedAddressFamily, StunAttributeTypes.RequestedTransport,
        StunAttributeTypes.DontFragment, StunAttributeTypes.XorMappedAddress, StunAttributeTypes.Priority, StunAttributeTypes.UseCandidate,
    ];

    /// <summary>Handle a STUN message or channel data from <paramref name="client"/>; malformed input is dropped.</summary>
    internal async Task HandleAsync(ClientConnection client, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        // Channel data, then STUN with a correct fingerprint when it has one
        if (ChannelData.IsChannelData(bytes.Span))
        {
            await HandleChannelDataAsync(client, bytes, cancellationToken);
            return;
        }
        if (StunMessage.Parse(bytes.Span) is not StunMessage message) return;
        bool fingerprint = message.Get(StunAttributeTypes.Fingerprint) is not null;
        if (fingerprint && !StunMessage.CheckFingerprint(bytes.Span)) return;

        // Send indications, and requests
        if (message is { Class: StunClass.Indication, Method: StunMethod.Send }) await HandleSendAsync(client, message, cancellationToken);
        else if (message.Class == StunClass.Request) await HandleRequestAsync(client, message, bytes, fingerprint, cancellationToken);
    }

    /// <summary>Answer a request: Binding without credentials, the TURN methods with them.</summary>
    private async Task HandleRequestAsync(ClientConnection client, StunMessage request, ReadOnlyMemory<byte> bytes, bool fingerprint, CancellationToken cancellationToken)
    {
        // Unknown comprehension-required attributes are refused
        List<ushort> unknown = [.. request.Attributes.Select(a => a.Type).Where(t => StunAttributeTypes.IsComprehensionRequired(t) && !KnownAttributes.Contains(t)).Distinct()];
        if (unknown.Count > 0)
        {
            byte[] list = new byte[unknown.Count * 2];
            for (int i = 0; i < unknown.Count; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(list.AsSpan(i * 2), unknown[i]);
            await ErrorAsync(client, request, StunErrorCodes.UnknownAttribute, null, fingerprint, cancellationToken, StunAttributeTypes.UnknownAttributes, list);
            return;
        }

        // Binding: the address the client is seen from
        if (request.Method == StunMethod.Binding)
        {
            StunMessage response = new(StunMethod.Binding, StunClass.Success, request.TransactionId);
            response.Add(StunAttributeTypes.XorMappedAddress, StunAddress.EncodeXor(client.Remote, request.TransactionId));
            await RespondAsync(client, response, null, fingerprint, cancellationToken);
            return;
        }
        if (request.Method is not (StunMethod.Allocate or StunMethod.Refresh or StunMethod.CreatePermission or StunMethod.ChannelBind))
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, null, fingerprint, cancellationToken);
            return;
        }

        // TURN methods need credentials
        if (await AuthenticateAsync(client, request, bytes, fingerprint, cancellationToken) is not (string username, byte[] key)) return;
        Task handled = request.Method switch
        {
            StunMethod.Allocate => AllocateAsync(client, request, username, key, fingerprint, cancellationToken),
            StunMethod.Refresh => RefreshAsync(client, request, username, key, fingerprint, cancellationToken),
            StunMethod.CreatePermission => CreatePermissionAsync(client, request, username, key, fingerprint, cancellationToken),
            _ => ChannelBindAsync(client, request, username, key, fingerprint, cancellationToken),
        };
        // Done
        await handled;
    }

    /// <summary>Check the long-term credentials of a request; return the user and key, or null after answering 400, 401 or 438.</summary>
    private async Task<(string Username, byte[] Key)?> AuthenticateAsync(ClientConnection client, StunMessage request, ReadOnlyMemory<byte> bytes, bool fingerprint, CancellationToken cancellationToken)
    {
        // No integrity: a challenge with the realm and a nonce
        if (request.Get(StunAttributeTypes.MessageIntegrity) is null)
        {
            await ChallengeAsync(client, request, StunErrorCodes.Unauthorized, fingerprint, cancellationToken);
            return null;
        }

        // The parts of the credentials, then the nonce
        if (request.GetText(StunAttributeTypes.Username) is not string username || request.GetText(StunAttributeTypes.Realm) is null || request.GetText(StunAttributeTypes.Nonce) is not string nonce)
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, null, fingerprint, cancellationToken);
            return null;
        }
        if (!nonces.IsValid(nonce, client.Remote))
        {
            await ChallengeAsync(client, request, StunErrorCodes.StaleNonce, fingerprint, cancellationToken);
            return null;
        }

        // A key of the user that the integrity matches
        byte[]? key = authenticator.GetKeys(username, options.Realm).FirstOrDefault(k => StunMessage.CheckIntegrity(bytes.Span, k));
        if (key is null)
        {
            logger.LogDebug("Refused {Method} of {User} from {Client}: unknown user, expired or wrong credentials", request.Method, username, client.Remote);
            await ChallengeAsync(client, request, StunErrorCodes.Unauthorized, fingerprint, cancellationToken);
            return null;
        }
        // Return credentials
        return (username, key);
    }

    /// <summary>Make an allocation: a relay socket on a free port, for the lifetime asked, within the quotas.</summary>
    private async Task AllocateAsync(ClientConnection client, StunMessage request, string username, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // A retransmission gets the same answer; another request on the same 5-tuple is a mismatch
        if (allocations.TryGetValue(client.Key, out Allocation? existing))
        {
            if (existing.AllocateTransaction.AsSpan().SequenceEqual(request.TransactionId)) await client.SendAsync(existing.AllocateResponse, cancellationToken);
            else await ErrorAsync(client, request, StunErrorCodes.AllocationMismatch, key, fingerprint, cancellationToken);
            return;
        }

        // UDP relaying over IPv4 only
        if (request.Get(StunAttributeTypes.RequestedTransport) is not { Length: 4 } transport)
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, key, fingerprint, cancellationToken);
            return;
        }
        if (transport[0] != Udp)
        {
            await ErrorAsync(client, request, StunErrorCodes.UnsupportedTransportProtocol, key, fingerprint, cancellationToken);
            return;
        }
        if (request.Get(StunAttributeTypes.RequestedAddressFamily) is { Length: >= 1 } family && family[0] != FamilyIPv4)
        {
            await ErrorAsync(client, request, StunErrorCodes.AddressFamilyNotSupported, key, fingerprint, cancellationToken);
            return;
        }

        // Quotas of the user and the server
        if (allocations.Values.Count(a => a.Username == username) >= options.MaxAllocationsPerUser)
        {
            await ErrorAsync(client, request, StunErrorCodes.AllocationQuotaReached, key, fingerprint, cancellationToken);
            return;
        }
        if (allocations.Count >= options.MaxAllocations || OpenRelay() is not Socket relay)
        {
            logger.LogWarning("Refused an allocation of {User}: the server has no room for more", username);
            await ErrorAsync(client, request, StunErrorCodes.InsufficientCapacity, key, fingerprint, cancellationToken);
            return;
        }

        // The allocation, its answer kept for retransmissions, then relaying
        TimeSpan lifetime = Lifetime(request);
        IPEndPoint relayed = new(externalAddress, ((IPEndPoint)relay.LocalEndPoint!).Port);
        Allocation allocation = new(client, username, key, relay, relayed, time.GetUtcNow() + lifetime) { AllocateTransaction = request.TransactionId };
        StunMessage response = new(StunMethod.Allocate, StunClass.Success, request.TransactionId);
        response.Add(StunAttributeTypes.XorRelayedAddress, StunAddress.EncodeXor(relayed, request.TransactionId))
            .Add(StunAttributeTypes.Lifetime, (uint)lifetime.TotalSeconds)
            .Add(StunAttributeTypes.XorMappedAddress, StunAddress.EncodeXor(client.Remote, request.TransactionId))
            .Add(StunAttributeTypes.Software, options.Software);
        allocation.AllocateResponse = response.Encode(key, fingerprint);
        Add(allocation);
        // Done
        await client.SendAsync(allocation.AllocateResponse, cancellationToken);
    }

    /// <summary>Refresh an allocation for the lifetime asked, or end it with a lifetime of zero.</summary>
    private async Task RefreshAsync(ClientConnection client, StunMessage request, string username, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // The client's own allocation
        if (await FindAllocationAsync(client, request, username, key, fingerprint, cancellationToken) is not Allocation allocation) return;

        // Zero ends it; else the new lifetime
        StunMessage response = new(StunMethod.Refresh, StunClass.Success, request.TransactionId);
        if (request.GetUInt32(StunAttributeTypes.Lifetime) == 0)
        {
            Remove(allocation, "released by the client");
            response.Add(StunAttributeTypes.Lifetime, 0u);
        }
        else
        {
            TimeSpan lifetime = Lifetime(request);
            lock (allocation.Gate) allocation.Expires = time.GetUtcNow() + lifetime;
            response.Add(StunAttributeTypes.Lifetime, (uint)lifetime.TotalSeconds);
        }
        // Done
        await RespondAsync(client, response, key, fingerprint, cancellationToken);
    }

    /// <summary>Permit the peers of the request's XOR-PEER-ADDRESS attributes, all or none.</summary>
    private async Task CreatePermissionAsync(ClientConnection client, StunMessage request, string username, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // The client's own allocation, and at least one peer
        if (await FindAllocationAsync(client, request, username, key, fingerprint, cancellationToken) is not Allocation allocation) return;
        List<IPEndPoint?> peers = [.. request.Attributes.Where(a => a.Type == StunAttributeTypes.XorPeerAddress).Select(a => StunAddress.DecodeXor(a.Value, request.TransactionId))];
        if (peers.Count == 0 || peers.Any(p => p is null))
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, key, fingerprint, cancellationToken);
            return;
        }
        if (await CheckPeersAsync(client, request, peers!, key, fingerprint, cancellationToken) is false) return;

        // Each permitted for five minutes
        DateTimeOffset until = time.GetUtcNow() + Allocation.PermissionLifetime;
        lock (allocation.Gate)
        {
            foreach (IPEndPoint peer in peers!) allocation.Permissions[peer.Address] = until;
        }
        // Done
        await RespondAsync(client, new StunMessage(StunMethod.CreatePermission, StunClass.Success, request.TransactionId), key, fingerprint, cancellationToken);
    }

    /// <summary>Bind a channel to a peer, or refresh the binding; it permits the peer too.</summary>
    private async Task ChannelBindAsync(ClientConnection client, StunMessage request, string username, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // The client's own allocation, a valid channel and one peer
        if (await FindAllocationAsync(client, request, username, key, fingerprint, cancellationToken) is not Allocation allocation) return;
        ushort channel = request.Get(StunAttributeTypes.ChannelNumber) is { Length: 4 } number ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(number) : (ushort)0;
        IPEndPoint? peer = request.Get(StunAttributeTypes.XorPeerAddress) is byte[] address ? StunAddress.DecodeXor(address, request.TransactionId) : null;
        if (!ChannelData.IsValidChannel(channel) || peer is null)
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, key, fingerprint, cancellationToken);
            return;
        }
        if (await CheckPeersAsync(client, request, [peer], key, fingerprint, cancellationToken) is false) return;

        // A channel has one peer and a peer one channel, for the binding's life
        DateTimeOffset now = time.GetUtcNow();
        bool conflict;
        lock (allocation.Gate)
        {
            conflict = (allocation.Channels.TryGetValue(channel, out (IPEndPoint Peer, DateTimeOffset Expires) bound) && bound.Expires > now && !bound.Peer.Equals(peer))
                || (allocation.ChannelOfPeer.TryGetValue(peer, out ushort other) && other != channel && allocation.Channels[other].Expires > now);
            if (!conflict)
            {
                if (allocation.ChannelOfPeer.TryGetValue(peer, out ushort old)) allocation.Channels.Remove(old);
                allocation.Channels[channel] = (peer, now + Allocation.ChannelLifetime);
                allocation.ChannelOfPeer[peer] = channel;
                allocation.Permissions[peer.Address] = now + Allocation.PermissionLifetime;
            }
        }
        if (conflict)
        {
            await ErrorAsync(client, request, StunErrorCodes.BadRequest, key, fingerprint, cancellationToken);
            return;
        }
        // Done
        await RespondAsync(client, new StunMessage(StunMethod.ChannelBind, StunClass.Success, request.TransactionId), key, fingerprint, cancellationToken);
    }

    /// <summary>Relay the data of a Send indication to its peer, when permitted; indications get no answer.</summary>
    private async Task HandleSendAsync(ClientConnection client, StunMessage indication, CancellationToken cancellationToken)
    {
        // Allocation, peer, data and permission
        if (!allocations.TryGetValue(client.Key, out Allocation? allocation)) return;
        if (indication.Get(StunAttributeTypes.XorPeerAddress) is not byte[] address || StunAddress.DecodeXor(address, indication.TransactionId) is not IPEndPoint peer) return;
        if (indication.Get(StunAttributeTypes.Data) is not byte[] data) return;
        lock (allocation.Gate)
        {
            if (!allocation.IsPermitted(peer.Address, time.GetUtcNow())) return;
        }
        // Done
        await SendToPeerAsync(allocation, peer, data, cancellationToken);
    }

    /// <summary>Relay channel data to the channel's peer, when bound.</summary>
    private async Task HandleChannelDataAsync(ClientConnection client, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        // Allocation and the bound peer
        if (!allocations.TryGetValue(client.Key, out Allocation? allocation)) return;
        if (!ChannelData.TryParse(bytes.Span, out ushort channel, out ReadOnlySpan<byte> data)) return;
        int offset = ChannelData.HeaderLength, length = data.Length;
        IPEndPoint peer;
        lock (allocation.Gate)
        {
            if (!allocation.Channels.TryGetValue(channel, out (IPEndPoint Peer, DateTimeOffset Expires) bound) || bound.Expires <= time.GetUtcNow()) return;
            peer = bound.Peer;
        }
        // Done
        await SendToPeerAsync(allocation, peer, bytes.Slice(offset, length), cancellationToken);
    }

    /// <summary>Return the client's allocation, or null after answering 437 when it has none or 441 when it belongs to another user.</summary>
    private async Task<Allocation?> FindAllocationAsync(ClientConnection client, StunMessage request, string username, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // Same 5-tuple and user
        if (!allocations.TryGetValue(client.Key, out Allocation? allocation))
        {
            await ErrorAsync(client, request, StunErrorCodes.AllocationMismatch, key, fingerprint, cancellationToken);
            return null;
        }
        if (allocation.Username != username)
        {
            await ErrorAsync(client, request, StunErrorCodes.WrongCredentials, key, fingerprint, cancellationToken);
            return null;
        }
        // Return allocation
        return allocation;
    }

    /// <summary>Return whether every peer may be relayed to, after answering 443 for an IPv6 peer or 403 for a denied one.</summary>
    private async Task<bool> CheckPeersAsync(ClientConnection client, StunMessage request, IReadOnlyList<IPEndPoint> peers, byte[] key, bool fingerprint, CancellationToken cancellationToken)
    {
        // IPv4 only, then the lists
        if (peers.Any(p => p.AddressFamily != AddressFamily.InterNetwork))
        {
            await ErrorAsync(client, request, StunErrorCodes.PeerAddressFamilyMismatch, key, fingerprint, cancellationToken);
            return false;
        }
        if (peers.FirstOrDefault(p => !MayRelayTo(p.Address)) is IPEndPoint denied)
        {
            logger.LogInformation("Refused relaying to {Peer} for {Client}: the address is not allowed", denied.Address, client.Remote);
            await ErrorAsync(client, request, StunErrorCodes.Forbidden, key, fingerprint, cancellationToken);
            return false;
        }
        // Return allowed
        return true;
    }

    /// <summary>Return the lifetime a request asks, at least the default and at most the maximum.</summary>
    private TimeSpan Lifetime(StunMessage request)
    {
        // Asked seconds, clamped
        TimeSpan asked = request.GetUInt32(StunAttributeTypes.Lifetime) is uint seconds ? TimeSpan.FromSeconds(seconds) : options.DefaultLifetime;
        // Return lifetime
        return asked < options.DefaultLifetime ? options.DefaultLifetime : asked > options.MaxLifetime ? options.MaxLifetime : asked;
    }

    /// <summary>Answer 401 or 438 with the realm and a new nonce, without integrity.</summary>
    private Task ChallengeAsync(ClientConnection client, StunMessage request, int code, bool fingerprint, CancellationToken cancellationToken)
    {
        // Error with realm and nonce
        StunMessage response = new(request.Method, StunClass.Error, request.TransactionId);
        response.AddError(code).Add(StunAttributeTypes.Realm, options.Realm).Add(StunAttributeTypes.Nonce, nonces.Create(client.Remote));
        // Done
        return RespondAsync(client, response, null, fingerprint, cancellationToken);
    }

    /// <summary>Answer an error, with integrity when the request was authenticated, and one extra attribute when given.</summary>
    private Task ErrorAsync(ClientConnection client, StunMessage request, int code, byte[]? key, bool fingerprint, CancellationToken cancellationToken, ushort extraType = 0, byte[]? extraValue = null)
    {
        // Error code, then the extra attribute
        StunMessage response = new(request.Method, StunClass.Error, request.TransactionId);
        response.AddError(code);
        if (extraValue is not null) response.Add(extraType, extraValue);
        // Done
        return RespondAsync(client, response, key, fingerprint, cancellationToken);
    }

    /// <summary>Send a response with the software name, integrity with <paramref name="key"/> when given, and a fingerprint when the request had one.</summary>
    private async Task RespondAsync(ClientConnection client, StunMessage response, byte[]? key, bool fingerprint, CancellationToken cancellationToken)
    {
        // Software, then encode
        if (response.Get(StunAttributeTypes.Software) is null) response.Add(StunAttributeTypes.Software, options.Software);
        // Done
        await client.SendAsync(response.Encode(key, fingerprint), cancellationToken);
    }
}
