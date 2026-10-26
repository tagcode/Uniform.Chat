namespace Uniform.Matrix.Client.Tests;

using System.Text.Json;
using Uniform.Matrix.Client.Formatting;
using Uniform.Matrix.Client.State;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Unit tests for <see cref="MarkdownComposer"/>, <see cref="HtmlText"/> and formatted messages in the log.</summary>
public class RichTextTests
{
    private const string Me = "@me:hs.example";
    private const string Bob = "@bob:hs.example";

    /// <summary>Plain text stays plain, so messages without Markdown carry no HTML.</summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("1 < 2 & 3 > 2")]
    [InlineData("äö € 👍")]
    public void CanKeepPlainTextPlain(string text)
    {
        // Act and assert
        Assert.Null(MarkdownComposer.ToHtml(text));
    }

    /// <summary>Markdown becomes the HTML subset Matrix clients render.</summary>
    [Theory]
    [InlineData("**bold**", "<strong>bold</strong>")]
    [InlineData("_it_", "<em>it</em>")]
    [InlineData("~~gone~~", "<del>gone</del>")]
    [InlineData("`x < y`", "<code>x &lt; y</code>")]
    [InlineData("[site](https://example.org)", "<a href=\"https://example.org\">site</a>")]
    [InlineData("see https://example.org", "see <a href=\"https://example.org\">https://example.org</a>")]
    public void CanConvertMarkdown(string text, string expected)
    {
        // Act and assert
        Assert.Equal(expected, MarkdownComposer.ToHtml(text));
    }

    /// <summary>Raw HTML typed by the user is escaped, never passed on as markup.</summary>
    [Fact]
    public void CanEscapeRawHtml()
    {
        // Act
        string? html = MarkdownComposer.ToHtml("<script>alert(1)</script> **x**");

        // Assert
        Assert.NotNull(html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    /// <summary>Completed names become matrix.to mention pills, also in otherwise plain text.</summary>
    [Fact]
    public void CanAddMentionPills()
    {
        // Act
        string? html = MarkdownComposer.ToHtml("Bob: hi", new Dictionary<string, string> { ["Bob"] = Bob });

        // Assert
        Assert.Equal("<a href=\"https://matrix.to/#/@bob:hs.example\">Bob</a>: hi", html);
    }

    /// <summary>HTML renders as readable terminal text.</summary>
    [Theory]
    [InlineData("<p>Hi <strong>all</strong> and <em>you</em></p>", "Hi *all* and _you_")]
    [InlineData("line one<br>line two", "line one\nline two")]
    [InlineData("<ul><li>one</li><li>two</li></ul>", "• one\n• two")]
    [InlineData("<ol start=\"3\"><li>c</li><li>d</li></ol>", "3. c\n4. d")]
    [InlineData("<blockquote><p>quoted</p></blockquote><p>reply</p>", "> quoted\n\nreply")]
    [InlineData("<pre><code>a  =  1\nb = 2\n</code></pre>", "a  =  1\nb = 2")]
    [InlineData("use <code>dotnet test</code>", "use `dotnet test`")]
    [InlineData("<a href=\"https://example.org\">site</a>", "site (https://example.org)")]
    [InlineData("<a href=\"https://example.org\">https://example.org</a>", "https://example.org")]
    [InlineData("<mx-reply><blockquote>old</blockquote></mx-reply>new", "new")]
    [InlineData("<span data-mx-spoiler>secret</span>", "[spoiler]")]
    [InlineData("<h2>Title</h2><p>text</p>", "## Title\n\ntext")]
    [InlineData("<img src=\"mxc://hs/x\" alt=\":cat:\">", "[image: :cat:]")]
    [InlineData("a &amp; b &lt;c&gt;", "a & b <c>")]
    public void CanRenderHtml(string html, string expected)
    {
        // Act and assert
        Assert.Equal(expected, HtmlText.ToText(html));
    }

    /// <summary>Mention pills show the user's display name from the lookup.</summary>
    [Fact]
    public void CanRenderMentionNames()
    {
        // Act
        string text = HtmlText.ToText("<a href=\"https://matrix.to/#/%40bob%3Ahs.example\">bob</a>: hi", id => id == Bob ? "Robert" : null);

        // Assert
        Assert.Equal("Robert: hi", text);
    }

    /// <summary>Nested lists indent their items.</summary>
    [Fact]
    public void CanRenderNestedLists()
    {
        // Act
        string text = HtmlText.ToText("<ul><li>fruit<ul><li>apple</li></ul></li><li>bread</li></ul>");

        // Assert
        Assert.Equal("• fruit\n  • apple\n• bread", text);
    }
}
