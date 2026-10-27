namespace Uniform.Matrix.Client.Avalonia.Core.Views;

using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;

/// <summary>Message text that shows the HTML <c>formatted_body</c> of Matrix messages as styled, selectable text: emphasis, code, quotes, lists, headings, links and mentions.</summary>
/// <remarks>Without <see cref="Html"/>, it shows <see cref="Body"/> as plain text with its web addresses as links. Reply fallbacks (<c>mx-reply</c>) are left out and spoilers hidden. Clicking a link raises <see cref="LinkClicked"/>.</remarks>
public sealed partial class RichText : SelectableTextBlock
{
    /// <summary>Raised when a link is clicked, with its address; a click that selects text is not a link click.</summary>
    public static readonly RoutedEvent<LinkClickedEventArgs> LinkClickedEvent = RoutedEvent.Register<RichText, LinkClickedEventArgs>(nameof(LinkClicked), RoutingStrategies.Bubble);
    /// <summary>Plain text, shown when there is no HTML.</summary>
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<RichText, string?>(nameof(Body));
    /// <summary>HTML <c>formatted_body</c>, or null.</summary>
    public static readonly StyledProperty<string?> HtmlProperty = AvaloniaProperty.Register<RichText, string?>(nameof(Html));
    private static readonly HtmlParser Parser = new();
    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, monospace");
    private static readonly IBrush LinkBrush = new ImmutableSolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush CodeBackground = new ImmutableSolidColorBrush(Color.FromArgb(40, 128, 128, 128));
    private static readonly IBrush QuoteBrush = new ImmutableSolidColorBrush(Color.FromArgb(160, 128, 128, 128));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private IReadOnlyList<LinkSpan> links = [];

    /// <summary>Raised when a link is clicked.</summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked
    {
        add => AddHandler(LinkClickedEvent, value);
        remove => RemoveHandler(LinkClickedEvent, value);
    }

    /// <summary>Plain text, shown when there is no HTML.</summary>
    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>HTML <c>formatted_body</c>, or null for plain text.</summary>
    public string? Html
    {
        get => GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    /// <summary>Use the style of selectable text blocks.</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>Rebuild the text when the body or HTML changes.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        // Only our two inputs
        base.OnPropertyChanged(change);
        if (change.Property != BodyProperty && change.Property != HtmlProperty) return;

        // Formatted when there is HTML; plain text gets links only when it has addresses
        Text = null;
        Builder builder = new();
        if (!string.IsNullOrWhiteSpace(Html)) builder.AddChildren(Parser.ParseDocument($"<!DOCTYPE html><html><body>{Html}</body></html>").Body!, new Style());
        else if (Body is string body && WebAddress().IsMatch(body)) builder.AddPlain(body);
        else
        {
            Inlines = null;
            links = [];
            Text = Body;
            return;
        }
        Inlines = builder.Finish();
        links = builder.Links;
    }

    /// <summary>Return the inlines of <paramref name="html"/>.</summary>
    internal static InlineCollection Build(string html) => Build(html, out _);

    /// <summary>Return the inlines of <paramref name="html"/> and where its links are in the text.</summary>
    internal static InlineCollection Build(string html, out IReadOnlyList<LinkSpan> links)
    {
        // Parse as a fragment of a body
        IDocument document = Parser.ParseDocument($"<!DOCTYPE html><html><body>{html}</body></html>");
        Builder builder = new();
        builder.AddChildren(document.Body!, new Style());
        InlineCollection inlines = builder.Finish();
        links = builder.Links;
        // Return inlines without trailing breaks
        return inlines;
    }

    /// <summary>Return the link at character <paramref name="index"/>, or null.</summary>
    internal string? LinkAt(int index) => links.FirstOrDefault(link => index >= link.Start && index < link.Start + link.Length)?.Href;

    /// <summary>Show a hand over links.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        // Only text with links
        base.OnPointerMoved(e);
        if (links.Count == 0) return;
        Cursor = LinkUnder(e.GetPosition(this)) is not null ? HandCursor : Cursor.Default;
    }

    /// <summary>Raise <see cref="LinkClicked"/> for a plain click on a link.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // A left click or tap that did not select text
        base.OnPointerReleased(e);
        if (links.Count == 0 || e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
        if (LinkUnder(e.GetPosition(this)) is not string href) return;
        e.Handled = true;
        // Done
        RaiseEvent(new LinkClickedEventArgs(LinkClickedEvent, href));
    }

    /// <summary>Return the link under <paramref name="point"/>, or null.</summary>
    private string? LinkUnder(Point point)
    {
        // The character under the pointer, when the pointer is on it and not past the line's end
        Point inText = point - new Point(Padding.Left, Padding.Top);
        int index = TextLayout.HitTestPoint(inText).CharacterHit.FirstCharacterIndex;
        if (!TextLayout.HitTestTextRange(index, 1).Any(rect => rect.Inflate(2).Contains(inText))) return null;
        // Return its link
        return LinkAt(index);
    }

    /// <summary>Match web addresses in plain text.</summary>
    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""]+[^\s<>"".,;:!?)\]}'']", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    /// <summary>Text style passed down the tree.</summary>
    private sealed record Style
    {
        /// <summary>Bold, from <c>strong</c>, <c>b</c> and headings.</summary>
        public bool Bold { get; init; }
        /// <summary>Italic, from <c>em</c> and <c>i</c>.</summary>
        public bool Italic { get; init; }
        /// <summary>Struck through, from <c>del</c>, <c>s</c> and <c>strike</c>.</summary>
        public bool Strike { get; init; }
        /// <summary>Underlined, from <c>u</c> and links.</summary>
        public bool Underline { get; init; }
        /// <summary>Monospace, from <c>code</c> and <c>pre</c>.</summary>
        public bool Code { get; init; }
        /// <summary>Whether whitespace is kept, inside <c>pre</c>.</summary>
        public bool Preformatted { get; init; }
        /// <summary>Text colour of links and mentions, or null.</summary>
        public IBrush? Foreground { get; init; }
        /// <summary>Text size of headings, or null.</summary>
        public double? Size { get; init; }
        /// <summary>Prefix of each new line, for quotes and list items.</summary>
        public string LinePrefix { get; init; } = "";
    }

    /// <summary>Collects inlines, starting block elements on new lines, and where links are in the text.</summary>
    private sealed class Builder
    {
        private readonly InlineCollection inlines = [];
        private readonly List<LinkSpan> links = [];
        private bool atLineStart = true;
        private bool pendingBreak;
        private int position;

        /// <summary>Links collected so far, by character range.</summary>
        public IReadOnlyList<LinkSpan> Links => links;

        /// <summary>Add plain text, turning web addresses into links.</summary>
        public void AddPlain(string text)
        {
            // Text between addresses as is
            int last = 0;
            foreach (Match match in WebAddress().Matches(text))
            {
                if (match.Index > last) AddText(text[last..match.Index], new Style { Preformatted = true });
                string href = match.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? $"https://{match.Value}" : match.Value;
                int start = position;
                AddText(match.Value, new Style { Preformatted = true, Underline = true, Foreground = LinkBrush });
                links.Add(new LinkSpan(start, position - start, href));
                last = match.Index + match.Length;
            }
            if (last < text.Length) AddText(text[last..], new Style { Preformatted = true });
        }

        /// <summary>Add each child node.</summary>
        public void AddChildren(INode node, Style style)
        {
            // In document order
            foreach (INode child in node.ChildNodes) Add(child, style);
        }

        /// <summary>Return the collected inlines.</summary>
        public InlineCollection Finish()
        {
            // Drop breaks at the end; links all lie before them
            while (inlines.Count > 0 && inlines[^1] is LineBreak) inlines.RemoveAt(inlines.Count - 1);
            // Return inlines
            return inlines;
        }

        /// <summary>Add one node and its children.</summary>
        private void Add(INode node, Style style)
        {
            // Text, collapsing whitespace outside pre
            if (node is IText text)
            {
                string value = style.Preformatted ? text.Data : CollapseWhitespace(text.Data);
                if (value.Length > 0) AddText(value, style);
                return;
            }
            if (node is not IElement element) return;

            // Elements by tag
            switch (element.LocalName)
            {
                case "mx-reply" or "script" or "style":
                    return;
                case "br":
                    LineBreak();
                    return;
                case "p" or "div":
                    Block(element, style);
                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Block(element, style with { Bold = true, Size = element.LocalName[1] switch { '1' => 22, '2' => 19, '3' => 17, _ => 15 } });
                    return;
                case "blockquote":
                    Block(element, style with { LinePrefix = style.LinePrefix + "▎ ", Foreground = QuoteBrush });
                    return;
                case "pre":
                    Block(element, style with { Code = true, Preformatted = true });
                    return;
                case "ul" or "ol":
                    AddList(element, style, ordered: element.LocalName == "ol");
                    return;
                case "code":
                    AddChildren(element, style with { Code = true });
                    return;
                case "strong" or "b":
                    AddChildren(element, style with { Bold = true });
                    return;
                case "em" or "i":
                    AddChildren(element, style with { Italic = true });
                    return;
                case "del" or "s" or "strike":
                    AddChildren(element, style with { Strike = true });
                    return;
                case "u":
                    AddChildren(element, style with { Underline = true });
                    return;
                case "a":
                    AddLink(element, style);
                    return;
                case "img":
                    AddText(element.GetAttribute("alt") is { Length: > 0 } alt ? alt : "[image]", style);
                    return;
                case "span" when element.HasAttribute("data-mx-spoiler"):
                    AddText("[spoiler]", style with { Italic = true });
                    return;
                default:
                    AddChildren(element, style);
                    return;
            }
        }

        /// <summary>Add a block element on its own lines.</summary>
        private void Block(IElement element, Style style)
        {
            // Break before and after
            BreakBeforeBlock();
            AddChildren(element, style);
            pendingBreak = true;
        }

        /// <summary>Add list items with bullets or numbers.</summary>
        private void AddList(IElement list, Style style, bool ordered)
        {
            // One line per item, nested lists indented
            BreakBeforeBlock();
            int number = int.TryParse(list.GetAttribute("start"), out int start) ? start : 1;
            foreach (IElement item in list.Children.Where(c => c.LocalName == "li"))
            {
                if (!atLineStart) LineBreak();
                AddText(ordered ? $"{number++}. " : "• ", style);
                AddChildren(item, style with { LinePrefix = style.LinePrefix + "   " });
            }
            pendingBreak = true;
        }

        /// <summary>Add a link: mentions as names, other links underlined with their address when it differs from the text.</summary>
        private void AddLink(IElement link, Style style)
        {
            // Mention pills keep their text, highlighted
            string href = link.GetAttribute("href") ?? "";
            string label = link.TextContent;
            FlushBreak();
            int start = position;
            if (href.StartsWith("https://matrix.to/#/", StringComparison.Ordinal))
            {
                AddText(label, style with { Bold = true, Foreground = LinkBrush });
                links.Add(new LinkSpan(start, position - start, href));
                return;
            }

            // Links show where they lead
            AddChildren(link, style with { Underline = true, Foreground = LinkBrush });
            if (href.Length > 0 && !string.Equals(label.Trim(), href, StringComparison.OrdinalIgnoreCase)) AddText($" ({href})", style with { Foreground = QuoteBrush });
            if (href.Length > 0 && position > start) links.Add(new LinkSpan(start, position - start, href));
        }

        /// <summary>Add text, splitting preformatted text at its line breaks.</summary>
        private void AddText(string text, Style style)
        {
            // A blank line owed by an earlier block
            FlushBreak();

            // Line by line, with the line prefix at each start
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) LineBreak();
                string line = atLineStart ? lines[i].TrimStart(style.Preformatted ? [] : [' ']) : lines[i];
                if (line.Length == 0) continue;
                if (atLineStart && style.LinePrefix.Length > 0)
                {
                    inlines.Add(new Run(style.LinePrefix) { Foreground = QuoteBrush });
                    position += style.LinePrefix.Length;
                }
                inlines.Add(CreateRun(line, style));
                position += line.Length;
                atLineStart = false;
            }
        }

        /// <summary>Return a run in <paramref name="style"/>.</summary>
        private static Run CreateRun(string text, Style style)
        {
            // Only the properties the style sets, so theme defaults apply otherwise
            Run run = new(text);
            if (style.Bold) run.FontWeight = FontWeight.SemiBold;
            if (style.Italic) run.FontStyle = FontStyle.Italic;
            if (style.Code)
            {
                run.FontFamily = Monospace;
                run.Background = CodeBackground;
            }
            if (style.Foreground is IBrush foreground) run.Foreground = foreground;
            if (style.Size is double size) run.FontSize = size;
            if (style.Strike || style.Underline) run.TextDecorations = style.Strike ? global::Avalonia.Media.TextDecorations.Strikethrough : global::Avalonia.Media.TextDecorations.Underline;
            // Return run
            return run;
        }

        /// <summary>Write the blank line owed by an earlier block, so text positions after it are final.</summary>
        private void FlushBreak()
        {
            // Once
            if (!pendingBreak) return;
            if (!atLineStart) LineBreak();
            LineBreak();
            pendingBreak = false;
        }

        /// <summary>Start a new line.</summary>
        private void LineBreak()
        {
            // Line break, one character of the text, then the prefix with the next text
            inlines.Add(new LineBreak());
            position++;
            atLineStart = true;
        }

        /// <summary>Owe a blank line before the next text, unless nothing came before.</summary>
        private void BreakBeforeBlock()
        {
            // Nothing yet: the block starts at the top
            if (inlines.Count > 0) pendingBreak = true;
        }

        /// <summary>Collapse runs of whitespace to one space.</summary>
        private static string CollapseWhitespace(string text)
        {
            // One pass over the text
            System.Text.StringBuilder result = new(text.Length);
            bool space = false;
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!space) result.Append(' ');
                    space = true;
                    continue;
                }
                result.Append(character);
                space = false;
            }
            // Return collapsed text
            return result.ToString();
        }
    }
}

/// <summary>Where a link is in the text of a <see cref="RichText"/>.</summary>
/// <param name="Start">Index of its first character.</param>
/// <param name="Length">Number of characters.</param>
/// <param name="Href">Address it leads to.</param>
internal sealed record LinkSpan(int Start, int Length, string Href);

/// <summary>Arguments of <see cref="RichText.LinkClicked"/>.</summary>
/// <param name="routedEvent">The event.</param>
/// <param name="href">Address of the clicked link.</param>
public sealed class LinkClickedEventArgs(RoutedEvent routedEvent, string href) : RoutedEventArgs(routedEvent)
{
    /// <summary>Address of the clicked link.</summary>
    public string Href { get; } = href;
}
