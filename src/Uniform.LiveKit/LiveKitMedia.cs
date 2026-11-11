namespace Uniform.LiveKit;

using System.Collections.Concurrent;
using global::LiveKit.Proto;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>The media of a call through LiveKit, as one <see cref="IMediaConnection"/>: this side's frames encrypted and sent on the room it publishes to, and the others' received on the rooms it subscribes to, decrypted with each sender's key.</summary>
/// <remarks>With MatrixRTC's many SFUs, each member publishes on its own homeserver's SFU, and the others subscribe there; a call has one room to publish to and a room for each SFU the others publish on, the same one when they share it. Without encryption, frames go and come as they are.</remarks>
public sealed class LiveKitMedia : IMediaConnection
{
    private readonly ConcurrentDictionary<string, FrameCryptor> keys = new();
    private readonly List<LiveKitRoom> subscribed = [];
    private readonly Lock gate = new();
    private AudioPeerConnection publisher;
    private uint audioClock;
    private uint videoClock;

    /// <summary>Send on <paramref name="room"/>'s publisher connection, encrypting with <see cref="OwnKey"/> when <paramref name="encrypted"/>.</summary>
    /// <exception cref="InvalidOperationException">When the room has not published.</exception>
    public LiveKitMedia(LiveKitRoom room, bool encrypted)
    {
        // The publisher and its feedback
        publisher = room.Publisher ?? throw new InvalidOperationException("Publish first.");
        Encrypted = encrypted;
        Follow(publisher);
        // Done
        Subscribe(room);
    }

    /// <summary>Move the media from <paramref name="old"/> to <paramref name="replacement"/>, a room joined again, e.g. after the network changed: publishing there when <paramref name="old"/> published, and receiving there; the voice and video pipelines stay as they are.</summary>
    /// <exception cref="InvalidOperationException">When <paramref name="old"/> published and <paramref name="replacement"/> has not.</exception>
    public void Replace(LiveKitRoom old, LiveKitRoom replacement)
    {
        // The publisher, then the subscription
        if (ReferenceEquals(old.Publisher, publisher))
        {
            publisher = replacement.Publisher ?? throw new InvalidOperationException("Publish first.");
            Follow(publisher);
        }
        Unsubscribe(old);
        // Done
        Subscribe(replacement);
    }

    /// <summary>Stop receiving on <paramref name="room"/>, e.g. one that is left.</summary>
    public void Unsubscribe(LiveKitRoom room)
    {
        // Done
        lock (gate) subscribed.Remove(room);
    }

    /// <summary>Pass a publisher's feedback on, while it is the current one.</summary>
    private void Follow(AudioPeerConnection connection)
    {
        // Only the current publisher's
        connection.KeyframeRequested += () => { if (ReferenceEquals(connection, publisher)) KeyframeRequested?.Invoke(); };
        // Done
        connection.BitrateRequested += bitrate => { if (ReferenceEquals(connection, publisher)) BitrateRequested?.Invoke(bitrate); };
    }

    /// <summary>Whether frames are encrypted end to end.</summary>
    public bool Encrypted { get; }

    /// <summary>This side's key, that its frames are encrypted with; set it and send it to the others.</summary>
    public FrameCryptor OwnKey { get; } = new();

    /// <inheritdoc/>
    public event Action<RtpPacket>? AudioReceived;

    /// <inheritdoc/>
    public event Action<Vp8EncodedFrame>? VideoReceived;

    /// <inheritdoc/>
    public event Action<long>? BitrateRequested;

    /// <inheritdoc/>
    public event Action? KeyframeRequested;

    /// <summary>Raised with the sender's identity when its frame did not decrypt, e.g. before its key came; on a network thread.</summary>
    public event Action<string>? DecryptionFailed;

    /// <inheritdoc/>
    public bool ReceivesVideo => true;

    /// <inheritdoc/>
    public bool SendsVideo => publisher.SendsVideo;

    /// <inheritdoc/>
    public bool SendsOrientation => publisher.SendsOrientation;

    /// <summary>Frames received from someone whose key is not known.</summary>
    public long FramesWithoutKey => Interlocked.Read(ref framesWithoutKey);
    private long framesWithoutKey;

    /// <summary>Set the key of <paramref name="identity"/>, a participant on the SFUs, e.g. <c>@bob:example.org:DEVICE</c>, at <paramref name="index"/>.</summary>
    public void SetKey(string identity, byte index, ReadOnlySpan<byte> rawKey) => keys.GetOrAdd(identity, _ => new FrameCryptor()).SetKey(index, rawKey);

    /// <summary>Receive the others' media on <paramref name="room"/>, e.g. another member's SFU.</summary>
    public void Subscribe(LiveKitRoom room)
    {
        // Once each; its subscriber now or when the server offers it
        lock (gate)
        {
            if (subscribed.Contains(room)) return;
            subscribed.Add(room);
        }
        if (room.Subscriber is AudioPeerConnection existing) Listen(room, existing);
        // Done
        room.SubscriberCreated += subscriber => Listen(room, subscriber);
    }

    /// <inheritdoc/>
    public ValueTask SendAudioAsync(byte[] opus, int samples = AudioPeerConnection.FrameSamples, CancellationToken cancellationToken = default)
    {
        // Encrypted when the call is
        uint clock = audioClock += (uint)samples;
        // Done
        return publisher.SendAudioAsync(Encrypted ? OwnKey.Encrypt(opus, FrameKind.Audio, 1, clock) : opus, samples, cancellationToken);
    }

    /// <inheritdoc/>
    public void SkipAudio(int samples = AudioPeerConnection.FrameSamples)
    {
        // Done
        audioClock += (uint)samples;
        publisher.SkipAudio(samples);
    }

    /// <inheritdoc/>
    public ValueTask SendVideoAsync(byte[] frame, CancellationToken cancellationToken = default, int rotation = 0)
    {
        // Encrypted when the call is
        uint clock = ++videoClock;
        // Done
        return publisher.SendVideoAsync(Encrypted ? OwnKey.Encrypt(frame, FrameKind.Vp8, 2, clock) : frame, cancellationToken, rotation);
    }

    /// <inheritdoc/>
    public Task RequestKeyframeAsync() => Task.WhenAll(Subscribers().Select(s => s.RequestKeyframeAsync()));

    /// <summary>Ask the SFUs for a video layer that fits <paramref name="bitsPerSecond"/>, and the senders for at most it.</summary>
    public Task RequestBitrateAsync(long bitsPerSecond)
    {
        // The layer, low, medium or high, as the senders' simulcast has them
        VideoQuality quality = bitsPerSecond < 400_000 ? VideoQuality.Low : bitsPerSecond < 1_000_000 ? VideoQuality.Medium : VideoQuality.High;
        LiveKitRoom[] rooms;
        lock (gate) rooms = [.. subscribed];
        // Done
        return Task.WhenAll([.. rooms.Select(r => r.SetVideoQualityAsync(quality)), .. Subscribers().Select(s => s.RequestBitrateAsync(bitsPerSecond))]);
    }

    /// <summary>Return the subscriber connections there are now.</summary>
    private AudioPeerConnection[] Subscribers()
    {
        // Done
        lock (gate) return [.. subscribed.Select(r => r.Subscriber).OfType<AudioPeerConnection>()];
    }

    /// <summary>Take the media of a room's subscriber connection.</summary>
    private void Listen(LiveKitRoom room, AudioPeerConnection subscriber)
    {
        // Each kind, decrypted by its sender's key, while the room is subscribed
        subscriber.AudioReceived += packet =>
        {
            if (IsSubscribed(room) && Open(room, packet.Ssrc, packet.Payload, FrameKind.Audio) is byte[] plain) AudioReceived?.Invoke(packet with { Payload = plain });
        };
        // Done
        subscriber.VideoReceived += frame =>
        {
            if (IsSubscribed(room) && Open(room, frame.Ssrc, frame.Data, FrameKind.Vp8) is byte[] plain) VideoReceived?.Invoke(frame with { Data = plain });
        };
    }

    /// <summary>Return whether <paramref name="room"/> is received on.</summary>
    private bool IsSubscribed(LiveKitRoom room) { lock (gate) return subscribed.Contains(room); }

    /// <summary>Return a received frame as its sender sent it: decrypted with the sender's key when encrypted; null when it does not decrypt.</summary>
    private byte[]? Open(LiveKitRoom room, uint ssrc, byte[] frame, FrameKind kind)
    {
        // Unencrypted calls as they are
        if (!Encrypted) return frame;
        if (room.IdentityOf(ssrc) is not string identity || !keys.TryGetValue(identity, out FrameCryptor? cryptor))
        {
            Interlocked.Increment(ref framesWithoutKey);
            return null;
        }
        cryptor.ServerTrailer = room.ServerTrailer;
        if (cryptor.Decrypt(frame, kind) is byte[] plain) return plain;
        // On a frame that did not decrypt
        DecryptionFailed?.Invoke(identity);
        return null;
    }
}
