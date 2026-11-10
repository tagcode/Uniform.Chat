namespace Uniform.Matrix.WebRtc.Rtp;

/// <summary>Splits VP8 frames into RTP payloads, <see href="https://www.rfc-editor.org/rfc/rfc7741">RFC 7741</see>: each starts with a payload descriptor carrying a 15-bit picture ID, which receivers use to notice lost frames; the first of a frame has the start bit.</summary>
public sealed class Vp8Packetizer
{
    /// <summary>Length of the descriptor written: the first byte, the extension byte and the 15-bit picture ID.</summary>
    public const int DescriptorLength = 4;
    private ushort pictureId;

    /// <summary>Start from a random picture ID, as senders should.</summary>
    public Vp8Packetizer() => pictureId = (ushort)Random.Shared.Next(0x8000);

    /// <summary>Return the payloads of <paramref name="frame"/>, each at most <paramref name="maxPayload"/> bytes with its descriptor; the last needs the RTP marker bit.</summary>
    public IReadOnlyList<byte[]> Packetize(ReadOnlySpan<byte> frame, int maxPayload = 1200)
    {
        // Assert room for data after the descriptor
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPayload, DescriptorLength);
        int chunk = maxPayload - DescriptorLength;
        List<byte[]> payloads = [];
        for (int at = 0; at < frame.Length; at += chunk)
        {
            // X and S on the first, PID 0; I with the picture ID in 15 bits
            int length = Math.Min(chunk, frame.Length - at);
            byte[] payload = new byte[DescriptorLength + length];
            payload[0] = (byte)(0x80 | (at == 0 ? 0x10 : 0));
            payload[1] = 0x80;
            payload[2] = (byte)(0x80 | pictureId >> 8);
            payload[3] = (byte)pictureId;
            frame.Slice(at, length).CopyTo(payload.AsSpan(DescriptorLength));
            payloads.Add(payload);
        }
        // Return them, the next frame with the next picture ID
        pictureId = (ushort)((pictureId + 1) & 0x7FFF);
        return payloads;
    }
}
