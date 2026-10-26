namespace Uniform.Matrix.Hosting;

/// <summary>State of an option in a <see cref="SettingsEditor"/>.</summary>
public enum OptionChange
{
    /// <summary>Unchanged.</summary>
    None,
    /// <summary>Edited, not applied yet.</summary>
    Edited,
    /// <summary>Applied to the running client, not saved.</summary>
    Applied,
}

/// <summary>Edits options for the settings window: changes are kept here, then applied to the running client or saved to the settings file.</summary>
public sealed class SettingsEditor
{
    private readonly SettingsStore store;
    private readonly Dictionary<string, string?> edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Edit <paramref name="options"/> stored in <paramref name="store"/>.</summary>
    public SettingsEditor(SettingsStore store, IReadOnlyList<OptionInfo> options)
    {
        // Store dependencies
        this.store = store;
        Options = options;
    }

    /// <summary>All options, in display order.</summary>
    public IReadOnlyList<OptionInfo> Options { get; }
    /// <summary>The settings file.</summary>
    public string SettingsPath => store.Path;
    /// <summary>Whether there are changes not saved to the settings file.</summary>
    public bool HasUnsavedChanges => edits.Count > 0 || applied.Count > 0;

    /// <summary>Return the value shown for <paramref name="option"/>: the edit, else the current value, else the default; null when unset.</summary>
    public string? GetValue(OptionInfo option)
    {
        // Edit first, then applied, then configuration
        string? value = edits.TryGetValue(option.Key, out string? edited) ? edited : applied.TryGetValue(option.Key, out string? current) ? current : store.GetValue(option.Key);
        value ??= option.DefaultValue;
        // Return in the normal form, e.g. "true" for JSON's "True"
        return value is null || option.Secret ? value : option.Parse(value).Value ?? value;
    }

    /// <summary>Return the value as shown in the list: secrets hidden, unset as <c>(not set)</c>.</summary>
    public string GetDisplayValue(OptionInfo option)
    {
        // Secrets are never shown
        string? value = GetValue(option);
        if (option.Secret) return value is null ? "(not set)" : "(hidden)";
        // Return value
        return value ?? "(not set)";
    }

    /// <summary>Return whether <paramref name="option"/> is edited or applied but not saved.</summary>
    public OptionChange GetChange(OptionInfo option) => edits.ContainsKey(option.Key) ? OptionChange.Edited : applied.ContainsKey(option.Key) ? OptionChange.Applied : OptionChange.None;

    /// <summary>Return which layer sets the option, or <c>edited</c> for pending changes.</summary>
    public string GetSource(OptionInfo option) => GetChange(option) switch
    {
        OptionChange.Edited => "edited",
        OptionChange.Applied => "applied",
        _ => store.GetSource(option.Key),
    };

    /// <summary>Set <paramref name="option"/> from text; return an error message when the text is not a valid value.</summary>
    public string? Set(OptionInfo option, string text)
    {
        // Assert editable and valid
        if (option.Secret) return "This value is secret; give it on the command line or when asked.";
        (string? value, string? error) = option.Parse(text.Trim());
        if (error is not null) return error;
        // Keep as edit
        edits[option.Key] = value;
        return null;
    }

    /// <summary>Undo the latest unsaved step of <paramref name="option"/>: discards an edit, else undo an applied change, else mark it for removal from the settings file on save, so the built-in default or <c>appsettings.json</c> applies.</summary>
    /// <returns>What was undone: <see cref="OptionChange.Edited"/>, <see cref="OptionChange.Applied"/>, or <see cref="OptionChange.None"/> for a reset to the default.</returns>
    public OptionChange Reset(OptionInfo option)
    {
        // Pending edit: back to the current value
        if (edits.Remove(option.Key)) return OptionChange.Edited;

        // Applied change: lower layers show again
        if (applied.Remove(option.Key))
        {
            store.Apply(new Dictionary<string, string?> { [option.Key] = null });
            return OptionChange.Applied;
        }

        // Saved or default: remove from the settings file on save
        edits[option.Key] = null;
        // Return reset to default
        return OptionChange.None;
    }

    /// <summary>Apply edits to the running client; return the keys among them that need a restart.</summary>
    public IReadOnlyList<string> Apply()
    {
        // Move edits to the applied layer
        IReadOnlyList<string> restart = RestartKeys(edits.Keys);
        store.Apply(edits);
        foreach ((string key, string? value) in edits) applied[key] = value;
        edits.Clear();
        // Return keys that wait for a restart
        return restart;
    }

    /// <summary>Save all edited and applied changes to the settings file, which also applies them; return the keys among them that need a restart.</summary>
    public IReadOnlyList<string> Save()
    {
        // Edits override earlier applied values
        Dictionary<string, string?> changes = new(applied, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in edits) changes[key] = value;
        IReadOnlyList<string> restart = RestartKeys(changes.Keys);

        // Write and clear
        store.Save(changes);
        edits.Clear();
        applied.Clear();
        // Return keys that wait for a restart
        return restart;
    }

    /// <summary>Write the current values that differ from the defaults to another file, e.g. to keep a copy; secrets are left out.</summary>
    public void Export(string path)
    {
        // Non-default, non-secret values
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (OptionInfo option in Options)
        {
            string? value = GetValue(option);
            if (option.Secret || value is null || value == option.DefaultValue) continue;
            values[option.Key] = value;
        }
        // Write
        store.Save(values, path);
    }

    /// <summary>Load the known options of a settings file as edits; return how many were loaded.</summary>
    public int Load(string path)
    {
        // Known, non-secret keys only
        int count = 0;
        foreach ((string key, string? value) in SettingsStore.Read(path))
        {
            if (Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) is not OptionInfo option || option.Secret) continue;
            (string? parsed, string? error) = option.Parse(value ?? "");
            if (error is not null) continue;
            edits[option.Key] = parsed;
            count++;
        }
        // Return count
        return count;
    }

    /// <summary>Discard edits that are not applied yet.</summary>
    public void DiscardEdits() => edits.Clear();

    /// <summary>Return the keys among <paramref name="keys"/> whose options need a restart.</summary>
    private List<string> RestartKeys(IEnumerable<string> keys) =>
        [.. keys.Where(k => Options.Any(o => o.RestartRequired && string.Equals(o.Key, k, StringComparison.OrdinalIgnoreCase)))];
}
