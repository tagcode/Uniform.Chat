namespace Uniform.Matrix.Gui.Terminal;

using System.Globalization;
using global::Terminal.Gui.Text;

/// <summary>Word-wraps text by terminal columns, so wide characters such as emoji and CJK count as two.</summary>
public static class TextWrapper
{
    /// <summary>Wrap <paramref name="text"/> to <paramref name="width"/> columns, breaking at spaces when possible; continuation lines and later paragraphs are indented by <paramref name="indent"/> columns.</summary>
    /// <example><code><![CDATA[TextWrapper.Wrap("12:00 <alice> a long message", 20, indent: 14);]]></code></example>
    public static IReadOnlyList<string> Wrap(string text, int width, int indent = 0)
    {
        // Assert usable width; a large indent on a narrow view would leave no room
        if (width < 2) return [text];
        if (indent > width / 2) indent = 0;

        // Wrap each paragraph; later ones align with the first line's text
        List<string> lines = [];
        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < paragraphs.Length; i++) WrapParagraph((i == 0 ? "" : new string(' ', indent)) + paragraphs[i].Replace('\t', ' '), width, indent, lines);
        // Return lines
        return lines;
    }

    /// <summary>Return the number of terminal columns <paramref name="text"/> occupies.</summary>
    public static int Columns(string text) => Math.Max(0, text.GetColumns());

    /// <summary>Append the wrapped lines of one paragraph to <paramref name="lines"/>.</summary>
    private static void WrapParagraph(string paragraph, int width, int indent, List<string> lines)
    {
        // Current line as text elements, so combined characters stay together
        List<(string Element, int Columns)> line = [];
        int columns = 0;
        int prefix = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(paragraph);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int elementColumns = Columns(element);
            if (columns + elementColumns > width && line.Count > prefix) (columns, prefix) = BreakLine(line, indent, lines);
            line.Add((element, elementColumns));
            columns += elementColumns;
        }
        // Add the last line
        lines.Add(string.Concat(line.Select(e => e.Element)).TrimEnd());
    }

    /// <summary>Emit <paramref name="line"/> up to its last space, or whole when it has none, and keep the rest indented; return the new column count and prefix length.</summary>
    private static (int Columns, int Prefix) BreakLine(List<(string Element, int Columns)> line, int indent, List<string> lines)
    {
        // Break at the last space after the indent, or hard-break
        int space = line.FindLastIndex(e => e.Element == " ");
        int firstContent = line.FindIndex(e => e.Element != " ");
        bool atSpace = space > firstContent && firstContent >= 0;
        List<(string Element, int Columns)> rest = atSpace ? line[(space + 1)..] : [];
        lines.Add(string.Concat(line.Take(atSpace ? space : line.Count).Select(e => e.Element)).TrimEnd());

        // Start the continuation line
        line.Clear();
        line.AddRange(Enumerable.Repeat((" ", 1), indent));
        line.AddRange(rest);
        // Return new state
        return (line.Sum(e => e.Columns), indent);
    }
}
