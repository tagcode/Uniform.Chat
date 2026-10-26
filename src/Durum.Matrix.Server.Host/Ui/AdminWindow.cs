namespace Durum.Matrix.Server.Host.Ui;

using System.Collections.ObjectModel;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Protocol.Core;
using global::Terminal.Gui.App;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Base of the admin lists: a header, a list of rows, details of the selected row, and action buttons.</summary>
/// <typeparam name="T">Type of the listed items.</typeparam>
public abstract class AdminWindow<T> : Dialog
{
    private readonly ObservableCollection<string> rows = [];
    private readonly ListView list = new() { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(6) };
    private readonly Label details = new() { X = 1, Y = Pos.AnchorEnd(5), Width = Dim.Fill(1), Height = 3 };
    private IReadOnlyList<T> items = [];
    private Button? lastAction;

    /// <summary>Build the window; subclasses add buttons with <see cref="AddAction"/>, then call <see cref="Reload"/>.</summary>
    protected AdminWindow(IApplication app, string title, string header)
    {
        // Layout: header, list, details, actions
        Tui = app;
        Title = title;
        Width = Dim.Fill(2);
        Height = Dim.Fill(1);
        Label heading = new() { Text = header, X = 1, Y = 1, Width = Dim.Fill(1) };
        list.SetSource(rows);
        list.ValueChanged += (_, _) => ShowDetails();
        Add(heading, list, details);
        AddButton(new Button { Text = "_Close" });
    }

    /// <summary>Application the window runs in.</summary>
    protected IApplication Tui { get; }

    /// <summary>Selected item, if any.</summary>
    protected T? Selected => list.SelectedItem is int index && index < items.Count ? items[index] : default;

    /// <summary>Load the items.</summary>
    protected abstract Task<IReadOnlyList<T>> LoadAsync();

    /// <summary>Format an item as one row under the header.</summary>
    protected abstract string FormatRow(T item);

    /// <summary>Describe an item below the list.</summary>
    protected abstract string Describe(T item);

    /// <summary>Add an action button after the previous ones.</summary>
    protected void AddAction(string text, Action action)
    {
        // Next to the last action
        Button button = new() { Text = text, X = lastAction is null ? 1 : Pos.Right(lastAction) + 1, Y = Pos.AnchorEnd(1) };
        button.Accepting += (_, e) => { e.Handled = true; action(); };
        Add(button);
        lastAction = button;
    }

    /// <summary>Reload the items, keeping the selection where possible.</summary>
    protected void Reload()
    {
        // Load off the UI thread, then show
        int selected = list.SelectedItem ?? 0;
        if (!Run(Title.ToString(), async () => items = await LoadAsync())) return;
        rows.Clear();
        foreach (T item in items) rows.Add(FormatRow(item));
        list.SelectedItem = items.Count == 0 ? null : Math.Min(selected, items.Count - 1);
        ShowDetails();
        list.SetFocus();
    }

    /// <summary>Run a database action off the UI thread and wait for it; show Matrix errors and return false for them.</summary>
    protected bool Run(string title, Func<Task> action)
    {
        // Thread pool, so continuations do not need the UI thread that waits
        try
        {
            Task.Run(action).GetAwaiter().GetResult();
            return true;
        }
        catch (MatrixException ex)
        {
            Dialogs.Error(Tui, title, ex.Error ?? ex.Message);
            return false;
        }
    }

    /// <summary>Clip text to a column width, with an ellipsis.</summary>
    protected static string Clip(string? text, int width) => text is null ? "" : text.Length <= width ? text : text[..(width - 1)] + "…";

    /// <summary>Show the selected item's details.</summary>
    private void ShowDetails() => details.Text = Selected is T item ? Describe(item) : "";
}
