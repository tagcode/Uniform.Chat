namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>A VP8 frame put together from RTP packets: its RTP timestamp, whether it is a keyframe, and the frame as the decoder takes it.</summary>
/// <param name="Timestamp">RTP timestamp, in the 90 kHz video clock.</param>
/// <param name="IsKeyframe">Whether the frame decodes on its own.</param>
/// <param name="Data">The VP8 frame.</param>
/// <param name="Rotation">Clockwise rotation in degrees that shows the picture upright, 0, 90, 180 or 270, from the sender's video orientation.</param>
public sealed record Vp8EncodedFrame(uint Timestamp, bool IsKeyframe, byte[] Data, int Rotation = 0);

/// <summary>The VP8 payload descriptor of <see href="https://www.rfc-editor.org/rfc/rfc7741#section-4.2">RFC 7741 4.2</see> at the start of each RTP packet's payload.</summary>
/// <param name="Start">Whether the packet starts a partition (S).</param>
/// <param name="PartitionIndex">Index of the partition the packet starts or continues (PID); 0 is the frame's first.</param>
/// <param name="Length">Length of the descriptor, after which the VP8 data starts.</param>
internal readonly record struct Vp8Descriptor(bool Start, int PartitionIndex, int Length)
{
    /// <summary>Parse the descriptor at the start of <paramref name="payload"/>; null when it is cut short.</summary>
    public static Vp8Descriptor? Parse(ReadOnlySpan<byte> payload)
    {
        // First byte: X, R, N, S, R, PID
        if (payload.Length < 1) return null;
        bool extended = (payload[0] & 0x80) != 0;
        bool start = (payload[0] & 0x10) != 0;
        int partition = payload[0] & 0x07;
        int length = 1;
        if (extended)
        {
            // Extension byte: I, L, T, K; then a 7- or 15-bit picture ID, TL0PICIDX, and TID/Y/KEYIDX
            if (payload.Length < 2) return null;
            byte flags = payload[1];
            length = 2;
            if ((flags & 0x80) != 0)
            {
                if (payload.Length < length + 1) return null;
                length += (payload[length] & 0x80) != 0 ? 2 : 1;
            }
            if ((flags & 0x40) != 0) length++;
            if ((flags & 0x30) != 0) length++;
        }
        // Return descriptor, when the VP8 data follows
        return payload.Length > length ? new Vp8Descriptor(start, partition, length) : null;
    }
}
