namespace Uniform.Matrix.Client.Formatting;

using System.Net;
using Markdig;

/// <summary>Turns typed Markdown into the <c>formatted_body</c> HTML of a message, like Element does.</summary>
public static class MarkdownComposer
{
    /// <summary>CommonMark with strikethrough and autolinks; raw HTML in the input is escaped, not passed on.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UseAutoLinks().UseSoftlineBreakAsHardlineBreak().DisableHtml().Build();

    /// <summary>Return HTML for <paramref name="text"/>, or null when it has no formatting and no mentions, so plain messages stay plain.</summary>
    /// <param name="text">Typed text, sent unchanged as <c>body</c>.</param>
    /// <param name="mentions">Display names to turn into mention pills, mapped to user IDs.</param>
    /// <example><code><![CDATA[string? html = MarkdownComposer.ToHtml("**hi** Bob", new Dictionary<string, string> { ["Bob"] = "@bob:matrix.org" });]]></code></example>
    public static string? ToHtml(string text, IReadOnlyDictionary<string, string>? mentions = null)
    {
        // Convert; a single paragraph is sent without its wrapper, like Element does
        string html = Markdown.ToHtml(text, Pipeline).Trim();
        if (html.StartsWith("<p>") && html.EndsWith("</p>") && !html[3..^4].Contains("<p>")) html = html[3..^4];
        // Without tags left it is plain text
        bool formatted = html.Contains('<');

        // Mention pills: the first occurrence of each name becomes a matrix.to link
        bool mentioned = false;
        foreach ((string name, string userId) in mentions ?? new Dictionary<string, string>())
        {
            string encoded = WebUtility.HtmlEncode(name);
            int index = html.IndexOf(encoded, StringComparison.Ordinal);
            if (index < 0) continue;
            html = $"{html[..index]}<a href=\"https://matrix.to/#/{WebUtility.HtmlEncode(userId)}\">{encoded}</a>{html[(index + encoded.Length)..]}";
            mentioned = true;
        }
        // Return HTML only when it adds something
        return formatted || mentioned ? html : null;
    }
}
