// Third-party notices of the apps: the license text of every package an app bundles, and of the .NET runtimes, under LICENSES/,
// and the package table of THIRD-PARTY-NOTICES.md. Run from the repository root after a restore:
//   dotnet run tools/ThirdPartyNotices.cs
// Uniform.Matrix.Docs.Tests fails when a bundled package has no license file here, e.g. after an update; run this again then.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

return ThirdPartyNotices.Run(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());

/// <summary>Collects the license texts of what the apps bundle.</summary>
static partial class ThirdPartyNotices
{
    /// <summary>The apps by project, with how the notices name them.</summary>
    private static readonly (string Project, string Name)[] Apps =
    [
        ("Uniform.Chat.Terminal", "terminal client"),
        ("Uniform.Chat.Desktop", "desktop client"),
        ("Uniform.Chat.Android", "Android client"),
        ("Uniform.Chat.Server", "server"),
    ];

    /// <summary>Runtime packs bundled in self-contained apps, with the apps that bundle them.</summary>
    private static readonly (string Pack, string UsedBy)[] Runtimes =
    [
        ("microsoft.netcore.app.runtime.linux-x64", "all desktop apps"),
        ("microsoft.aspnetcore.app.runtime.linux-x64", "server"),
        ("Microsoft.NETCore.App.Runtime.Mono.android-arm64", "Android client"),
        ("Microsoft.Android.Runtime.Mono.36.android-arm64", "Android client"),
    ];

    private const string TableStart = "<!-- packages:start -->";
    private const string TableEnd = "<!-- packages:end -->";

    /// <summary>A bundled package: its ID, version, and the apps that bundle it.</summary>
    private sealed record Package(string Id, string Version, SortedSet<string> UsedBy);

    /// <summary>Write the license files and the table; return the exit code.</summary>
    public static int Run(string root)
    {
        // Packages of the apps, from their restore output
        string nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        SortedDictionary<string, Package> packages = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string project, string name) in Apps)
        {
            string assets = Path.Combine(root, "src", project, "obj", "project.assets.json");
            if (!File.Exists(assets))
            {
                Console.Error.WriteLine($"{assets} is missing; restore the solution first.");
                return 1;
            }
            foreach ((string id, string version) in BundledPackages(assets))
            {
                if (!packages.TryGetValue(id, out Package? package)) packages[id] = package = new Package(id, version, []);
                package.UsedBy.Add(name);
            }
        }

        // One license file per package, stale ones removed
        string folder = Path.Combine(root, "LICENSES", "packages");
        Directory.CreateDirectory(folder);
        foreach (string stale in Directory.GetFiles(folder, "*.txt").Where(f => !packages.ContainsKey(Path.GetFileNameWithoutExtension(f)))) File.Delete(stale);
        List<string> rows = [];
        Dictionary<string, string> firstWithBody = [];
        foreach (Package package in packages.Values)
        {
            // A text several packages share, such as the notices of all SkiaSharp native packages, is kept once
            (string header, string body, string license) = LicenseText(root, nuget, package);
            string text = firstWithBody.TryGetValue(body, out string? first) ? $"{header}\nThe license text is the same as that of {first}; see {first}.txt.\n" : header + body;
            firstWithBody.TryAdd(body, package.Id);
            File.WriteAllText(Path.Combine(folder, package.Id + ".txt"), text);
            rows.Add($"| {package.Id} | {package.Version} | {string.Join(", ", package.UsedBy)} | [{license}](LICENSES/packages/{package.Id}.txt) |");
        }

        // The runtimes' own license and notices
        string runtimes = Path.Combine(root, "LICENSES", "runtime");
        Directory.CreateDirectory(runtimes);
        List<string> runtimeRows = [];
        foreach ((string pack, string usedBy) in Runtimes)
        {
            if (FindPack(nuget, pack) is not string directory)
            {
                Console.Error.WriteLine($"Runtime pack {pack} not found; build the apps for release first.");
                return 1;
            }
            string version = Path.GetFileName(directory);
            StringBuilder text = new($"{pack} {version}\nUsed by: {usedBy}\n");
            foreach (string file in Directory.GetFiles(directory).Where(IsLicenseFile).Order(StringComparer.Ordinal)) text.Append($"\n===== {Path.GetFileName(file)} =====\n\n").Append(File.ReadAllText(file));
            File.WriteAllText(Path.Combine(runtimes, pack + ".txt"), text.ToString());
            runtimeRows.Add($"| {pack} | {version} | {usedBy} | [MIT, with third-party notices](LICENSES/runtime/{pack}.txt) |");
        }

        // The table in the notices
        string notices = Path.Combine(root, "THIRD-PARTY-NOTICES.md");
        string current = File.ReadAllText(notices);
        int start = current.IndexOf(TableStart, StringComparison.Ordinal), end = current.IndexOf(TableEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            Console.Error.WriteLine($"{notices} has no {TableStart} … {TableEnd} section.");
            return 1;
        }
        string table = string.Join('\n', ["", "| Runtime | Version | Used by | License |", "|---|---|---|---|", .. runtimeRows, "", "| Package | Version | Used by | License |", "|---|---|---|---|", .. rows, ""]);
        File.WriteAllText(notices, current[..(start + TableStart.Length)] + table + current[end..]);
        // Done
        Console.WriteLine($"{packages.Count} packages and {runtimeRows.Count} runtimes written.");
        return 0;
    }

    /// <summary>Return the packages a project bundles: those with runtime or native files in its plain target framework.</summary>
    private static IEnumerable<(string Id, string Version)> BundledPackages(string assetsFile)
    {
        // Libraries of the targets without a runtime identifier
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
        foreach (JsonProperty target in assets.RootElement.GetProperty("targets").EnumerateObject().Where(t => !t.Name.Contains('/')))
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package") continue;
                bool hasFiles = new[] { "runtime", "native", "runtimeTargets" }.Any(kind => library.Value.TryGetProperty(kind, out JsonElement files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("/_._", StringComparison.Ordinal)));
                if (!hasFiles) continue;
                string[] parts = library.Name.Split('/');
                yield return (parts[0], parts[1]);
            }
        }
    }

    /// <summary>Return a package's header (version, project, license, copyright), its license body (an override, else the package's own license files, else the declared license's standard text with the package's copyright), and the license name to show.</summary>
    private static (string Header, string Body, string License) LicenseText(string root, string nuget, Package package)
    {
        // What the package declares
        string directory = Path.Combine(nuget, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
        string nuspec = File.ReadAllText(Directory.GetFiles(directory, "*.nuspec").Single());
        string? expression = Regex.Match(nuspec, @"<license type=""expression"">([^<]+)</license>") is { Success: true } e ? e.Groups[1].Value : null;
        string? copyright = Regex.Match(nuspec, @"<copyright>([^<]*)</copyright>") is { Success: true } c && c.Groups[1].Value.Trim().Length > 0 ? System.Net.WebUtility.HtmlDecode(c.Groups[1].Value.Trim()) : null;
        string? project = Regex.Match(nuspec, @"<projectUrl>([^<]+)</projectUrl>") is { Success: true } p ? System.Net.WebUtility.HtmlDecode(p.Groups[1].Value) : null;
        string license = expression ?? "see file";
        StringBuilder text = new($"{package.Id} {package.Version}\n");
        if (project is not null) text.Append($"Project: {project}\n");
        text.Append($"License: {license}\nUsed by: {string.Join(", ", package.UsedBy)}\n");
        if (copyright is not null) text.Append($"Copyright: {copyright}\n");
        StringBuilder body = new();

        // Override, else the package's files, else the standard text
        string overrideFile = Path.Combine(root, "LICENSES", "overrides", package.Id + ".txt");
        string[] own = Directory.GetFiles(directory).Where(IsLicenseFile).Order(StringComparer.Ordinal).ToArray();
        if (File.Exists(overrideFile)) body.Append("\n").Append(File.ReadAllText(overrideFile));
        else if (own.Length > 0) foreach (string file in own) body.Append($"\n===== {Path.GetFileName(file)} =====\n\n").Append(File.ReadAllText(file));
        else if (expression is not null) foreach (string id in SpdxIds(expression)) body.Append($"\n===== {id} =====\n\n").Append(StandardText(root, id, copyright));
        else throw new InvalidOperationException($"{package.Id} {package.Version} declares no license; add LICENSES/overrides/{package.Id}.txt.");
        // Return header, body and name
        return (text.ToString(), body.ToString(), license);
    }

    /// <summary>Return the SPDX license IDs of an expression such as <c>MIT AND Apache-2.0</c>.</summary>
    private static IEnumerable<string> SpdxIds(string expression) =>
        expression.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("AND" or "OR" or "WITH")).Distinct();

    /// <summary>Return the standard text of a license from LICENSES/, with the copyright line filled in when the text has a placeholder for it.</summary>
    private static string StandardText(string root, string spdxId, string? copyright)
    {
        // The template, which must exist
        string path = Path.Combine(root, "LICENSES", spdxId + ".txt");
        if (!File.Exists(path)) throw new InvalidOperationException($"No standard text for {spdxId}; add LICENSES/{spdxId}.txt.");
        string text = File.ReadAllText(path);
        // Return it with the package's copyright, when known
        return copyright is null ? text : PlaceholderCopyright().Replace(text, copyright);
    }

    /// <summary>Return the directory of the newest version of a pack in the NuGet cache or the SDK's packs, or null.</summary>
    private static string? FindPack(string nuget, string pack)
    {
        // Both places, newest version
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "packs", pack);
        IEnumerable<string> versions = new[] { Path.Combine(nuget, pack.ToLowerInvariant()), dotnet }.Where(Directory.Exists).SelectMany(Directory.GetDirectories);
        // Return the newest
        return versions.Where(v => Version.TryParse(Path.GetFileName(v).Split('-')[0], out _)).OrderByDescending(v => Version.Parse(Path.GetFileName(v).Split('-')[0])).FirstOrDefault();
    }

    /// <summary>Return whether a file name is one of a license, notice or third-party notices.</summary>
    private static bool IsLicenseFile(string path) => LicenseFileName().IsMatch(Path.GetFileName(path));

    /// <summary>File names of license and notice files.</summary>
    [GeneratedRegex(@"^(licen[cs]e|copying|notice|third.?party)", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseFileName();

    /// <summary>The copyright line of a standard license text, e.g. <c>Copyright (c) &lt;year&gt; &lt;copyright holders&gt;</c>.</summary>
    [GeneratedRegex(@"Copyright \(c\) <year> <[^>]+>")]
    private static partial Regex PlaceholderCopyright();
}
