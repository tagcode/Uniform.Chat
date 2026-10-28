namespace Uniform.Matrix.Protocol.Server;

using System.Buffers.Binary;
using System.Text;

/// <summary>A DNS <see href="https://www.rfc-editor.org/rfc/rfc2782">SRV record</see>: where a service of a domain runs.</summary>
/// <param name="Priority">Lower is tried first.</param>
/// <param name="Weight">Among equal priorities, higher is preferred.</param>
/// <param name="Port">Port of the service.</param>
/// <param name="Target">Host of the service, without the trailing dot; <c>.</c> means the service is not offered.</param>
public sealed record SrvRecord(int Priority, int Weight, int Port, string Target);

/// <summary>DNS messages for SRV lookups, as <see href="https://www.rfc-editor.org/rfc/rfc1035">RFC 1035</see> lays them out: a query for one name, and the SRV records of its answer.</summary>
public static class DnsSrv
{
    private const ushort TypeSrv = 33;
    private const ushort ClassIn = 1;
    private const int HeaderLength = 12;

    /// <summary>Return a recursive query for the SRV records of <paramref name="name"/>, e.g. <c>_matrix-fed._tcp.example.org</c>.</summary>
    /// <param name="id">Query ID, which the answer repeats.</param>
    /// <param name="name">Domain name; labels of 1 to 63 bytes.</param>
    public static byte[] BuildQuery(ushort id, string name)
    {
        // Header: ID, recursion desired, one question
        List<byte> message = new(HeaderLength + name.Length + 6);
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
        message.AddRange(header);

        // The name as length-prefixed labels, then type and class
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException($"Invalid DNS name {name}.", nameof(name));
            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }
        message.AddRange([0, TypeSrv >> 8, TypeSrv & 0xFF, ClassIn >> 8, ClassIn & 0xFF]);
        // Return query
        return [.. message];
    }

    /// <summary>Return the SRV records of an answer to query <paramref name="id"/>, best first: by priority, then weight; empty when the name has none or does not exist, null when the message is not a usable answer.</summary>
    public static IReadOnlyList<SrvRecord>? ParseAnswer(ReadOnlySpan<byte> message, ushort id)
    {
        // Assert an answer to this query, without errors other than an unknown name
        if (message.Length < HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(message) != id) return null;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        int code = flags & 0x000F;
        if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0) return null;
        if (code == 3) return [];
        if (code != 0) return null;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        // Skip the questions, then read SRV answers
        int offset = HeaderLength;
        List<SrvRecord> records = [];
        try
        {
            for (int i = 0; i < questions; i++) offset = SkipName(message, offset) + 4;
            for (int i = 0; i < answers; i++)
            {
                offset = SkipName(message, offset);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
                int data = offset + 10;
                offset = data + length;
                if (type != TypeSrv || length < 7 || offset > message.Length) continue;
                records.Add(new SrvRecord(BinaryPrimitives.ReadUInt16BigEndian(message[data..]), BinaryPrimitives.ReadUInt16BigEndian(message[(data + 2)..]), BinaryPrimitives.ReadUInt16BigEndian(message[(data + 4)..]), ReadName(message, data + 6)));
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            // On error not usable
            return null;
        }
        // Return records best first
        return [.. records.OrderBy(r => r.Priority).ThenByDescending(r => r.Weight)];
    }

    /// <summary>Return the offset after the name at <paramref name="offset"/>, which may end in a compression pointer.</summary>
    private static int SkipName(ReadOnlySpan<byte> message, int offset)
    {
        // Labels until the root or a pointer
        while (true)
        {
            byte length = message[offset];
            if (length == 0) return offset + 1;
            if ((length & 0xC0) == 0xC0) return offset + 2;
            offset += length + 1;
        }
    }

    /// <summary>Return the name at <paramref name="offset"/>, following compression pointers, without the trailing dot; <c>.</c> for the root.</summary>
    private static string ReadName(ReadOnlySpan<byte> message, int offset)
    {
        // Labels, jumping at pointers, a bounded number of times against loops
        List<string> labels = [];
        for (int jumps = 0; jumps < 32;)
        {
            byte length = message[offset];
            if (length == 0) return labels.Count == 0 ? "." : string.Join('.', labels);
            if ((length & 0xC0) == 0xC0)
            {
                offset = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]) & 0x3FFF;
                jumps++;
                continue;
            }
            labels.Add(Encoding.ASCII.GetString(message.Slice(offset + 1, length)));
            offset += length + 1;
        }
        // On error a loop of pointers
        throw new FormatException("DNS name pointers loop.");
    }
}
