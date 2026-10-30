namespace Uniform.Chat.Terminal.Ui;

using System.Collections.ObjectModel;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.Drawing;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Message log of the selected room: entries wrapped to the view width, colored by <see cref="EntryKind"/>, with day separators and pending messages last.</summary>
/// <remarks>Must be used on the UI thread. Follows new lines while scrolled to the bottom, like irssi. When focused, the selected line marks the message for <see cref="SelectedEntry"/>.</remarks>
public sealed class TimelinePane : ListView
{
    private readonly List<TimelineEntry> entries = [];
    private readonly List<TimelineEntry> pending = [];
    private readonly ObservableCollection<string> lines = [];
    private readonly List<EntryKind?> lineKinds = [];
    private readonly List<TimelineEntry?> lineEntries = [];
    private int wrapWidth;
    private int wrapHeight;
    private const string PreviewIndent = "      ";

    /// <summary>Create an empty pane.</summary>
    public TimelinePane()
    {
        // Show lines without selection highlight; colors come from RowRender
        SetSource(lines);
        RowRender += OnRowRender;
        ViewportChanged += (_, _) => RewrapIfWidthChanged();
    }

    /// <summary>Event ID-carrying entries, oldest first.</summary>
    public IReadOnlyList<TimelineEntry> Entries => entries;
    /// <summary>Return preview lines for an image entry in the given columns and pane rows, e.g. ASCII art; null for none. Called on every rebuild, so it should cache.</summary>
    public Func<TimelineEntry, int, int, IReadOnlyList<string>?>? PreviewProvider { get; set; }

    /// <summary>Rebuild lines, e.g. after an image preview became available.</summary>
    public void Refresh() => Rebuild(IsAtEnd());

    /// <summary>Entry of the selected line, or null for separators and when nothing is selected.</summary>
    public TimelineEntry? SelectedEntry => SelectedItem is int index && index < lineEntries.Count ? lineEntries[index] : null;

    /// <summary>Return a shown event by ID, e.g. to quote the message a reply answers.</summary>
    public ClientEvent? FindEvent(string eventId) => entries.FirstOrDefault(e => e.EventId == eventId)?.Event;

    /// <summary>Select the last message line, e.g. when focusing the pane to act on messages.</summary>
    public void SelectLast()
    {
        // Last line belonging to an event
        int index = lineEntries.FindLastIndex(e => e?.Event is not null);
        if (index >= 0) SelectedItem = index;
    }

    /// <summary>Timestamp of the oldest entry, for loading older history; null when empty.</summary>
    public long? OldestTimestamp => entries.Count == 0 ? null : entries[0].Timestamp;

    /// <summary>Replace all entries, e.g. when switching rooms.</summary>
    public void SetEntries(IEnumerable<TimelineEntry> items)
    {
        // Replace and redraw at the bottom
        entries.Clear();
        entries.AddRange(items.OrderBy(e => e.Timestamp));
        Rebuild(scrollToEnd: true);
    }

    /// <summary>Append new entries, skipping ones already shown.</summary>
    public void Append(IEnumerable<TimelineEntry> items)
    {
        // Skip duplicates by event ID
        HashSet<string> known = [.. entries.Select(e => e.EventId).OfType<string>()];
        List<TimelineEntry> added = [.. items.Where(e => e.EventId is null || known.Add(e.EventId))];
        if (added.Count == 0) return;

        // Keep following when at the bottom
        bool follow = IsAtEnd();
        entries.AddRange(added);
        Rebuild(follow);
    }

    /// <summary>Insert older entries before the current ones, keeping the view on the same line.</summary>
    public void Prepend(IEnumerable<TimelineEntry> items)
    {
        // Skip duplicates by event ID
        HashSet<string> known = [.. entries.Select(e => e.EventId).OfType<string>()];
        List<TimelineEntry> older = [.. items.Where(e => e.EventId is null || known.Add(e.EventId)).OrderBy(e => e.Timestamp)];
        if (older.Count == 0) return;

        // Insert and keep the previous top line in place
        int before = lines.Count;
        int selected = SelectedItem ?? 0;
        entries.InsertRange(0, older);
        Rebuild(scrollToEnd: false);
        SelectedItem = Math.Min(lines.Count - 1, selected + lines.Count - before);
    }

    /// <summary>Replace the entry with the same event ID, e.g. after a redaction.</summary>
    public void Replace(TimelineEntry entry)
    {
        // Find by event ID
        int index = entries.FindIndex(e => e.EventId == entry.EventId);
        if (index < 0) return;
        // Replace and redraw in place
        bool follow = IsAtEnd();
        int selected = SelectedItem ?? 0;
        entries[index] = entry;
        Rebuild(follow);
        if (!follow) SelectedItem = Math.Min(selected, lines.Count - 1);
    }

    /// <summary>Replace pending and failed own messages shown after the timeline.</summary>
    public void SetPending(IEnumerable<TimelineEntry> items)
    {
        // Replace and redraw
        bool follow = IsAtEnd();
        pending.Clear();
        pending.AddRange(items);
        Rebuild(follow);
    }

    /// <summary>Scroll one page up.</summary>
    public void PageUp() => MovePageUp();

    /// <summary>Scroll one page down.</summary>
    public void PageDown() => MovePageDown();

    /// <summary>Whether the last line is selected, so new lines should be followed.</summary>
    private bool IsAtEnd() => lines.Count == 0 || SelectedItem is null || SelectedItem >= lines.Count - 1;

    /// <summary>Rewrap when the width changed, e.g. on terminal resize or toggling the member list, or the height changed while images are shown, as their previews scale with it.</summary>
    private void RewrapIfWidthChanged()
    {
        // Assert size change that matters
        if (Viewport.Width <= 0) return;
        bool heightMatters = Viewport.Height != wrapHeight && entries.Any(e => e.ImageUri is not null);
        if (Viewport.Width == wrapWidth && !heightMatters) return;
        // Rewrap
        Rebuild(IsAtEnd());
    }

    /// <summary>Regenerate wrapped lines from <see cref="entries"/> and <see cref="pending"/>.</summary>
    private void Rebuild(bool scrollToEnd)
    {
        // Wrap to current size
        wrapWidth = Viewport.Width;
        wrapHeight = Viewport.Height;
        int width = Math.Max(20, wrapWidth);
        List<string> newLines = [];
        lineKinds.Clear();
        lineEntries.Clear();
        DateOnly? day = null;
        foreach (TimelineEntry entry in entries.Concat(pending))
        {
            DateOnly entryDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).LocalDateTime);
            if (day is not null && entryDay != day) AddLine(newLines, $"--- Day changed {entryDay:ddd d MMM yyyy} ---", null);
            day = entryDay;
            foreach (string line in TextWrapper.Wrap(entry.Text, width, entry.Indent)) AddLine(newLines, line, entry);
            if (entry.ImageUri is not null && PreviewProvider?.Invoke(entry, width - PreviewIndent.Length, Math.Max(1, wrapHeight)) is IReadOnlyList<string> preview)
                foreach (string line in preview) AddLine(newLines, PreviewIndent + line, entry, EntryKind.Preview);
        }

        // Swap content
        lines.Clear();
        foreach (string line in newLines) lines.Add(line);
        if (scrollToEnd && lines.Count > 0) MoveEnd();
        SetNeedsDraw();
    }

    /// <summary>Add a line with its entry, null for separators; <paramref name="kind"/> overrides the entry's kind, e.g. for previews.</summary>
    private void AddLine(List<string> target, string line, TimelineEntry? entry, EntryKind? kind = null)
    {
        // Keep kinds and entries parallel to lines
        target.Add(line);
        lineKinds.Add(kind ?? entry?.Kind);
        lineEntries.Add(entry);
    }

    /// <summary>Color each line by kind on the normal background; the selection bar shows only while focused, covering the selected message.</summary>
    private void OnRowRender(object? sender, ListViewRowEventArgs args)
    {
        // Selected message while focused
        TuiAttribute normal = GetAttributeForRole(VisualRole.Normal);
        TimelineEntry? selected = HasFocus ? SelectedEntry : null;
        if (selected is not null && args.Row < lineEntries.Count && ReferenceEquals(lineEntries[args.Row], selected))
        {
            args.RowAttribute = GetAttributeForRole(VisualRole.Focus);
            return;
        }

        // Pick foreground by kind
        EntryKind? kind = args.Row < lineKinds.Count ? lineKinds[args.Row] : EntryKind.Message;
        Color foreground = kind switch
        {
            null => Color.Cyan,
            EntryKind.Own => Color.BrightCyan,
            EntryKind.Highlight => Color.BrightYellow,
            EntryKind.Emote => Color.BrightMagenta,
            EntryKind.Notice => Color.Gray,
            EntryKind.Info => Color.DarkGray,
            EntryKind.Pending => Color.DarkGray,
            EntryKind.Failed => Color.BrightRed,
            EntryKind.Preview => Color.Gray,
            _ => normal.Foreground,
        };
        // Apply
        args.RowAttribute = new TuiAttribute(foreground, normal.Background);
    }
}
