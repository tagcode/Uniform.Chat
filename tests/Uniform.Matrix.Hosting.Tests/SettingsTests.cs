namespace Uniform.Matrix.Hosting.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

/// <summary>Tests of <see cref="OptionCatalog"/>, <see cref="SettingsStore"/> and <see cref="SettingsEditor"/>.</summary>
public sealed class SettingsTests : IDisposable
{
    private static readonly OptionCatalog Catalog = new([new OptionSection("Matrix", typeof(SampleMatrixOptions)), new OptionSection("Ui", typeof(SampleUiOptions))]);
    private readonly string directory = Directory.CreateTempSubdirectory("matrix-settings-").FullName;

    /// <summary>Path of the settings file of this test.</summary>
    private string SettingsPath => Path.Combine(directory, "settings.json");

    /// <summary>Build configuration like the app: base values, the settings file, the command line, then applied changes.</summary>
    private (IConfigurationRoot Configuration, SettingsStore Store) Build(params string[] args)
    {
        // Layers in app order
        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Ui:DarkBackground"] = "true", ["Logging:LogLevel:Default"] = "Information", ["Logging:LogLevel:Microsoft"] = "Warning" });
        builder.AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true);
        builder.AddCommandLine(args);
        AppliedConfigurationProvider applied = SettingsStore.AddLayers(builder, SettingsPath);
        IConfigurationRoot configuration = builder.Build();
        // Return configuration and store
        return (configuration, new SettingsStore(configuration, applied, SettingsPath));
    }

    /// <summary>Every option has a description from the XML documentation, a default, and the right flags.</summary>
    [Fact]
    public void CanListOptionsWithDescriptions()
    {
        // Act
        IReadOnlyList<OptionInfo> options = Catalog.GetAll(Build().Configuration);

        // Assert
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description), o.Key));
        OptionInfo timeout = options.Single(o => o.Key == "Matrix:SyncTimeout");
        Assert.Equal("00:00:30", timeout.DefaultValue);
        Assert.True(timeout.RestartRequired);
        Assert.True(options.Single(o => o.Key == "Matrix:Password").Secret);
        Assert.Equal("Room to open at start, see SampleMatrixOptions.", options.Single(o => o.Key == "Ui:StartRoom").Description);
        Assert.False(options.Single(o => o.Key == "Ui:InlineImages").RestartRequired);
        Assert.Contains("Ascii", options.Single(o => o.Key == "Ui:ImagePreviewStyle").Choices);
        Assert.Contains(options, o => o.Key == "Logging:LogLevel:Microsoft");
        Assert.DoesNotContain("<", string.Concat(options.Select(o => o.Description)));
    }

    /// <summary>Values are checked and normalized by type.</summary>
    [Theory]
    [InlineData("Matrix:SyncTimeout", "0:1:0", "00:01:00", null)]
    [InlineData("Matrix:SyncTimeout", "soon", null, "Not a valid TimeSpan. Use hh:mm:ss, e.g. 00:00:30.")]
    [InlineData("Matrix:TimelineLimit", "x", null, "Not a valid Int32.")]
    [InlineData("Ui:InlineImages", "False", "false", null)]
    [InlineData("Ui:ImagePreviewStyle", "braille", "Braille", null)]
    [InlineData("Ui:StartRoom", "", null, null)]
    [InlineData("Matrix:TimelineLimit", "", null, "A value is required.")]
    public void CanParseValues(string key, string text, string? value, string? error)
    {
        // Act
        OptionInfo option = Catalog.GetAll(Build().Configuration).Single(o => o.Key == key);
        (string? parsed, string? message) = option.Parse(text);

        // Assert
        Assert.Equal(value, parsed);
        Assert.Equal(error, message);
    }

    /// <summary>Applied changes show at once, above the command line; saved ones go to the settings file, nested, and the file keeps its other settings.</summary>
    [Fact]
    public void CanApplyAndSave()
    {
        // Arrange: a setting in the file already, and one on the command line
        File.WriteAllText(SettingsPath, """{ "Ui": { "StartRoom": "#home:hs" } }""");
        (IConfigurationRoot configuration, SettingsStore store) = Build("--Matrix:TimelineLimit=10");
        Assert.Equal("settings.json", store.GetSource("Ui:StartRoom"));
        Assert.Equal("command line", store.GetSource("Matrix:TimelineLimit"));
        Assert.Equal("default", store.GetSource("Ui:ImagePreviewRows"));

        // Act: apply
        store.Apply(new Dictionary<string, string?> { ["Ui:DarkBackground"] = "false" });

        // Assert
        Assert.Equal("false", configuration["Ui:DarkBackground"]);
        Assert.Equal("applied", store.GetSource("Ui:DarkBackground"));

        // Act: save, with a number and a removal
        store.Save(new Dictionary<string, string?> { ["Ui:DarkBackground"] = "false", ["Ui:ImagePreviewRows"] = "12", ["Ui:StartRoom"] = null });

        // Assert file content and sources
        JsonObject file = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.False(file["Ui"]!["DarkBackground"]!.GetValue<bool>());
        Assert.Equal(12, file["Ui"]!["ImagePreviewRows"]!.GetValue<int>());
        Assert.Null(file["Ui"]!["StartRoom"]);
        Assert.Equal("settings.json", store.GetSource("Ui:DarkBackground"));
        Assert.Null(configuration["Ui:StartRoom"]);
    }

    /// <summary>The editor keeps edits until applied, then until saved, and reports options that need a restart.</summary>
    [Fact]
    public void CanEditApplyAndSave()
    {
        // Arrange
        (IConfigurationRoot configuration, SettingsStore store) = Build();
        SettingsEditor editor = new(store, Catalog.GetAll(configuration));
        OptionInfo rows = editor.Options.Single(o => o.Key == "Ui:ImagePreviewRows");
        OptionInfo limit = editor.Options.Single(o => o.Key == "Matrix:TimelineLimit");

        // Act: edit, with one bad value
        Assert.Null(editor.Set(rows, "8"));
        Assert.Null(editor.Set(limit, "30"));
        Assert.NotNull(editor.Set(limit, "many"));

        // Assert edited, not yet live
        Assert.Equal(OptionChange.Edited, editor.GetChange(rows));
        Assert.Equal("30", editor.GetValue(limit));
        Assert.Null(configuration["Ui:ImagePreviewRows"]);

        // Act and assert: apply
        Assert.Equal(["Matrix:TimelineLimit"], editor.Apply());
        Assert.Equal(OptionChange.Applied, editor.GetChange(rows));
        Assert.Equal("8", configuration["Ui:ImagePreviewRows"]);
        Assert.True(editor.HasUnsavedChanges);

        // Act and assert: save
        editor.Save();
        Assert.False(editor.HasUnsavedChanges);
        Assert.Equal("8", SettingsStore.Read(SettingsPath)["Ui:ImagePreviewRows"]);
        Assert.Equal("settings.json", editor.GetSource(rows));
    }

    /// <summary>Reset undoes one step per call: the edit, then the applied change, then the saved value on the next save.</summary>
    [Fact]
    public void ResetStepByStep()
    {
        // Arrange: a saved value
        (IConfigurationRoot configuration, SettingsStore store) = Build();
        SettingsEditor editor = new(store, Catalog.GetAll(configuration));
        OptionInfo rows = editor.Options.Single(o => o.Key == "Ui:ImagePreviewRows");
        editor.Set(rows, "8");
        editor.Save();

        // Act and assert: an edit is discarded
        editor.Set(rows, "5");
        Assert.Equal(OptionChange.Edited, editor.Reset(rows));
        Assert.Equal("8", editor.GetValue(rows));
        Assert.Equal(OptionChange.None, editor.GetChange(rows));
        Assert.False(editor.HasUnsavedChanges);

        // Act and assert: an applied change is undone at once
        editor.Set(rows, "6");
        editor.Apply();
        Assert.Equal("6", configuration["Ui:ImagePreviewRows"]);
        Assert.Equal(OptionChange.Applied, editor.Reset(rows));
        Assert.Equal("8", configuration["Ui:ImagePreviewRows"]);
        Assert.Equal("8", editor.GetValue(rows));
        Assert.False(editor.HasUnsavedChanges);

        // Act and assert: then the saved value is removed on save
        Assert.Equal(OptionChange.None, editor.Reset(rows));
        Assert.Equal(OptionChange.Edited, editor.GetChange(rows));
        editor.Save();
        Assert.False(SettingsStore.Read(SettingsPath).ContainsKey("Ui:ImagePreviewRows"));
        Assert.Null(configuration["Ui:ImagePreviewRows"]);
    }

    /// <summary>Secrets are neither shown, edited, loaded nor exported; loading takes known keys as edits.</summary>
    [Fact]
    public void CanLoadAndExportWithoutSecrets()
    {
        // Arrange
        (IConfigurationRoot configuration, SettingsStore store) = Build("--Matrix:Password=hunter2");
        SettingsEditor editor = new(store, Catalog.GetAll(configuration));
        OptionInfo password = editor.Options.Single(o => o.Key == "Matrix:Password");
        string other = Path.Combine(directory, "other.json");
        File.WriteAllText(other, """{ "Ui": { "InlineImages": false, "Unknown": 1 }, "Matrix": { "Password": "x" } }""");

        // Act
        int loaded = editor.Load(other);
        string exported = Path.Combine(directory, "export.json");
        editor.Export(exported);

        // Assert
        Assert.Equal("(hidden)", editor.GetDisplayValue(password));
        Assert.NotNull(editor.Set(password, "new"));
        Assert.Equal(1, loaded);
        Assert.Equal("false", editor.GetValue(editor.Options.Single(o => o.Key == "Ui:InlineImages")));
        IReadOnlyDictionary<string, string?> written = SettingsStore.Read(exported);
        Assert.Equal("false", written["Ui:InlineImages"], ignoreCase: true);
        Assert.False(written.ContainsKey("Matrix:Password"));
    }

    /// <summary>Remove the test directory.</summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
