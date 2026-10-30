namespace Uniform.Chat.Terminal.Ui;

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using global::Terminal.Gui.Drawing;
using global::Terminal.Gui.Views;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;

/// <summary>A list of text rows that draws one warning phrase, such as <see cref="NotTrusted"/>, in white on red wherever it appears, and the rest of each row as usual.</summary>
/// <remarks>Wraps a <see cref="ListWrapper{T}"/>, whose drawing cannot be overridden, and hands it everything but drawing the rows.</remarks>
internal sealed class WarningListSource : IListDataSource
{
    /// <summary>Label of a device that the account's current identity has not signed.</summary>
    public const string NotTrusted = "Not trusted";
    private readonly ObservableCollection<string> rows;
    private readonly string warning;
    private readonly ListWrapper<string> inner;

    /// <summary>Wrap <paramref name="rows"/>, marking <paramref name="warning"/> in them.</summary>
    public WarningListSource(ObservableCollection<string> rows, string warning)
    {
        // Store rows and phrase; the wrapper does the rest
        this.rows = rows;
        this.warning = warning;
        inner = new ListWrapper<string>(rows);
    }

    /// <summary>Raised when the rows change.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => inner.CollectionChanged += value;
        remove => inner.CollectionChanged -= value;
    }

    /// <summary>Get the number of rows.</summary>
    public int Count => inner.Count;

    /// <summary>Get the width of the longest row.</summary>
    public int MaxItemLength => inner.MaxItemLength;

    /// <summary>Get or set whether row changes are held back.</summary>
    public bool SuspendCollectionChangedEvent
    {
        get => inner.SuspendCollectionChangedEvent;
        set => inner.SuspendCollectionChangedEvent = value;
    }

    /// <summary>Return whether a row is marked.</summary>
    public bool IsMarked(int item) => inner.IsMarked(item);

    /// <summary>Mark or unmark a row.</summary>
    public void SetMark(int item, bool value) => inner.SetMark(item, value);

    /// <summary>Return the rows as a list.</summary>
    public IList ToList() => inner.ToList();

    /// <summary>Draw the mark of a row as the wrapper does.</summary>
    public bool RenderMark(ListView listView, int item, int row, bool isMarked, bool allowsMultiple) => inner.RenderMark(listView, item, row, isMarked, allowsMultiple);

    /// <summary>Release the wrapper.</summary>
    public void Dispose() => inner.Dispose();

    /// <summary>Draw a row in pieces: the warning phrase on red, the rest in the list's attribute for the row.</summary>
    public void Render(ListView container, bool selected, int item, int col, int line, int width, int viewportX = 0)
    {
        // The visible part of the row, padded to the width
        string text = item < rows.Count ? rows[item] : "";
        string visible = (viewportX < text.Length ? text[viewportX..] : "").PadRight(width);
        if (visible.Length > width) visible = visible[..width];
        TuiAttribute normal = container.GetAttributeForRole(selected ? (container.HasFocus ? VisualRole.Focus : VisualRole.Active) : VisualRole.Normal);
        container.Move(col, line);

        // Pieces before, of and after each warning
        int start = 0;
        for (int found = visible.IndexOf(warning, StringComparison.Ordinal); found >= 0; found = visible.IndexOf(warning, start, StringComparison.Ordinal))
        {
            container.SetAttribute(normal);
            container.AddStr(visible[start..found]);
            container.SetAttribute(new TuiAttribute(Color.White, Color.Red));
            container.AddStr(warning);
            start = found + warning.Length;
        }
        // Done with the rest of the row
        container.SetAttribute(normal);
        container.AddStr(visible[start..]);
    }
}
