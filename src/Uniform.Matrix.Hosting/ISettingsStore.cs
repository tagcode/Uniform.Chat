namespace Uniform.Matrix.Hosting;

/// <summary>Where a <see cref="SettingsEditor"/> reads and changes settings: the app's own <see cref="SettingsStore"/>, or a running server's, e.g. through its admin socket.</summary>
public interface ISettingsStore
{
    /// <summary>The settings file.</summary>
    string Path { get; }

    /// <summary>Return the current value of <paramref name="key"/>, e.g. <c>Ui:InlineImages</c>, or null when only the built-in default applies.</summary>
    string? GetValue(string key);

    /// <summary>Return which layer sets <paramref name="key"/>: <c>applied</c>, <c>command line</c>, <c>environment</c>, a file name, or <c>default</c>.</summary>
    string GetSource(string key);

    /// <summary>Apply <paramref name="changes"/> until the next start; a null value clears an applied change, so lower layers show again.</summary>
    /// <exception cref="IOException">When the store cannot be reached.</exception>
    void Apply(IReadOnlyDictionary<string, string?> changes);

    /// <summary>Write <paramref name="changes"/> into the settings file, keeping its other settings; a null value removes the setting. The changes are applied too.</summary>
    /// <exception cref="IOException">When the file cannot be written, or the store cannot be reached.</exception>
    void Save(IReadOnlyDictionary<string, string?> changes);
}
