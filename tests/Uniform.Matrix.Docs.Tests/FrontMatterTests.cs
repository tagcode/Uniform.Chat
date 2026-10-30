namespace Uniform.Matrix.Docs.Tests;

using System.Text.RegularExpressions;

/// <summary>Every article of the documentation sites has a <c>title</c> equal to its heading, except the short titles of the user guides' front pages, and a <c>uid</c> ending in <c>.Article</c>, unique within its site.</summary>
public partial class FrontMatterTests
{
    /// <summary>The sites and their article files, from the repository root: the developer site, and each user guide's front page and articles.</summary>
    public static TheoryData<string> Sites => ["docs", "user-docs/Uniform.Chat.Desktop", "user-docs/Uniform.Chat.Android", "user-docs/Uniform.Chat.Terminal", "user-docs/Uniform.Chat.Server"];

    /// <summary>Each article of the site has front matter that follows the rules.</summary>
    [Theory]
    [MemberData(nameof(Sites))]
    public void CheckArticles(string site)
    {
        // Arrange: Markdown files of the site, without generated ones
        string root = Path.Combine(Repository.Root, site);
        List<string> articles = [.. Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_site{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}api{Path.DirectorySeparatorChar}"))];

        // Act: problems of each article
        List<string> problems = [];
        Dictionary<string, string> uids = [];
        foreach (string article in articles)
        {
            string name = Path.GetRelativePath(Repository.Root, article);
            string text = File.ReadAllText(article);
            IReadOnlyDictionary<string, string> header = Repository.FrontMatter(text);
            if (!header.TryGetValue("uid", out string? uid) || !header.TryGetValue("title", out string? title))
            {
                problems.Add($"{name}: no uid or title");
                continue;
            }
            if (!uid.EndsWith(".Article", StringComparison.Ordinal)) problems.Add($"{name}: uid {uid} does not end in .Article");
            if (!uids.TryAdd(uid, name)) problems.Add($"{name}: uid {uid} is also in {uids[uid]}");
            string? heading = Repository.Heading(text) ?? IncludedHeading(article, text);
            if (heading != title && !IsGuideFrontPage(site, article)) problems.Add($"{name}: title \"{title}\" is not the heading \"{heading}\"");
        }

        // Assert
        Assert.NotEmpty(articles);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Return whether <paramref name="article"/> is the front page of a user guide, whose title is a short name for the navigation and may differ from its heading.</summary>
    private static bool IsGuideFrontPage(string site, string article) => site.StartsWith("user-docs/", StringComparison.Ordinal) && Path.GetFileName(article) == "index.md" && Path.GetDirectoryName(article) == Path.Combine(Repository.Root, site);

    /// <summary>Return the heading of the file an article only includes, as the Android guide's pages do, or null.</summary>
    private static string? IncludedHeading(string article, string text)
    {
        // The included file, relative to the article
        Match include = IncludePattern().Match(text);
        if (!include.Success) return null;
        // Return its heading
        return Repository.Heading(File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(article)!, include.Groups[1].Value))));
    }

    /// <summary>A DocFX include of another Markdown file.</summary>
    [GeneratedRegex(@"\[!include\[[^\]]*\]\(([^)]+)\)\]")]
    private static partial Regex IncludePattern();
}
