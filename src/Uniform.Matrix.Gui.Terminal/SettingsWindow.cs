namespace Uniform.Matrix.Gui.Terminal;

using System.Collections.ObjectModel;
using TuiAttribute = global::Terminal.Gui.Drawing.Attribute;
using Uniform.Matrix.Hosting;
using global::Terminal.Gui.App;
using global::Terminal.Gui.Drawing;
using global::Terminal.Gui.Input;
using global::Terminal.Gui.ViewBase;
using global::Terminal.Gui.Views;
using Uniform.Matrix.Gui.Terminal.Localization;

/// <summary>Settings window: every option with its value, source and description; edit, reset, apply, save, load and export.</summary>
/// <remarks>Edits stay in the window until applied or saved; see <see cref="SettingsEditor"/>.</remarks>
public sealed class SettingsWindow : Dialog
{
    private readonly IApplication app;
    private readonly SettingsEditor editor;
    private readonly ObservableCollection<string> rows = [];
    private readonly ListView list = new() { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(8) };
    private readonly Label description = new() { X = 1, Y = Pos.AnchorEnd(7), Width = Dim.Fill(1), Height = 5 };
    private string descriptionText = "";

    /// <summary>Build the window over <paramref name="editor"/>.</summary>
    public SettingsWindow(IApplication app, SettingsEditor editor)
    {
        // Layout: file, list, description, actions
        this.app = app;
        this.editor = editor;
        Title = Strings.Settings;
        Width = Dim.Fill(2);
        Height = Dim.Fill(1);
        Label file = new() { Text = Texts.Format(Strings.SettingsFileLegend, editor.SettingsPath), X = 1, Y = 0, Width = Dim.Fill(1) };
        list.SetSource(rows);
        list.RowRender += OnRowRender;
        list.ValueChanged += (_, _) => ShowDescription();
        description.ViewportChanged += (_, _) => WrapDescription();
        list.Accepting += (_, e) => { e.Handled = true; Edit(); };
        Add(file, list, description);

        // Actions that keep the window open, then Close
        View? previous = null;
        foreach ((string text, Action action) in new (string, Action)[] { (Strings.ButtonEdit, Edit), (Strings.ButtonReset, Reset), (Strings.ButtonApply, Apply), (Strings.ButtonSave, Save), (Strings.ButtonLoad, Load), (Strings.ButtonExport, Export) })
        {
            Button button = new() { Text = text, X = previous is null ? 1 : Pos.Right(previous) + 1, Y = Pos.AnchorEnd(1) };
            button.Accepting += (_, e) => { e.Handled = true; action(); };
            Add(button);
            previous = button;
        }
        AddButton(new Button { Text = Strings.ButtonClose });

        // Fill
        Refresh();
        list.SelectedItem = 0;
        list.SetFocus();
    }

    /// <summary>Whether the user may close: asks when changes are not saved.</summary>
    public bool ConfirmClose() => !editor.HasUnsavedChanges || Dialogs.Confirm(app, Strings.UnsavedSettings, Strings.UnsavedQuestion);

    /// <summary>Option of the selected row.</summary>
    private OptionInfo? Selected => list.SelectedItem is int index && index < editor.Options.Count ? editor.Options[index] : null;

    /// <summary>Rebuild the rows, keeping the selection.</summary>
    private void Refresh()
    {
        // One row per option
        int? selected = list.SelectedItem;
        int keyWidth = Math.Min(50, editor.Options.Max(o => o.Key.Length));
        rows.Clear();
        foreach (OptionInfo option in editor.Options)
        {
            string mark = editor.GetChange(option) switch { OptionChange.Edited => "*", OptionChange.Applied => "+", _ => " " };
            string restart = option.RestartRequired ? "↻" : " ";
            rows.Add($"{mark}{restart} {option.Key.PadRight(keyWidth)}  {Clip(editor.GetDisplayValue(option), 36),-36}  {editor.GetSource(option)}");
        }
        list.SelectedItem = selected;
        ShowDescription();
    }

    /// <summary>Show the selected option's description, default, type and note.</summary>
    private void ShowDescription()
    {
        // Assert selection
        if (Selected is not OptionInfo option)
        {
            descriptionText = "";
            WrapDescription();
            return;
        }

        // Description and facts
        Type type = Nullable.GetUnderlyingType(option.Type) ?? option.Type;
        string choices = option.Choices.Count > 0 ? Texts.Format(Strings.OneOf, string.Join(", ", option.Choices)) : "";
        string defaults = option.Secret ? "" : Texts.Format(Strings.Default, option.DefaultValue ?? Strings.NotSet);
        string restart = option.RestartRequired ? Strings.EffectAfterRestart : Strings.EffectWhenApplied;
        string source = editor.GetSource(option);
        string overridden = source is "command line" or "environment" ? Texts.Format(Strings.OverriddenBy, source) : "";
        descriptionText = Texts.Format(Strings.OptionDescription, option.Key, option.Description, type.Name, choices, defaults, restart, overridden);
        WrapDescription();
    }

    /// <summary>Wrap the description to the label's width.</summary>
    private void WrapDescription()
    {
        // Wrap each paragraph
        int width = description.Viewport.Width > 10 ? description.Viewport.Width : 100;
        description.Text = string.Join('\n', descriptionText.Split('\n').SelectMany(p => TextWrapper.Wrap(p, width, 0)));
    }

    /// <summary>Edit the selected option: a list for enums and booleans, a text field otherwise.</summary>
    private void Edit()
    {
        // Assert editable
        if (Selected is not OptionInfo option) return;
        if (option.Secret)
        {
            Dialogs.Info(app, option.Key, Strings.SecretValue);
            return;
        }

        // Ask for the value
        string current = editor.GetValue(option) ?? "";
        string? text = option.Choices.Count > 0
            ? Dialogs.PickOne(app, option.Key, option.Choices, current)
            : Dialogs.AskAllowEmpty(app, option.Key, Texts.Format(Strings.NewValuePrompt, option.Description, (option.Type.IsValueType && Nullable.GetUnderlyingType(option.Type) is null ? "" : Strings.EmptyForNotSet)), current);
        if (text is null) return;

        // Validate and show
        if (editor.Set(option, text) is string error)
        {
            Dialogs.Error(app, option.Key, error);
            return;
        }
        Refresh();
    }

    /// <summary>Undo the selected option's edit or applied change; without either, resets it to its default once saved.</summary>
    private void Reset()
    {
        // One step back per press
        if (Selected is not OptionInfo option || option.Secret) return;
        try
        {
            editor.Reset(option);
        }
        catch (IOException ex)
        {
            Dialogs.Error(app, Strings.Reset, ex.Message);
        }
        Refresh();
    }

    /// <summary>Apply edits to the running client.</summary>
    private void Apply()
    {
        // Apply and report what waits for a restart; a store elsewhere may not answer
        IReadOnlyList<string> restart;
        try
        {
            restart = editor.Apply();
        }
        catch (IOException ex)
        {
            Dialogs.Error(app, Strings.Apply, ex.Message);
            return;
        }
        Refresh();
        if (restart.Count > 0) Dialogs.Info(app, Strings.Applied, Texts.Format(Strings.AppliedRestart, string.Join('\n', restart)));
    }

    /// <summary>Save all changes to the settings file.</summary>
    private void Save()
    {
        // Write, reporting failures
        IReadOnlyList<string> restart;
        try
        {
            restart = editor.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(app, Strings.Save, ex.Message);
            return;
        }
        Refresh();
        Dialogs.Info(app, Strings.Saved, Texts.Format(Strings.SavedTo, editor.SettingsPath, (restart.Count > 0 ? Texts.Format(Strings.TheseAfterRestart, string.Join('\n', restart)) : "")));
    }

    /// <summary>Load options from a settings file as edits, to apply or save.</summary>
    private void Load()
    {
        // Pick file
        string? path = Dialogs.Ask(app, Strings.LoadSettings, Strings.LoadPrompt, editor.SettingsPath);
        if (path is null) return;
        if (!File.Exists(path))
        {
            Dialogs.Error(app, Strings.LoadSettings, Texts.Format(Strings.DoesNotExist, path));
            return;
        }

        // Read as edits
        int count;
        try
        {
            count = editor.Load(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            Dialogs.Error(app, Strings.LoadSettings, ex.Message);
            return;
        }
        Refresh();
        Dialogs.Info(app, Strings.LoadSettings, Texts.Format(Strings.LoadedSettings, count));
    }

    /// <summary>Write the current non-default settings to another file.</summary>
    private void Export()
    {
        // Pick file and write
        string? path = Dialogs.Ask(app, Strings.ExportSettings, Strings.ExportPrompt, Path.Combine(Path.GetDirectoryName(editor.SettingsPath)!, "settings-copy.json"));
        if (path is null) return;
        try
        {
            editor.Export(path);
            Dialogs.Info(app, Strings.ExportSettings, Texts.Format(Strings.Wrote, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(app, Strings.ExportSettings, ex.Message);
        }
    }

    /// <summary>Color rows by change state.</summary>
    private void OnRowRender(object? sender, ListViewRowEventArgs args)
    {
        // Selected row keeps the focus color
        if (args.Row == list.SelectedItem && list.HasFocus) return;
        TuiAttribute normal = list.GetAttributeForRole(VisualRole.Normal);
        OptionChange change = args.Row < editor.Options.Count ? editor.GetChange(editor.Options[args.Row]) : OptionChange.None;
        Color foreground = change switch { OptionChange.Edited => Color.BrightYellow, OptionChange.Applied => Color.BrightCyan, _ => normal.Foreground };
        // Apply
        args.RowAttribute = new TuiAttribute(foreground, normal.Background);
    }

    /// <summary>Shorten text to <paramref name="width"/> columns with an ellipsis.</summary>
    private static string Clip(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";
}
