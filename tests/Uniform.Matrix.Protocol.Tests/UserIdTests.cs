namespace Uniform.Matrix.Protocol.Tests;

using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="UserId"/>.</summary>
public class UserIdTests
{
    /// <summary>Valid user IDs split into localpart and server name.</summary>
    [Theory]
    [InlineData("@alice:matrix.org", "alice", "matrix.org")]
    [InlineData("@bob:example.org:8448", "bob", "example.org:8448")]
    [InlineData("@a.b-c=d_e/f:[::1]", "a.b-c=d_e/f", "[::1]")]
    public void CanParseValidUserId(string text, string localpart, string serverName)
    {
        // Act
        UserId id = UserId.Parse(text);

        // Assert
        Assert.Equal(localpart, id.Localpart);
        Assert.Equal(serverName, id.ServerName);
        Assert.Equal(text, id.ToString());
    }

    /// <summary>Malformed user IDs are rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alice:matrix.org")]
    [InlineData("@alice")]
    [InlineData("@:matrix.org")]
    [InlineData("@alice:")]
    [InlineData("alice@matrix.org")]
    public void CanRejectInvalidUserId(string? text)
    {
        // Act and assert
        Assert.False(UserId.TryParse(text, out _));
    }
}
