namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text;

/// <summary>Tests of <see cref="DnsSrv"/>.</summary>
public class DnsSrvTests
{
    /// <summary>A query names the service as labels and asks for SRV records with recursion.</summary>
    [Fact]
    public void BuildQuery()
    {
        // Act
        byte[] query = DnsSrv.BuildQuery(0x1234, "_matrix-fed._tcp.example.org");

        // Assert: header, labels, type 33, class 1
        Assert.Equal([0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0], query[..12]);
        Assert.Equal("\u000b_matrix-fed\u0004_tcp\u0007example\u0003org\0", Encoding.ASCII.GetString(query[12..^4]));
        Assert.Equal([0, 33, 0, 1], query[^4..]);
    }

    /// <summary>Answers give their records best first, following compressed names; unknown names give none, and answers to other queries are not used.</summary>
    [Fact]
    public void ParseAnswers()
    {
        // Arrange: two records, the second's target compressed against the question
        byte[] query = DnsSrv.BuildQuery(7, "_matrix-fed._tcp.example.org");
        List<byte> answer = [.. query];
        answer[2] = 0x81;
        answer[3] = 0x80;
        answer[7] = 2;
        answer.AddRange(Record(priority: 20, weight: 0, port: 443, [.. Labels("backup"), 0xC0, 29]));
        answer.AddRange(Record(priority: 10, weight: 5, port: 8448, [.. Labels("matrix"), .. Labels("example"), .. Labels("org"), 0]));
        byte[] unknown = [.. query];
        unknown[2] = 0x81;
        unknown[3] = 0x83;

        // Act
        IReadOnlyList<SrvRecord>? records = DnsSrv.ParseAnswer([.. answer], 7);
        IReadOnlyList<SrvRecord>? none = DnsSrv.ParseAnswer(unknown, 7);
        IReadOnlyList<SrvRecord>? other = DnsSrv.ParseAnswer([.. answer], 8);

        // Assert
        Assert.Equal([new SrvRecord(10, 5, 8448, "matrix.example.org"), new SrvRecord(20, 0, 443, "backup.example.org")], records);
        Assert.Empty(none!);
        Assert.Null(other);
    }

    /// <summary>Return a length-prefixed label.</summary>
    private static byte[] Labels(string label) => [(byte)label.Length, .. Encoding.ASCII.GetBytes(label)];

    /// <summary>Return an SRV answer record for the question's name, which starts at offset 12.</summary>
    private static byte[] Record(int priority, int weight, int port, byte[] target)
    {
        // Name pointer, type, class, TTL, length, then the data
        int length = 6 + target.Length;
        // Return record
        return [0xC0, 12, 0, 33, 0, 1, 0, 0, 0x0E, 0x10, (byte)(length >> 8), (byte)length, (byte)(priority >> 8), (byte)priority, (byte)(weight >> 8), (byte)weight, (byte)(port >> 8), (byte)port, .. target];
    }
}
