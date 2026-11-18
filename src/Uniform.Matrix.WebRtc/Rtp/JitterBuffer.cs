namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Puts received RTP packets back in order and evens out their arrival, for a player that takes one frame every packet interval: it holds a few packets before the first one plays, and then gives the next in sequence, or null so that the decoder conceals a frame.</summary>
/// <remarks>A packet that has not arrived when its turn comes is waited for, as it is usually late rather than lost; only when later packets pile up behind it is it skipped as lost. Waiting adds delay, which is drained again: while more than <see cref="Slack"/> packets above the target are held, a near-silent packet is skipped now and then, or any packet once <see cref="FarAhead"/> are, so the delay goes back to the target after the network hiccuped; <c>capacityPackets</c> bounds it meanwhile. A packet of another stream, by its SSRC, starts over, as when the sender reconnects through an SFU and its sequence numbers begin anew.</remarks>
/// <param name="delayPackets">How many packets to hold before playing; three of 20 ms are 60 ms of delay.</param>
/// <param name="capacityPackets">How many packets to hold at most; when more come, e.g. after a stall, the oldest are dropped to catch up.</param>
public sealed class JitterBuffer(int delayPackets = 3, int capacityPackets = 50)
{
    private readonly SortedDictionary<long, RtpPacket> packets = [];
    private readonly Lock gate = new();
    private long next = -1;
    private long highest = -1;
    private bool playing;
    private int missing;
    private uint? ssrc;
    private int sinceSkip;

    /// <summary>Packets held above the target before the delay is drained.</summary>
    public const int Slack = 2;

    /// <summary>Packets held above the target from which any packet is skipped, not only a near-silent one.</summary>
    public const int FarAhead = 10;

    /// <summary>Turns between two skips at most, so a drain is not heard: one frame in five, draining up to 200 ms a second.</summary>
    public const int SkipEvery = 5;

    /// <summary>Largest payload taken for silence, as Opus codes silent frames in a few bytes.</summary>
    public const int SilentBytes = 24;

    /// <summary>Packets held.</summary>
    public int Count { get { lock (gate) return packets.Count; } }

    /// <summary>Packets skipped as lost: missing while later ones were held.</summary>
    public long Lost { get; private set; }

    /// <summary>Turns when the next packet had not arrived yet, and a frame was concealed while waiting for it.</summary>
    public long Underruns { get; private set; }

    /// <summary>Packets skipped to drain delay.</summary>
    public long Drained { get; private set; }

    /// <summary>Add a received packet; late ones, whose turn has passed, and duplicates are dropped.</summary>
    public void Add(RtpPacket packet)
    {
        // The sequence extended across wraps, relative to the highest seen
        lock (gate)
        {
            // Another stream: its numbers have nothing to do with the last one's
            if (ssrc is uint current && current != packet.Ssrc)
            {
                packets.Clear();
                next = -1;
                highest = -1;
                playing = false;
                missing = 0;
            }
            ssrc = packet.Ssrc;
            long index = highest < 0 ? packet.Sequence : Extend(packet.Sequence);
            if (next >= 0 && index < next) return;
            packets.TryAdd(index, packet);
            if (index > highest) highest = index;

            // Too many: skip ahead to catch up
            while (packets.Count > capacityPackets)
            {
                packets.Remove(packets.Keys.First());
                next = packets.Keys.First();
            }
        }
    }

    /// <summary>Return the packet whose turn it is; null for a lost packet, to conceal, and also while filling, when <paramref name="isPlaying"/> tells the two apart.</summary>
    /// <param name="isPlaying">Whether playing: false while filling, or after the stream stopped, when nothing should play.</param>
    public RtpPacket? Next(out bool isPlaying)
    {
        // Fill up before playing
        lock (gate)
        {
            if (!playing)
            {
                isPlaying = false;
                if (packets.Count < delayPackets) return null;
                playing = true;
                next = packets.Keys.First();
            }

            // Ahead of the target, as after late packets: a near-silent frame skipped now and then, or any once far ahead
            isPlaying = true;
            if (++sinceSkip >= SkipEvery && packets.Count > delayPackets + Slack && packets.TryGetValue(next, out RtpPacket? ahead) && (ahead.Payload.Length <= SilentBytes || packets.Count > delayPackets + FarAhead))
            {
                packets.Remove(next);
                next++;
                Drained++;
                sinceSkip = 0;
            }

            // The next in sequence
            if (packets.Remove(next, out RtpPacket? packet))
            {
                next++;
                missing = 0;
                return packet;
            }

            // Nothing held: late, not lost, so conceal and keep waiting for it; after a long silence, fill again
            if (packets.Count == 0)
            {
                Underruns++;
                if (++missing <= capacityPackets / 2) return null;
                playing = false;
                isPlaying = false;
                missing = 0;
                return null;
            }

            // A gap before held packets: lost once enough later ones are here, else it may still come
            if (packets.Count < delayPackets)
            {
                Underruns++;
                return null;
            }
            next++;
            Lost++;
            missing = 0;
            // On loss
            return null;
        }
    }

    /// <summary>Return a 16-bit sequence number as a 64-bit index, nearest the highest seen.</summary>
    private long Extend(ushort sequence)
    {
        // The candidate in the highest's cycle, or the neighbouring one
        long cycle = highest & ~0xFFFFL;
        long index = cycle | sequence;
        if (index - highest > 0x8000) index -= 0x10000;
        else if (highest - index > 0x8000) index += 0x10000;
        // Return index
        return index;
    }
}
