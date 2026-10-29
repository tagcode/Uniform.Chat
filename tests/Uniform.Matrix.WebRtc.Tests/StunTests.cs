namespace Uniform.Matrix.WebRtc.Tests;

using System.Net;
using System.Text;
using Uniform.Matrix.WebRtc.Stun;

/// <summary>Tests of STUN messages against the <see href="https://www.rfc-editor.org/rfc/rfc5769">RFC 5769</see> test vectors, and of encoding.</summary>
public class StunTests
{
    /// <summary>RFC 5769 2.1: a request with ICE attributes, short-term credentials and a fingerprint.</summary>
    private const string SampleRequest = "000100582112a442b7e7a701bc34d686fa87dfae802200105354554e207465737420636c69656e74002400046e0001ff80290008932ff9b151263b36000600096576746a3a68367659202020000800149aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a280280004e57a3bcf";
    /// <summary>RFC 5769 2.2: a response with an IPv4 XOR-MAPPED-ADDRESS.</summary>
    private const string IPv4Response = "0101003c2112a442b7e7a701bc34d686fa87dfae8022000b7465737420766563746f7220002000080001a147e112a643000800142b91f599fd9e90c38c7489f92af9ba53f06be7d780280004c07d4c96";
    /// <summary>RFC 5769 2.3: a response with an IPv6 XOR-MAPPED-ADDRESS.</summary>
    private const string IPv6Response = "010100482112a442b7e7a701bc34d686fa87dfae8022000b7465737420766563746f7220002000140002a1470113a9faa5d3f179bc25f4b5bed2b9d900080014a382954e4be67bf11784c97c8292c275bfe3ed4180280004c8fb0b4c";
    /// <summary>RFC 5769 2.4: a request with long-term credentials.</summary>
    private const string LongTermRequest = "000100602112a44278ad3433c6ad72c029da412e00060012e3839ee38388e383aae38383e382afe382b900000015001c662f2f3439396b39353464364f4c33346f4c394653547679363473410014000b6578616d706c652e6f72670000080014f67024656dd64a3e02b8e0712e85c9a28ca89666";
    /// <summary>The short-term password of the RFC's samples.</summary>
    private const string Password = "VOkJxbRl1RmTxUk/WvJxBt";

    /// <summary>The sample request parses into its attributes, and its integrity and fingerprint check out; a wrong password does not.</summary>
    [Fact]
    public void ParseSampleRequest()
    {
        // Arrange
        byte[] datagram = Convert.FromHexString(SampleRequest);

        // Act
        StunMessage message = StunMessage.Parse(datagram)!;

        // Assert
        Assert.Equal(StunMethod.Binding, message.Method);
        Assert.Equal(StunClass.Request, message.Class);
        Assert.Equal("STUN test client", message.GetText(StunAttributeTypes.Software));
        Assert.Equal("evtj:h6vY", message.GetText(StunAttributeTypes.Username));
        Assert.Equal(Convert.FromHexString("6e0001ff"), message.Get(StunAttributeTypes.Priority));
        Assert.Equal(Convert.FromHexString("932ff9b151263b36"), message.Get(StunAttributeTypes.IceControlled));
        Assert.True(StunMessage.CheckIntegrity(datagram, Encoding.UTF8.GetBytes(Password)));
        Assert.False(StunMessage.CheckIntegrity(datagram, Encoding.UTF8.GetBytes("wrong")));
        Assert.True(StunMessage.CheckFingerprint(datagram));
    }

    /// <summary>The sample responses give their mapped addresses, IPv4 and IPv6, and check out.</summary>
    [Theory]
    [InlineData(IPv4Response, "192.0.2.1", 32853)]
    [InlineData(IPv6Response, "2001:db8:1234:5678:11:2233:4455:6677", 32853)]
    public void ParseResponses(string hex, string address, int port)
    {
        // Arrange
        byte[] datagram = Convert.FromHexString(hex);

        // Act
        StunMessage message = StunMessage.Parse(datagram)!;
        IPEndPoint? mapped = StunAddress.DecodeXor(message.Get(StunAttributeTypes.XorMappedAddress), message.TransactionId);

        // Assert
        Assert.Equal(StunClass.Success, message.Class);
        Assert.Equal("test vector", message.GetText(StunAttributeTypes.Software));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), mapped);
        Assert.True(StunMessage.CheckIntegrity(datagram, Encoding.UTF8.GetBytes(Password)));
        Assert.True(StunMessage.CheckFingerprint(datagram));
    }

    /// <summary>The long-term sample checks out with the key from user name, realm and password.</summary>
    [Fact]
    public void CheckLongTermCredentials()
    {
        // Arrange: the RFC's values after SASLprep
        byte[] datagram = Convert.FromHexString(LongTermRequest);
        byte[] key = StunMessage.LongTermKey("\u30DE\u30C8\u30EA\u30C3\u30AF\u30B9", "example.org", "TheMatrIX");

        // Act
        StunMessage message = StunMessage.Parse(datagram)!;

        // Assert
        Assert.Equal("example.org", message.GetText(StunAttributeTypes.Realm));
        Assert.Equal("f//499k954d6OL34oL9FSTvy64sA", message.GetText(StunAttributeTypes.Nonce));
        Assert.True(StunMessage.CheckIntegrity(datagram, key));
    }

    /// <summary>A message encoded with integrity and fingerprint parses back the same and checks out; an address survives its XOR.</summary>
    [Fact]
    public void RoundTrip()
    {
        // Arrange
        StunMessage request = new(StunMethod.Binding, StunClass.Success);
        IPEndPoint address = new(IPAddress.Parse("203.0.113.7"), 54321);
        request.Add(StunAttributeTypes.Username, "a:b").Add(StunAttributeTypes.XorMappedAddress, StunAddress.EncodeXor(address, request.TransactionId));
        byte[] key = Encoding.UTF8.GetBytes("secret");

        // Act
        byte[] datagram = request.Encode(key, fingerprint: true);
        StunMessage parsed = StunMessage.Parse(datagram)!;

        // Assert
        Assert.Equal(request.TransactionId, parsed.TransactionId);
        Assert.Equal((StunMethod.Binding, StunClass.Success), (parsed.Method, parsed.Class));
        Assert.Equal("a:b", parsed.GetText(StunAttributeTypes.Username));
        Assert.Equal(address, StunAddress.DecodeXor(parsed.Get(StunAttributeTypes.XorMappedAddress), parsed.TransactionId));
        Assert.True(StunMessage.CheckIntegrity(datagram, key));
        Assert.True(StunMessage.CheckFingerprint(datagram));
        Assert.True(StunMessage.IsStun(datagram));
        Assert.False(StunMessage.IsStun([0x80, 0x00, 0x00, 0x00]));
    }

    /// <summary>Every method and class survives the interleaved type field.</summary>
    [Fact]
    public void EncodeMessageTypes()
    {
        // Act and assert
        foreach (StunMethod method in Enum.GetValues<StunMethod>())
        {
            foreach (StunClass messageClass in Enum.GetValues<StunClass>())
            {
                StunMessage parsed = StunMessage.Parse(new StunMessage(method, messageClass).Encode())!;
                Assert.Equal((method, messageClass), (parsed.Method, parsed.Class));
            }
        }
        Assert.Equal(Convert.FromHexString("0101"), new StunMessage(StunMethod.Binding, StunClass.Success).Encode()[..2]);
    }
}
