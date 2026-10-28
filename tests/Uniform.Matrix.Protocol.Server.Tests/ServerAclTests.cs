namespace Uniform.Matrix.Protocol.Server.Tests;

using System.Text.Json.Nodes;

/// <summary>Tests of <see cref="ServerAcl"/>.</summary>
public class ServerAclTests
{
    /// <summary>Deny patterns win over allow patterns, globs match names without ports and case, and servers not allowed are denied.</summary>
    [Theory]
    [InlineData("good.example", true)]
    [InlineData("GOOD.example:8448", true)]
    [InlineData("chat.good.example", true)]
    [InlineData("evil.example", false)]
    [InlineData("sub.evil.example", false)]
    [InlineData("evil1.example", false)]
    [InlineData("other.org", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("[::1]:8448", false)]
    public void MatchServers(string serverName, bool allowed)
    {
        // Arrange
        ServerAcl acl = new(new JsonObject { ["allow"] = new JsonArray("*.example", "good.example"), ["deny"] = new JsonArray("evil.example", "*.evil.example", "evil?.example"), ["allow_ip_literals"] = false });

        // Act and assert
        Assert.Equal(allowed, acl.IsAllowed(serverName));
    }

    /// <summary>IP literals are allowed unless turned off, and an ACL without allow patterns denies everyone.</summary>
    [Fact]
    public void ApplyDefaults()
    {
        // Arrange
        ServerAcl open = new(new JsonObject { ["allow"] = new JsonArray("*") });
        ServerAcl closed = new(new JsonObject());

        // Act and assert
        Assert.True(open.IsAllowed("1.2.3.4"));
        Assert.True(open.IsAllowed("any.example"));
        Assert.False(closed.IsAllowed("any.example"));
    }
}
