namespace Uniform.Matrix.Docs.Tests;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Uniform.Matrix.Client;
using Uniform.Chat.Terminal.Configuration;
using Uniform.Matrix.Hosting;
using Uniform.Matrix.Server;
using Uniform.Chat.Server;

/// <summary>The settings tables of the user guides match the options the apps register: every setting is listed, no row is for a setting that does not exist, and the defaults agree.</summary>
public partial class SettingsTableTests
{
    /// <summary>The terminal client's parameter table lists its settings and their defaults.</summary>
    [Fact]
    public void MatchTerminalSettings()
    {
        // Arrange: the client's sections as the app registers them
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Uniform.Matrix.Docs.Tests" });
        builder.AddMatrixClient();
        builder.Services.AddOptionSection<UiOptions>(builder.Configuration, UiOptions.Section);
        builder.Services.AddOptionSection<AudioOptions>(builder.Configuration, AudioOptions.Section);

        // Act and assert
        AssertTable("user-docs/Uniform.Chat.Terminal/articles/settings.md", Options(builder.Services, builder.Configuration));
    }

    /// <summary>The server's settings table lists its settings and their defaults; its <c>Urls</c> row, read by ASP.NET Core itself, has no section and is not compared.</summary>
    [Fact]
    public void MatchServerSettings()
    {
        // Arrange: the server's sections as the host registers them, with a data directory of its own
        string directory = Directory.CreateTempSubdirectory("docs-tests-").FullName;
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Matrix.Docs.Tests" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = directory });
            builder.AddMatrixServer();
            builder.Services.AddOptionSection<ServerHostOptions>(builder.Configuration, ServerHostOptions.Section);

            // Act and assert
            AssertTable("user-docs/Uniform.Chat.Server/articles/settings.md", Options(builder.Services, new ConfigurationBuilder().Build()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Return the options of the registered sections and the default log level, as the settings windows list them.</summary>
    private static IReadOnlyList<OptionInfo> Options(IServiceCollection services, IConfiguration configuration)
    {
        // Sections from the registrations
        List<OptionSection> sections = [.. services.Where(s => s.ServiceType == typeof(OptionSection)).Select(s => (OptionSection)s.ImplementationInstance!)];
        // Return options without per-category log levels
        return [.. new OptionCatalog(sections).GetAll(configuration).Where(o => !o.Key.StartsWith("Logging:", StringComparison.Ordinal) || o.Key == "Logging:LogLevel:Default")];
    }

    /// <summary>Assert that the table rows of <paramref name="guide"/> and <paramref name="options"/> name the same settings with the same defaults.</summary>
    /// <param name="guide">Guide page with the table, from the repository root.</param>
    /// <param name="options">Settings of the app.</param>
    private static void AssertTable(string guide, IReadOnlyList<OptionInfo> options)
    {
        // Rows: key and default cell
        Dictionary<string, string> rows = [];
        foreach (Match row in RowPattern().Matches(Repository.Read(guide))) rows[row.Groups[1].Value] = row.Groups[2].Value.Trim();

        // Same settings both ways
        List<string> undocumented = [.. options.Select(o => o.Key).Where(k => !rows.ContainsKey(k))];
        List<string> unknown = [.. rows.Keys.Where(k => options.All(o => o.Key != k))];
        Assert.True(undocumented.Count == 0, $"Not in {guide}: {string.Join(", ", undocumented)}");
        Assert.True(unknown.Count == 0, $"In {guide} but not a setting: {string.Join(", ", unknown)}");

        // Defaults: a value in backticks must be the default; without one, the cell may describe what applies, e.g. a computed folder
        foreach (OptionInfo option in options)
        {
            string cell = rows[option.Key];
            Match value = ValuePattern().Match(cell);
            if (value.Success && option.DefaultValue is not null) Assert.True(value.Groups[1].Value == option.DefaultValue, $"{option.Key}: the guide says {cell}, the default is {option.DefaultValue ?? "not set"}");
            if (cell == "not set") Assert.True(string.IsNullOrEmpty(option.DefaultValue), $"{option.Key}: the guide says not set, the default is {option.DefaultValue}");
        }
    }

    /// <summary>A settings table row: the key in backticks, <c>Section:Name</c>, and the default cell.</summary>
    [GeneratedRegex(@"^\| `([A-Za-z]+(?::[A-Za-z]+)+)` \| ([^|]*) \|", RegexOptions.Multiline)]
    private static partial Regex RowPattern();

    /// <summary>A default cell that is a single value in backticks.</summary>
    [GeneratedRegex(@"^`([^`]*)`$")]
    private static partial Regex ValuePattern();
}
