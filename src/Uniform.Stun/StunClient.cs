namespace Uniform.Matrix.WebRtc.Stun;

using System.Collections.Concurrent;
using System.Net;

/// <summary>Sends a datagram to an address, e.g. through the UDP socket a call uses.</summary>
public delegate ValueTask DatagramSender(ReadOnlyMemory<byte> datagram, IPEndPoint to, CancellationToken cancellationToken);

/// <summary>STUN transactions over a datagram socket someone else reads: sends requests, retransmits them as <see href="https://www.rfc-editor.org/rfc/rfc8489#section-6.2.1">RFC 8489 6.2.1</see> says, and completes them with the responses fed to <see cref="Receive"/>.</summary>
/// <param name="send">Sends a datagram.</param>
public sealed class StunClient(DatagramSender send)
{
    /// <summary>First retransmission timeout; it doubles after each.</summary>
    public TimeSpan InitialTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Number of times a request is sent at most.</summary>
    public int MaxSends { get; init; } = 7;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StunMessage>> pending = new();

    /// <summary>Send a request to <paramref name="server"/> until a response comes; return it, or null when none came.</summary>
    /// <param name="request">Request to send.</param>
    /// <param name="server">Address to send it to.</param>
    /// <param name="integrityKey">Key of the MESSAGE-INTEGRITY attribute, when the request needs one.</param>
    /// <param name="fingerprint">Whether to add a FINGERPRINT attribute, as ICE does.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    public async Task<StunMessage?> RequestAsync(StunMessage request, IPEndPoint server, byte[]? integrityKey = null, bool fingerprint = false, CancellationToken cancellationToken = default)
    {
        // Registered before the first send, so a fast response is not missed
        string id = Convert.ToHexString(request.TransactionId);
        TaskCompletionSource<StunMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        byte[] datagram = request.Encode(integrityKey, fingerprint);
        try
        {
            // Sent again after each timeout, the timeout doubling; the last waits sixteen times the first
            TimeSpan timeout = InitialTimeout;
            for (int sent = 0; sent < MaxSends; sent++)
            {
                await send(datagram, server, cancellationToken);
                TimeSpan wait = sent == MaxSends - 1 ? InitialTimeout * 16 : timeout;
                Task finished = await Task.WhenAny(response.Task, Task.Delay(wait, cancellationToken));
                if (finished == response.Task) return await response.Task;
                cancellationToken.ThrowIfCancellationRequested();
                timeout *= 2;
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
        // On no response
        return null;
    }

    /// <summary>Send an indication, which has no response.</summary>
    public ValueTask IndicateAsync(StunMessage indication, IPEndPoint to, CancellationToken cancellationToken = default) => send(indication.Encode(), to, cancellationToken);

    /// <summary>Complete the transaction that <paramref name="datagram"/>, a STUN message, answers; return whether one did.</summary>
    public bool Receive(ReadOnlySpan<byte> datagram)
    {
        // Only responses to our requests
        if (StunMessage.Parse(datagram) is not StunMessage message) return false;
        if (message.Class is not (StunClass.Success or StunClass.Error)) return false;
        if (!pending.TryRemove(Convert.ToHexString(message.TransactionId), out TaskCompletionSource<StunMessage>? response)) return false;
        // Done
        return response.TrySetResult(message);
    }
}
