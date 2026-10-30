namespace Uniform.Chat.Terminal.Tests;

using Uniform.Chat.Terminal.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Client;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Hosting;

/// <summary>Tests of the terminal client's option registration.</summary>
public sealed class OptionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("matrix-options-").FullName;

    /// <summary>The registered options are the client's and the terminal's, each with a description from the XML documentation; the data directory defaults by application name.</summary>
    [Fact]
    public void CanDescribeAllOptions()
    {
        // Arrange: register like Program, with logs in a temporary directory
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Uniform.Chat.Terminal" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = directory });
        builder.AddSettings();
        builder.AddMatrixClient();
        builder.Services.AddOptionSection<UiOptions>(builder.Configuration, UiOptions.Section);
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        // Act
        IReadOnlyList<OptionInfo> options = services.GetRequiredService<OptionCatalog>().GetAll(builder.Configuration);

        // Assert
        Assert.Equal(["Matrix", "Storage", "Ui", "Logging"], options.Select(o => o.Key.Split(':')[0]).Distinct());
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description), o.Key));
        Assert.Null(options.Single(o => o.Key == "Storage:DataDirectory").DefaultValue);
        Assert.EndsWith(Path.Combine("share", "uniform-chat-terminal"), new StorageOptions().ResolveDataDirectory("Uniform.Chat.Terminal"));
        Assert.EndsWith(Path.Combine("uniform-chat-terminal", "settings.json"), services.GetRequiredService<SettingsStore>().Path);
    }

    /// <summary>Remove the temporary directory.</summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
