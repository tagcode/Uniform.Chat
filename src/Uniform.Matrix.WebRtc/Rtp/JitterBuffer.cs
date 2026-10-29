namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Puts received RTP packets back in order and evens out their arrival, for a player that takes one frame every packet interval: it holds a few packets before the first one plays, and then gives the next in sequence, or null for a lost one so that the decoder conceals it.</summary>
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

    /// <summary>Packets held.</summary>
    public int Count { get { lock (gate) return packets.Count; } }

    /// <summary>Packets that were not there when their turn came.</summary>
    public long Lost { get; private set; }

    /// <summary>Add a received packet; late ones, whose turn has passed, and duplicates are dropped.</summary>
    public void Add(RtpPacket packet)
    {
        // The sequence extended across wraps, relative to the highest seen
        lock (gate)
        {
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

            // The next in sequence, or a gap
            isPlaying = true;
            if (packets.Remove(next, out RtpPacket? packet))
            {
                next++;
                missing = 0;
                return packet;
            }
            next++;
            Lost++;

            // A long gap with nothing held: the stream stopped, so fill again
            if (++missing > capacityPackets / 2 && packets.Count == 0)
            {
                playing = false;
                isPlaying = false;
                missing = 0;
            }
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
