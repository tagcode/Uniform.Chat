namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>Signing and checking <c>X-Matrix</c> requests, and parsing server names.</summary>
public class FederationAuthTests
{
    private const string Uri = "/_matrix/federation/v1/send/1?x=%40a";

    /// <summary>A signed request verifies at its destination, and not with another method, URI, body, destination or key.</summary>
    [Fact]
    public void SignAndVerify()
    {
        // Arrange
        (string secret, string publicKey) = Ed25519.GenerateKey();
        (_, string otherKey) = Ed25519.GenerateKey();
        JsonObject content = new() { ["pdus"] = new JsonArray() };

        // Act: through the header text, as it travels
        XMatrixAuthorization? header = FederationAuth.Parse(FederationAuth.Sign("PUT", Uri, "a.org", "b.org", content, "ed25519:k", secret).ToString());

        // Assert
        Assert.NotNull(header);
        Assert.Equal(("a.org", "b.org", "ed25519:k"), (header.Origin, header.Destination, header.KeyId));
        Assert.True(FederationAuth.Verify("PUT", Uri, header, "b.org", content, publicKey));
        Assert.False(FederationAuth.Verify("POST", Uri, header, "b.org", content, publicKey));
        Assert.False(FederationAuth.Verify("PUT", Uri + "1", header, "b.org", content, publicKey));
        Assert.False(FederationAuth.Verify("PUT", Uri, header, "b.org", new JsonObject(), publicKey));
        Assert.False(FederationAuth.Verify("PUT", Uri, header, "c.org", content, publicKey));
        Assert.False(FederationAuth.Verify("PUT", Uri, header, "b.org", content, otherKey));
    }

    /// <summary>Headers parse with any case, unquoted values, spaces and escapes; incomplete ones do not.</summary>
    [Fact]
    public void ParseHeaders()
    {
        // Act
        XMatrixAuthorization? unquoted = FederationAuth.Parse("X-Matrix Origin=a.org, Key=ed25519:k,SIG=abc");
        XMatrixAuthorization? quoted = FederationAuth.Parse("x-matrix origin=\"a.org\",destination=\"b,org\",key=\"ed25519:k\",sig=\"a\\\"b\",extra=1");

        // Assert
        Assert.Equal(new XMatrixAuthorization("a.org", null, "ed25519:k", "abc"), unquoted);
        Assert.Equal(new XMatrixAuthorization("a.org", "b,org", "ed25519:k", "a\"b"), quoted);
        Assert.Null(FederationAuth.Parse("Bearer abc"));
        Assert.Null(FederationAuth.Parse("X-Matrix origin=a.org,key=ed25519:k"));
    }

    /// <summary>Server names split into host and port; invalid ones are refused.</summary>
    [Theory]
    [InlineData("example.org", "example.org", null)]
    [InlineData("matrix.example.org:9976", "matrix.example.org", 9976)]
    [InlineData("1.2.3.4:8448", "1.2.3.4", 8448)]
    [InlineData("[::1]:8448", "[::1]", 8448)]
    [InlineData("[2001:db8::1]", "[2001:db8::1]", null)]
    public void ParseServerNames(string serverName, string host, int? port)
    {
        // Act
        bool valid = ServerNames.TryParse(serverName, out string? parsedHost, out int? parsedPort);

        // Assert
        Assert.True(valid);
        Assert.Equal((host, port), (parsedHost, parsedPort));
    }

    /// <summary>Names with bad characters, ports or brackets are not server names.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("exa mple.org")]
    [InlineData("example.org:")]
    [InlineData("example.org:0")]
    [InlineData("example.org:65536")]
    [InlineData("example.org:80x")]
    [InlineData("[::1")]
    [InlineData("[1.2.3.4]")]
    [InlineData("a/b")]
    public void RefuseServerNames(string serverName)
    {
        // Act and assert
        Assert.False(ServerNames.TryParse(serverName, out _, out _));
    }

    /// <summary>IP literals are told from DNS names.</summary>
    [Fact]
    public void TellIpLiterals()
    {
        // Assert
        Assert.True(ServerNames.IsIpLiteral("1.2.3.4"));
        Assert.True(ServerNames.IsIpLiteral("[::1]"));
        Assert.False(ServerNames.IsIpLiteral("example.org"));
        Assert.False(ServerNames.IsIpLiteral("1234"));
    }
}
