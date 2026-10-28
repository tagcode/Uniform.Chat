namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>A license text the app carries: its group, e.g. <c>Packages</c>, and name.</summary>
/// <param name="Group">Group: the app itself, runtime, packages or standard license texts.</param>
/// <param name="Name">Name, e.g. a package ID.</param>
/// <param name="ResourceName">Embedded resource holding the text.</param>
public sealed record LicenseEntry(string Group, string Name, string ResourceName)
{
    /// <summary>Name with its group, for the list.</summary>
    public string Title => Group.Length == 0 ? Name : $"{Name} ({Group.ToLowerInvariant()})";
}

/// <summary>The licenses of the app and of everything it bundles, as the licenses of the bundled libraries require it to show: the list, and the text of the chosen one.</summary>
public sealed partial class LicensesViewModel : ViewModelBase
{
    private const string Prefix = "Licenses/";
    private const string OwnLicense = "Uniform.Matrix";
    private static readonly string[] GroupOrder = ["", "Runtime", "Packages", "License texts"];

    /// <summary>Load the list from the app's embedded license texts; the app's own license and the notices are chosen first.</summary>
    public LicensesViewModel()
    {
        // Entries by group, the app's own first, then by name
        Entries = [.. typeof(LicensesViewModel).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(ToEntry)
            .OrderBy(e => Array.IndexOf(GroupOrder, e.Group))
            .ThenBy(e => e.Name == OwnLicense ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        Selected = Entries.FirstOrDefault();
    }

    /// <summary>The license texts.</summary>
    public IReadOnlyList<LicenseEntry> Entries { get; }

    /// <summary>The chosen license.</summary>
    [ObservableProperty]
    public partial LicenseEntry? Selected { get; set; }

    /// <summary>The chosen license's text.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Show the chosen license's text.</summary>
    partial void OnSelectedChanged(LicenseEntry? value) => Text = value is null ? "" : Read(value.ResourceName);

    /// <summary>Return the entry of a resource name such as <c>Licenses/Packages/Avalonia</c>.</summary>
    private static LicenseEntry ToEntry(string resourceName)
    {
        // Group and name after the prefix
        string[] parts = resourceName[Prefix.Length..].Split('/', 2);
        // Return entry
        return parts.Length == 2 ? new LicenseEntry(parts[0], parts[1], resourceName) : new LicenseEntry("", parts[0], resourceName);
    }

    /// <summary>Return the text of an embedded resource.</summary>
    private static string Read(string resourceName)
    {
        // As UTF-8 text
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"No resource {resourceName}.");
        using StreamReader reader = new(stream);
        // Return text
        return reader.ReadToEnd();
    }
}
