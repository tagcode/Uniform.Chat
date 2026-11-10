namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Puts VP8 frames together from RTP packets, <see href="https://www.rfc-editor.org/rfc/rfc7741">RFC 7741</see>: the packets of a frame share a timestamp, the first starts partition 0, and the last has the marker bit. Packets may come out of order; frames are given in order.</summary>
/// <remarks>
/// A frame waits while packets before it may still come, until the newest packet is a reorder window past the gap: then they count as lost. While a keyframe is needed, a complete one does not wait, as it decodes on its own.
/// A frame after lost packets cannot be decoded, as it predicts from the frames before; from then on only a keyframe is given, and <see cref="NeedsKeyframe"/> tells the caller to ask the sender for one, e.g. with an RTCP PLI.
/// </remarks>
/// <param name="reorderWindow">How many packets past a gap may arrive before the missing ones count as lost.</param>
public sealed class Vp8Depacketizer(int reorderWindow = 64)
{
    /// <summary>How many frames may wait at most; the oldest are given up.</summary>
    private const int MaxPendingFrames = 64;
    private readonly Dictionary<uint, List<Part>> pending = [];
    private ushort? lastSequence;
    private ushort newestSequence;
    private bool started;

    /// <summary>A packet's VP8 data and where it belongs.</summary>
    private sealed record Part(ushort Sequence, bool Start, bool Marker, byte[] Data);

    /// <summary>A frame whose packets are all here.</summary>
    private sealed record Ready(uint Timestamp, ushort First, ushort End, byte[] Data);

    /// <summary>Whether the frames given so far do not let the next inter frame decode, as at the start or after a loss: only a keyframe will be given.</summary>
    public bool NeedsKeyframe { get; private set; } = true;

    /// <summary>Take a packet; return the frames that are now ready in order, usually none or one.</summary>
    public IReadOnlyList<Vp8EncodedFrame> Push(RtpPacket packet)
    {
        // Assert a descriptor and data; a packet of a frame already given is late, a repeated one ignored
        if (Vp8Descriptor.Parse(packet.Payload) is not Vp8Descriptor descriptor) return [];
        if (lastSequence is ushort last && (short)(ushort)(packet.Sequence - last) <= 0) return [];
        if (!pending.TryGetValue(packet.Timestamp, out List<Part>? parts)) pending[packet.Timestamp] = parts = [];
        if (parts.Any(p => p.Sequence == packet.Sequence)) return [];
        parts.Add(new Part(packet.Sequence, descriptor.Start && descriptor.PartitionIndex == 0, packet.Marker, packet.Payload[descriptor.Length..]));
        if (!started || (short)(ushort)(packet.Sequence - newestSequence) > 0) newestSequence = packet.Sequence;
        started = true;

        // Ready frames in order; too many waiting are lost
        List<Vp8EncodedFrame> frames = [];
        while (Next() is Ready ready) Give(ready, frames);
        if (pending.Count <= MaxPendingFrames) return frames;
        foreach (uint stale in pending.Keys.OrderBy(t => (int)(t - packet.Timestamp)).Take(pending.Count - MaxPendingFrames).ToList()) pending.Remove(stale);
        NeedsKeyframe = true;
        // Return frames
        return frames;
    }

    /// <summary>Return the next frame to give: the first keyframe at the start; then the oldest complete frame, when it continues the last or the gap before it is past the reorder window; else, when one is needed, the oldest complete keyframe.</summary>
    private Ready? Next()
    {
        // The oldest complete frame, and the oldest complete keyframe
        Ready? oldest = null, keyframe = null;
        foreach ((uint timestamp, List<Part> parts) in pending)
        {
            if (Complete(timestamp, parts) is not Ready ready) continue;
            if (oldest is null || (int)(timestamp - oldest.Timestamp) < 0) oldest = ready;
            if ((ready.Data[0] & 1) == 0 && (keyframe is null || (int)(timestamp - keyframe.Timestamp) < 0)) keyframe = ready;
        }
        if (oldest is null) return null;

        // The stream starts at a keyframe, as nothing before one decodes
        if (lastSequence is not ushort last) return keyframe;

        // Continues the last, or the gap is lost
        if (oldest.First == (ushort)(last + 1)) return oldest;
        ushort from = (ushort)(last + 1);
        if ((ushort)(newestSequence - from) >= reorderWindow) return oldest;
        // Return a keyframe when one is needed, as it need not wait
        return NeedsKeyframe ? keyframe : null;
    }

    /// <summary>Give a ready frame, unless it cannot decode: after lost packets, only a keyframe.</summary>
    private void Give(Ready ready, List<Vp8EncodedFrame> frames)
    {
        // This frame and older ones are done; a jump in the sequence is a loss
        foreach (uint done in pending.Keys.Where(t => (int)(t - ready.Timestamp) <= 0).ToList()) pending.Remove(done);
        bool gap = lastSequence is ushort last && ready.First != (ushort)(last + 1);
        lastSequence = ready.End;
        bool keyframe = (ready.Data[0] & 1) == 0;
        if (gap) NeedsKeyframe = true;
        if (NeedsKeyframe && !keyframe) return;

        // Done
        NeedsKeyframe = false;
        frames.Add(new Vp8EncodedFrame(ready.Timestamp, keyframe, ready.Data));
    }

    /// <summary>Return the frame of <paramref name="timestamp"/> when its first packet, its last and all between are here, or null.</summary>
    private static Ready? Complete(uint timestamp, List<Part> parts)
    {
        // First and last packets, and every sequence number between
        if (parts.FirstOrDefault(p => p.Start) is not Part start || parts.FirstOrDefault(p => p.Marker) is not Part end) return null;
        int count = (ushort)(end.Sequence - start.Sequence) + 1;
        if (count > parts.Count) return null;
        List<Part> ordered = [.. parts.Where(p => (ushort)(p.Sequence - start.Sequence) < count).OrderBy(p => (ushort)(p.Sequence - start.Sequence))];
        if (ordered.Count != count) return null;

        // Their data in order
        byte[] data = new byte[ordered.Sum(p => p.Data.Length)];
        int at = 0;
        foreach (Part part in ordered)
        {
            part.Data.CopyTo(data, at);
            at += part.Data.Length;
        }
        // Return frame
        return new Ready(timestamp, start.Sequence, end.Sequence, data);
    }
}
