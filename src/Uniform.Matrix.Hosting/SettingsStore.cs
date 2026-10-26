namespace Uniform.Matrix.Hosting;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

/// <summary>The user's settings file and changes applied while running, as layers of the app's configuration.</summary>
/// <remarks>
/// Layers from lowest: <c>appsettings.json</c>, the settings file, environment variables, the command line, then applied changes.
/// Saving writes the settings file; applying sets the top layer until the next start. Both reload the configuration, so options read through <c>IOptionsMonitor</c> see the change.
/// </remarks>
public sealed class SettingsStore
{
    private readonly IConfigurationRoot configuration;
    private readonly AppliedConfigurationProvider applied;

    /// <summary>Use <paramref name="configuration"/>, which must contain <paramref name="applied"/> and the settings file at <paramref name="path"/>.</summary>
    public SettingsStore(IConfigurationRoot configuration, AppliedConfigurationProvider applied, string path)
    {
        // Store layers
        this.configuration = configuration;
        this.applied = applied;
        Path = path;
    }

    /// <summary>The settings file.</summary>
    public string Path { get; }

    /// <summary>Return the settings file of <paramref name="applicationName"/>: <c>settings.json</c> in <see cref="AppDirectories.Config"/>.</summary>
    public static string DefaultPath(string applicationName) => System.IO.Path.Combine(AppDirectories.Config(applicationName), "settings.json");

    /// <summary>Add the settings file and the applied layer to <paramref name="builder"/>: the file right after the other JSON files, the applied layer on top.</summary>
    /// <returns>The applied layer, for <see cref="SettingsStore"/>.</returns>
    public static AppliedConfigurationProvider AddLayers(IConfigurationBuilder builder, string path)
    {
        // Settings file above appsettings.json, below environment and command line
        JsonConfigurationSource file = new() { Path = path, Optional = true, ReloadOnChange = false };
        file.ResolveFileProvider();
        int index = builder.Sources.ToList().FindLastIndex(s => s is JsonConfigurationSource) + 1;
        builder.Sources.Insert(index, file);

        // Applied changes on top
        AppliedConfigurationSource applied = new();
        builder.Add(applied);
        // Return applied layer
        return applied.Provider;
    }

    /// <summary>Return the current value of <paramref name="key"/>, e.g. <c>Ui:InlineImages</c>, or null when only the built-in default applies.</summary>
    public string? GetValue(string key) => configuration[key];

    /// <summary>Return which layer sets <paramref name="key"/>: <c>applied</c>, <c>command line</c>, <c>environment</c>, a file name, or <c>default</c>.</summary>
    public string GetSource(string key)
    {
        // Highest layer that has the key
        foreach (IConfigurationProvider provider in configuration.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _)) continue;
            return provider switch
            {
                AppliedConfigurationProvider => "applied",
                CommandLineConfigurationProvider => "command line",
                EnvironmentVariablesConfigurationProvider => "environment",
                FileConfigurationProvider file => file.Source.Path ?? "file",
                _ => provider.GetType().Name,
            };
        }
        // Done; nothing sets it
        return "default";
    }

    /// <summary>Apply <paramref name="changes"/> until the next start; a null value clears an applied change, so lower layers show again.</summary>
    public void Apply(IReadOnlyDictionary<string, string?> changes)
    {
        // Set the top layer and reload
        applied.Set(changes);
        configuration.Reload();
    }

    /// <summary>Write <paramref name="changes"/> into the settings file, keeping its other settings; a null value removes the setting. The changes are applied too.</summary>
    public void Save(IReadOnlyDictionary<string, string?> changes) => Save(changes, Path);

    /// <summary>Write <paramref name="changes"/> into <paramref name="path"/>, keeping its other settings; the changes are applied when it is the settings file.</summary>
    public void Save(IReadOnlyDictionary<string, string?> changes, string path)
    {
        // Merge into the existing file
        Dictionary<string, string?> values = new(Read(path), StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in changes)
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(values).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        // Saved values come from the file now, not the applied layer
        if (path != Path) return;
        applied.Set(changes.ToDictionary(c => c.Key, _ => (string?)null));
        configuration.Reload();
    }

    /// <summary>Read a settings file as flat keys like <c>Ui:InlineImages</c>; empty when it does not exist.</summary>
    public static IReadOnlyDictionary<string, string?> Read(string path)
    {
        // Parse with the same JSON reader as the configuration
        if (!File.Exists(path)) return new Dictionary<string, string?>();
        IConfigurationRoot file = new ConfigurationBuilder().AddJsonFile(System.IO.Path.GetFullPath(path), optional: false, reloadOnChange: false).Build();
        // Return leaf values
        return file.AsEnumerable().Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Nest flat keys into JSON objects, writing <c>true</c>, <c>false</c> and integers as JSON values.</summary>
    private static JsonObject ToJson(IReadOnlyDictionary<string, string?> values)
    {
        // One object per section
        JsonObject root = [];
        foreach ((string key, string? value) in values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            string[] parts = key.Split(':');
            JsonObject node = root;
            foreach (string part in parts[..^1])
            {
                if (node[part] is not JsonObject child) node[part] = child = [];
                node = child;
            }
            node[parts[^1]] = value switch
            {
                "true" or "True" => JsonValue.Create(true),
                "false" or "False" => JsonValue.Create(false),
                _ when long.TryParse(value, out long number) && number.ToString() == value => JsonValue.Create(number),
                _ => JsonValue.Create(value),
            };
        }
        // Return tree
        return root;
    }
}

/// <summary>Source of the <see cref="AppliedConfigurationProvider"/> layer.</summary>
public sealed class AppliedConfigurationSource : IConfigurationSource
{
    /// <summary>The single provider of this source.</summary>
    public AppliedConfigurationProvider Provider { get; } = new();

    /// <summary>Return <see cref="Provider"/>.</summary>
    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>Configuration layer of changes applied in the settings window, kept in memory until the next start.</summary>
public sealed class AppliedConfigurationProvider : ConfigurationProvider
{
    /// <summary>Set or, for null values, clears keys.</summary>
    public void Set(IReadOnlyDictionary<string, string?> changes)
    {
        // Update data; the caller reloads the configuration
        foreach ((string key, string? value) in changes)
        {
            if (value is null) Data.Remove(key);
            else Data[key] = value;
        }
    }
}
