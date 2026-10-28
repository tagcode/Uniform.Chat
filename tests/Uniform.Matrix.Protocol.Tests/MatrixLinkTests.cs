namespace Uniform.Matrix.Protocol.Tests;

using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="MatrixLink"/>.</summary>
public class MatrixLinkTests
{
    /// <summary>matrix.to links, <c>matrix:</c> URIs and bare identifiers give the target, event and servers.</summary>
    [Theory]
    [InlineData("https://matrix.to/#/#room:example.org", "#room:example.org", null, "")]
    [InlineData("https://matrix.to/#/%23room%3Aexample.org", "#room:example.org", null, "")]
    [InlineData("https://matrix.to/#/!abc:example.org?via=example.org&via=other.org", "!abc:example.org", null, "example.org,other.org")]
    [InlineData("https://matrix.to/#/!abcdef?via=example.org", "!abcdef", null, "example.org")]
    [InlineData("https://matrix.to/#/!abc:example.org/$event?via=example.org", "!abc:example.org", "$event", "example.org")]
    [InlineData("https://matrix.to/#/@alice:example.org", "@alice:example.org", null, "")]
    [InlineData("matrix:roomid/abc:example.org?via=example.org&action=join", "!abc:example.org", null, "example.org")]
    [InlineData("matrix:r/room:example.org", "#room:example.org", null, "")]
    [InlineData("matrix:u/alice:example.org?action=chat", "@alice:example.org", null, "")]
    [InlineData("matrix:roomid/abc:example.org/e/event", "!abc:example.org", "$event", "")]
    [InlineData("#room:example.org", "#room:example.org", null, "")]
    [InlineData("!abcdef", "!abcdef", null, "")]
    public void ParseLinks(string text, string target, string? eventId, string via)
    {
        // Act
        bool parsed = MatrixLink.TryParse(text, out MatrixLink? link);

        // Assert
        Assert.True(parsed);
        Assert.Equal(target, link!.Target);
        Assert.Equal(eventId, link.EventId);
        Assert.Equal(via, string.Join(',', link.Via));
    }

    /// <summary>Other text and malformed links are not links.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("#room")]
    [InlineData("@alice")]
    [InlineData("https://example.org/#/#room:example.org")]
    [InlineData("https://matrix.to/#/hello")]
    [InlineData("https://matrix.to/#/@alice:example.org/$event")]
    [InlineData("matrix:x/abc")]
    [InlineData("matrix:u/alice:example.org/e/event")]
    public void RejectOthers(string? text)
    {
        // Act and assert
        Assert.False(MatrixLink.TryParse(text, out _));
    }

    /// <summary>Links are found in plain text and in HTML, once each, without trailing punctuation.</summary>
    [Fact]
    public void FindLinks()
    {
        // Arrange
        const string text = "Join https://matrix.to/#/#room:example.org. Or <a href=\"https://matrix.to/#/!abc:example.org?via=a.org&amp;via=b.org\">this</a>, see matrix:u/alice:example.org and https://matrix.to/#/#room:example.org again.";

        // Act
        IReadOnlyList<MatrixLink> links = MatrixLink.FindAll(text);

        // Assert
        Assert.Equal(["#room:example.org", "!abc:example.org", "@alice:example.org"], links.Select(l => l.Target));
        Assert.Equal(["a.org", "b.org"], links[1].Via);
    }

    /// <summary>A link formats as matrix.to and parses back the same.</summary>
    [Fact]
    public void RoundTrip()
    {
        // Arrange
        MatrixLink link = new() { Target = "!abc:example.org", EventId = "$event", Via = ["example.org"] };

        // Act
        string text = link.ToString();

        // Assert
        Assert.Equal("https://matrix.to/#/%21abc%3Aexample.org/%24event?via=example.org", text);
        Assert.True(MatrixLink.TryParse(text, out MatrixLink? parsed));
        Assert.Equal(link, parsed);
    }
}
