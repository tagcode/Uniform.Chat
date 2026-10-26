namespace Uniform.Matrix.Gui.Terminal;

using System.Collections.ObjectModel;
using global::Terminal.Gui.App;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;

/// <summary>Modal dialogs; call on the UI thread.</summary>
public static class Dialogs
{
    /// <summary>Ask for one line of text; return null when cancelled or left empty.</summary>
    /// <param name="app">Running application.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="label">Prompt above the field.</param>
    /// <param name="initial">Initial text.</param>
    /// <param name="secret">Whether to mask input, e.g. for passwords.</param>
    public static string? Ask(IApplication app, string title, string label, string initial = "", bool secret = false) =>
        AskCore(app, title, label, initial, secret) is { Length: > 0 } text ? text : null;

    /// <summary>Ask for one line of text that may be empty; return null only when cancelled.</summary>
    public static string? AskAllowEmpty(IApplication app, string title, string label, string initial = "") => AskCore(app, title, label, initial, secret: false);

    /// <summary>Ask for one line of text; return it trimmed when accepted, null when cancelled.</summary>
    private static string? AskCore(IApplication app, string title, string label, string initial, bool secret)
    {
        // Build dialog; the label may span lines
        using Dialog dialog = new() { Title = title, Width = Dim.Percent(60) };
        Label prompt = new() { Text = label, X = 1, Y = 0, Width = Dim.Fill(1) };
        TextField field = new() { Text = initial, Secret = secret, X = 1, Y = Pos.Bottom(prompt), Width = Dim.Fill(1) };
        dialog.Add(prompt, field);
        dialog.AddButton(new Button { Text = "_Cancel" });
        dialog.AddButton(new Button { Text = "_OK", IsDefault = true });

        // Run; Enter in the field accepts with the default button
        field.SetFocus();
        app.Run(dialog);
        string text = field.Text.Trim();
        // Return text when accepted
        return dialog.Result == 1 ? text : null;
    }

    /// <summary>Let the user pick one of <paramref name="options"/>; the current value is marked and selected. Returns null when cancelled.</summary>
    public static string? PickOne(IApplication app, string title, IReadOnlyList<string> options, string? current)
    {
        // Rows with the current value marked
        int index = options.ToList().FindIndex(o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase));
        ObservableCollection<string> rows = [.. options.Select((o, i) => $"{(i == index ? "●" : " ")} {o}")];

        // Width fits the rows; the height follows the content
        int listHeight = Math.Min(options.Count, 15);
        int width = Math.Max(Math.Max(30, title.Length + 8), options.Max(o => o.Length) + 8);
        using Dialog dialog = new() { Title = title, Width = width };
        Label currentLabel = new() { Text = $"Current: {current ?? "(not set)"}", X = 1, Y = 0, Width = Dim.Fill(1) };
        ListView list = new() { X = 1, Y = 2, Width = Dim.Fill(1), Height = listHeight };
        list.SetSource(rows);
        list.SelectedItem = Math.Max(0, index);
        list.Accepting += (_, e) => { e.Handled = true; dialog.Result = 1; app.RequestStop(dialog); };
        dialog.Add(currentLabel, list);
        dialog.AddButton(new Button { Text = "_Cancel" });
        dialog.AddButton(new Button { Text = "_OK", IsDefault = true });

        // Run
        list.SetFocus();
        app.Run(dialog);
        // Return the chosen option
        return dialog.Result == 1 && list.SelectedItem is int chosen && chosen < options.Count ? options[chosen] : null;
    }

    /// <summary>Show long read-only text, e.g. a log entry with its stack trace.</summary>
    public static void ShowText(IApplication app, string title, string text)
    {
        // Scrollable lines filling most of the screen
        using Dialog dialog = new() { Title = title, Width = Dim.Percent(90), Height = Dim.Percent(80) };
        ObservableCollection<string> lines = [.. text.Replace("\t", "    ").Split('\n')];
        ListView view = new() { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        view.SetSource(lines);
        dialog.Add(view);
        dialog.AddButton(new Button { Text = "_Close", IsDefault = true });

        // Run
        view.SetFocus();
        app.Run(dialog);
    }

    /// <summary>Show <paramref name="text"/> in a read-only field that can be selected and copied, e.g. a recovery key.</summary>
    public static void ShowCopyable(IApplication app, string title, string message, string text)
    {
        // Message above the text
        using Dialog dialog = new() { Title = title, Width = Dim.Percent(70) };
        Label label = new() { Text = message, X = 1, Y = 0, Width = Dim.Fill(1) };
        TextField field = new() { Text = text, ReadOnly = true, X = 1, Y = Pos.Bottom(label) + 1, Width = Dim.Fill(1) };
        dialog.Add(label, field);
        dialog.AddButton(new Button { Text = "_Done", IsDefault = true });

        // Run
        field.SetFocus();
        app.Run(dialog);
    }

    /// <summary>Ask a yes or no question; return true for yes.</summary>
    public static bool Confirm(IApplication app, string title, string message) => MessageBox.Query(app, title, message, "_No", "_Yes") == 1;

    /// <summary>Show a message with an OK button.</summary>
    public static void Info(IApplication app, string title, string message) => MessageBox.Query(app, title, message, "_OK");

    /// <summary>Show an error with an OK button.</summary>
    public static void Error(IApplication app, string title, string message) => MessageBox.ErrorQuery(app, title, message, "_OK");

    /// <summary>Let the user pick one of <paramref name="options"/>; return its index, or null when closed.</summary>
    public static int? Choose(IApplication app, string title, string message, IReadOnlyList<string> options) => MessageBox.Query(app, title, message, [.. options]);

    /// <summary>Let the user pick an existing file; return its path, or null when cancelled.</summary>
    public static string? PickFile(IApplication app, string title)
    {
        // Show the file browser
        using OpenDialog dialog = new() { Title = title, OpenMode = OpenMode.File, MustExist = true };
        app.Run(dialog);
        // Return path unless cancelled
        return dialog.Canceled || string.IsNullOrEmpty(dialog.Path) ? null : dialog.Path;
    }
}
