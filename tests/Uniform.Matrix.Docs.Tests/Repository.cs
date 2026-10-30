namespace Uniform.Matrix.Docs.Tests;

using System.Text.RegularExpressions;

/// <summary>Files of the repository the tests check: found by walking up from the test binaries to <c>Uniform.Chat.slnx</c>.</summary>
internal static partial class Repository
{
    /// <summary>Get the repository root.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Return the text of a file under <see cref="Root"/>.</summary>
    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>Return the front matter fields of a Markdown file, or an empty dictionary without front matter.</summary>
    public static IReadOnlyDictionary<string, string> FrontMatter(string text)
    {
        // Between the opening and closing dashes
        Match header = FrontMatterPattern().Match(text);
        if (!header.Success) return new Dictionary<string, string>();
        // Return name: value lines
        return header.Groups[1].Value.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
    }

    /// <summary>Return the first level-one heading of Markdown text, or null.</summary>
    public static string? Heading(string text) => HeadingPattern().Match(text) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    /// <summary>Return the root directory, which has the solution file.</summary>
    private static string FindRoot()
    {
        // Up from the binaries
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Uniform.Chat.slnx"))) return directory.FullName;
        }
        // On error no repository
        throw new InvalidOperationException("Uniform.Chat.slnx not found above the test binaries.");
    }

    /// <summary>Front matter at the start of a file.</summary>
    [GeneratedRegex(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatterPattern();

    /// <summary>A level-one heading line.</summary>
    [GeneratedRegex(@"^# (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();
}
