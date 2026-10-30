namespace Uniform.Matrix.Docs.Tests;

using System.Text.Json;

/// <summary>Every package an app bundles has its license text under <c>LICENSES/packages/</c>, for the version bundled, and a row in <c>THIRD-PARTY-NOTICES.md</c>; <c>dotnet run tools/ThirdPartyNotices.cs</c> writes them.</summary>
public class ThirdPartyNoticesTests
{
    /// <summary>A bundled package without its license file, or with one of another version, fails with the command that fixes it.</summary>
    [Theory]
    [InlineData("Uniform.Chat.Terminal")]
    [InlineData("Uniform.Chat.Desktop")]
    [InlineData("Uniform.Chat.Android")]
    [InlineData("Uniform.Chat.Server")]
    public void ListBundledPackages(string app)
    {
        // Arrange: the packages the app restores with files that ship
        string assetsFile = Path.Combine(Repository.Root, "src", app, "obj", "project.assets.json");
        if (!File.Exists(assetsFile)) Assert.Skip($"{app} is not restored here.");
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
        string notices = Repository.Read("THIRD-PARTY-NOTICES.md");
        List<string> problems = [];

        // Act: a license file of that version, and a table row, for each
        foreach (JsonProperty target in assets.RootElement.GetProperty("targets").EnumerateObject().Where(t => !t.Name.Contains('/')))
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package") continue;
                if (!new[] { "runtime", "native", "runtimeTargets" }.Any(kind => library.Value.TryGetProperty(kind, out JsonElement files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal)))) continue;
                string[] parts = library.Name.Split('/');
                string file = Path.Combine(Repository.Root, "LICENSES", "packages", parts[0] + ".txt");
                if (!File.Exists(file) || File.ReadLines(file).First() != $"{parts[0]} {parts[1]}") problems.Add(library.Name);
                else if (!notices.Contains($"| {parts[0]} | {parts[1]} |", StringComparison.Ordinal)) problems.Add($"{library.Name} (not in the table)");
            }
        }

        // Assert
        Assert.True(problems.Count == 0, $"Run `dotnet run tools/ThirdPartyNotices.cs`; no current license file for: {string.Join(", ", problems.Distinct())}");
    }
}
