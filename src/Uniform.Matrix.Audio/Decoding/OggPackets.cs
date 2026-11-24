namespace Uniform.Matrix.Audio.Decoding;

using System.Buffers.Binary;

/// <summary>A packet of an Ogg stream.</summary>
/// <param name="Data">The packet's bytes.</param>
/// <param name="Granule">The granule position of the page the packet ends on, when it is the last packet that ends there; -1 otherwise.</param>
internal readonly record struct OggPacket(byte[] Data, long Granule);

/// <summary>Reads the packets of an <see href="https://www.rfc-editor.org/rfc/rfc3533">Ogg</see> file's first logical stream, joining packets that span pages.</summary>
internal static class OggPackets
{
    /// <summary>Length of a page header before its segment table.</summary>
    private const int HeaderLength = 27;

    /// <summary>Return the packets of the first logical stream of <paramref name="data"/>, in order; a broken page ends them.</summary>
    public static IEnumerable<OggPacket> Read(byte[] data)
    {
        // Pages of the first stream's serial number
        int position = 0;
        uint? serial = null;
        List<byte> pending = [];
        while (position + HeaderLength <= data.Length && data.AsSpan(position, 4).SequenceEqual("OggS"u8))
        {
            long granule = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 6));
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 14));
            int segments = data[position + 26];
            int body = position + HeaderLength + segments;
            if (body > data.Length) yield break;
            int length = 0;
            for (int i = 0; i < segments; i++) length += data[position + HeaderLength + i];
            if (body + length > data.Length) yield break;
            serial ??= pageSerial;
            if (pageSerial != serial)
            {
                position = body + length;
                continue;
            }

            // Packets end at a segment shorter than 255; the page's granule goes to the last one ending here
            int lastEnd = -1;
            for (int i = 0; i < segments; i++) if (data[position + HeaderLength + i] < 255) lastEnd = i;
            int offset = body;
            for (int i = 0; i < segments; i++)
            {
                int size = data[position + HeaderLength + i];
                pending.AddRange(data.AsSpan(offset, size));
                offset += size;
                if (size == 255) continue;
                yield return new OggPacket([.. pending], i == lastEnd ? granule : -1);
                pending.Clear();
            }
            position = body + length;
        }
    }
}
