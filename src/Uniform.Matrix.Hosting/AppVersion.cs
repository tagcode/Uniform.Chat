namespace Uniform.Matrix.Hosting;

using System.Reflection;

/// <summary>The version of an app as people see it, e.g. <c>0.1.36 (6078a19)</c>: the release's version and the commit it was built from, as the build stamps them into the assembly.</summary>
public static class AppVersion
{
    /// <summary>Return the version of <paramref name="assembly"/>: its informational version, with the commit cut to seven characters; its assembly version when it has none.</summary>
    public static string Of(Assembly assembly)
    {
        // Informational version, e.g. 0.1.36+6078a19164ce…
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return assembly.GetName().Version?.ToString(3) ?? "unknown";
        int plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        string commit = informational[(plus + 1)..];
        // Return version and short commit
        return $"{informational[..plus]} ({(commit.Length > 7 ? commit[..7] : commit)})";
    }

    /// <summary>Return the name and version of the running app, e.g. <c>Uniform.Chat.Server 0.1.36 (6078a19)</c>, from the entry assembly; from <paramref name="fallback"/> where there is none, as on Android.</summary>
    public static string Describe(Assembly? fallback = null)
    {
        // The entry assembly, else the given one
        Assembly? assembly = Assembly.GetEntryAssembly() ?? fallback;
        // Return name and version
        return assembly is null ? "unknown" : $"{assembly.GetName().Name} {Of(assembly)}";
    }
}
