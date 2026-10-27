namespace Durum.Matrix.Client.Gui.Tests;

using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Durum.Matrix.Client.Gui.Views;

/// <summary>Tests of <see cref="RichText"/>: how message HTML becomes styled text.</summary>
public class RichTextTests
{
    /// <summary>Return the text of inlines, with line breaks as newlines.</summary>
    private static string TextOf(InlineCollection inlines) => string.Concat(inlines.Select(i => i switch { Run run => run.Text, LineBreak => "\n", _ => "" }));

    /// <summary>Paragraphs are separated by a blank line, list items get bullets, and emphasis becomes styled runs.</summary>
    [AvaloniaFact]
    public void FormatBlocks()
    {
        // Act
        InlineCollection inlines = RichText.Build("<p>Hi <strong>all</strong> and <em>you</em></p><ul><li>one</li><li>two</li></ul><pre><code>a  b\nc</code></pre>");

        // Assert
        Assert.Equal("Hi all and you\n\n• one\n• two\n\na  b\nc", TextOf(inlines));
        Assert.Equal(FontWeight.SemiBold, inlines.OfType<Run>().Single(r => r.Text == "all").FontWeight);
        Assert.Equal(FontStyle.Italic, inlines.OfType<Run>().Single(r => r.Text == "you").FontStyle);
        Assert.Contains("mono", inlines.OfType<Run>().Single(r => r.Text == "a  b").FontFamily.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Mentions show their name, other links show where they lead, and reply fallbacks are left out.</summary>
    [AvaloniaFact]
    public void FormatLinks()
    {
        // Act
        InlineCollection inlines = RichText.Build("<mx-reply><blockquote>old</blockquote></mx-reply><a href=\"https://matrix.to/#/@bob:hs\">Bob</a>: see <a href=\"https://example.org\">this</a>");

        // Assert
        Assert.Equal("Bob: see this (https://example.org)", TextOf(inlines));
        Assert.Equal(FontWeight.SemiBold, inlines.OfType<Run>().Single(r => r.Text == "Bob").FontWeight);
        Assert.Equal(TextDecorations.Underline, inlines.OfType<Run>().Single(r => r.Text == "this").TextDecorations);
    }

    /// <summary>Quotes are marked at the start of each line.</summary>
    [AvaloniaFact]
    public void FormatQuotes()
    {
        // Act
        InlineCollection inlines = RichText.Build("<blockquote>first<br>second</blockquote><p>after</p>");

        // Assert
        Assert.Equal("▎ first\n▎ second\n\nafter", TextOf(inlines));
    }

    /// <summary>Links are found by character, including mentions and the shown address; positions count line breaks.</summary>
    [AvaloniaFact]
    public void FindLinks()
    {
        // Act
        InlineCollection inlines = RichText.Build("<p>hi</p><a href=\"https://matrix.to/#/@bob:hs\">Bob</a>: <a href=\"https://example.org\">this</a>", out IReadOnlyList<LinkSpan> links);
        string text = TextOf(inlines);

        // Assert: "hi\n\nBob: this (https://example.org)"
        Assert.Equal(2, links.Count);
        Assert.Equal("Bob", text.Substring(links[0].Start, links[0].Length));
        Assert.Equal("https://matrix.to/#/@bob:hs", links[0].Href);
        Assert.Equal("this (https://example.org)", text.Substring(links[1].Start, links[1].Length));
    }

    /// <summary>Web addresses in plain text become links without trailing punctuation, and a click on one raises the event.</summary>
    [AvaloniaFact]
    public void ClickPlainLink()
    {
        // Arrange: plain text in a window
        RichText rich = new() { Body = "see https://example.org/a?b=1. ok" };
        Avalonia.Controls.Window window = new() { Content = rich, Width = 600, Height = 100 };
        window.Show();
        string? clicked = null;
        rich.LinkClicked += (_, e) => clicked = e.Href;

        // Act: press and release on the address
        Avalonia.Point point = rich.TextLayout.HitTestTextPosition(8).Center;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);

        // Assert
        Assert.Equal("https://example.org/a?b=1", rich.LinkAt(8));
        Assert.Null(rich.LinkAt(1));
        Assert.Equal("https://example.org/a?b=1", clicked);
        window.Close();
    }
}
