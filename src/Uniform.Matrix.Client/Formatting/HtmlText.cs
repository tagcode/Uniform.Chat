namespace Uniform.Matrix.Client.Formatting;

using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

/// <summary>Renders the HTML <c>formatted_body</c> of a message as terminal text: paragraphs, lists, quotes, code, links and mention names.</summary>
/// <remarks>Emphasis becomes <c>*bold*</c>, <c>_italic_</c> and <c>~strike~</c>, as the log has one color per line. Reply fallbacks (<c>mx-reply</c>) are dropped and spoilers hidden.</remarks>
public static partial class HtmlText
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Convert <paramref name="html"/> to text.</summary>
    /// <param name="html">Formatted body.</param>
    /// <param name="userName">Returns the display name for a mentioned user ID; mention pills show their link text when null.</param>
    /// <example><code><![CDATA[HtmlText.ToText("<p>Hi <strong>all</strong></p><ul><li>one</li></ul>"); // "Hi *all*\n• one"]]></code></example>
    public static string ToText(string html, Func<string, string?>? userName = null)
    {
        // Parse as a fragment of a body
        IDocument document = Parser.ParseDocument($"<!DOCTYPE html><html><body>{html}</body></html>");
        string text = RenderChildren(document.Body!, new Context(userName, Preformatted: false, ListDepth: 0));

        // Tidy blank lines and trailing spaces
        text = string.Join('\n', text.Split('\n').Select(l => l.TrimEnd()));
        // Return text
        return BlankLines().Replace(text, "\n\n").Trim('\n');
    }

    /// <summary>Rendering state passed down the tree.</summary>
    /// <param name="UserName">Mention name lookup.</param>
    /// <param name="Preformatted">Whether whitespace is kept, inside <c>pre</c>.</param>
    /// <param name="ListDepth">Nesting of lists, for indentation.</param>
    private sealed record Context(Func<string, string?>? UserName, bool Preformatted, int ListDepth);

    /// <summary>Render all child nodes.</summary>
    private static string RenderChildren(INode node, Context context) => string.Concat(node.ChildNodes.Select(n => Render(n, context)));

    /// <summary>Render one node and its children.</summary>
    private static string Render(INode node, Context context)
    {
        // Text keeps whitespace only when preformatted
        if (node is IText text) return context.Preformatted ? text.Data : Whitespace().Replace(text.Data, " ");
        if (node is not IElement element) return "";

        // Elements by tag
        string inner() => RenderChildren(element, context);
        // Return rendered element
        return element.LocalName switch
        {
            "br" => "\n",
            "p" or "div" => Block(inner().Trim()),
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => Block($"{new string('#', element.LocalName[1] - '0')} {inner().Trim()}"),
            "blockquote" => Block(Prefix(inner().Trim('\n', ' '), "> ")),
            "ul" or "ol" => Block(RenderList(element, context)),
            "pre" => Block(RenderChildren(element, context with { Preformatted = true }).TrimEnd('\n')),
            "code" => context.Preformatted ? inner() : $"`{inner()}`",
            "strong" or "b" => $"*{inner()}*",
            "em" or "i" => $"_{inner()}_",
            "del" or "s" or "strike" => $"~{inner()}~",
            "a" => RenderLink(element, inner(), context),
            "img" => $"[image: {element.GetAttribute("alt") ?? element.GetAttribute("title") ?? "image"}]",
            "hr" => Block("────────"),
            "mx-reply" => "",
            "span" when element.HasAttribute("data-mx-spoiler") => element.GetAttribute("data-mx-spoiler") is { Length: > 0 } reason ? $"[spoiler: {reason}]" : "[spoiler]",
            "table" => Block(RenderTable(element, context)),
            _ => inner(),
        };
    }

    /// <summary>Render list items with bullets or numbers, indenting nested lists and continuation lines.</summary>
    private static string RenderList(IElement list, Context context)
    {
        // Numbered lists may start elsewhere than 1
        bool ordered = list.LocalName == "ol";
        int number = int.TryParse(list.GetAttribute("start"), out int start) ? start : 1;
        string indent = new(' ', context.ListDepth * 2);
        Context nested = context with { ListDepth = context.ListDepth + 1 };

        // One block per item
        List<string> items = [];
        foreach (IElement item in list.Children.Where(c => c.LocalName == "li"))
        {
            string marker = ordered ? $"{number++}. " : "• ";
            string body = RenderChildren(item, nested).Trim('\n', ' ');
            string[] lines = BlankLines().Replace(body, "\n").Split('\n');
            items.Add(indent + marker + lines[0] + string.Concat(lines.Skip(1).Select(l => "\n" + (l.StartsWith(' ') || l.StartsWith('•') ? l : indent + new string(' ', marker.Length) + l))));
        }
        // Return items
        return string.Join('\n', items);
    }

    /// <summary>Render a link: mentions as names, bare URLs as is, others as <c>text (url)</c>.</summary>
    private static string RenderLink(IElement link, string text, Context context)
    {
        // Mention pills point at matrix.to
        string href = link.GetAttribute("href") ?? "";
        const string MatrixTo = "https://matrix.to/#/";
        if (href.StartsWith(MatrixTo, StringComparison.OrdinalIgnoreCase))
        {
            string target = Uri.UnescapeDataString(href[MatrixTo.Length..].Split('?')[0]);
            if (target.StartsWith('@')) return context.UserName?.Invoke(target) ?? text;
            return text.Length > 0 ? text : target;
        }
        // Return link text with its address when they differ
        return href.Length == 0 || text == href || text == href.Replace("mailto:", "") ? (text.Length > 0 ? text : href) : $"{text} ({href})";
    }

    /// <summary>Render table rows as cells separated by <c>|</c>.</summary>
    private static string RenderTable(IElement table, Context context) => string.Join('\n', table.QuerySelectorAll("tr").Select(row =>
        string.Join(" | ", row.Children.Where(c => c.LocalName is "td" or "th").Select(c => RenderChildren(c, context).Trim()))));

    /// <summary>Surround <paramref name="text"/> with line breaks so it stands as a block.</summary>
    private static string Block(string text) => $"\n{text}\n";

    /// <summary>Prefix every line of <paramref name="text"/>.</summary>
    private static string Prefix(string text, string prefix)
    {
        // Prefix each line
        StringBuilder result = new();
        foreach (string line in text.Split('\n')) result.Append(prefix).Append(line).Append('\n');
        // Return without the last break
        return result.ToString().TrimEnd('\n');
    }

    /// <summary>Run of whitespace in normal text.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Three or more line breaks, i.e. more than one blank line.</summary>
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
