namespace Uniform.Matrix.Hosting;

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>One configurable option, see <see cref="OptionCatalog"/>.</summary>
public sealed record OptionInfo
{
    /// <summary>Configuration key, e.g. <c>Ui:InlineImages</c>.</summary>
    public required string Key { get; init; }
    /// <summary>Value type; nullable types allow an empty value.</summary>
    public required Type Type { get; init; }
    /// <summary>Built-in default as configuration text, or null.</summary>
    public string? DefaultValue { get; init; }
    /// <summary>What the option does.</summary>
    public string Description { get; init; } = "";
    /// <summary>Whether a change takes effect only after a restart.</summary>
    public bool RestartRequired { get; init; }
    /// <summary>Whether the value is secret and not shown or saved, see <see cref="SecretAttribute"/>.</summary>
    public bool Secret { get; init; }

    /// <summary>Allowed values of enum and boolean options; empty for free text.</summary>
    public IReadOnlyList<string> Choices
    {
        get
        {
            // Enum names or booleans
            Type type = Nullable.GetUnderlyingType(Type) ?? Type;
            if (type.IsEnum) return Enum.GetNames(type);
            // Return choices
            return type == typeof(bool) ? ["true", "false"] : [];
        }
    }

    /// <summary>Return <paramref name="text"/> normalized for configuration, or an error message; empty text means unset for nullable types.</summary>
    public (string? Value, string? Error) Parse(string text)
    {
        // Empty
        Type type = Nullable.GetUnderlyingType(Type) ?? Type;
        bool nullable = Type != type || !Type.IsValueType;
        if (text.Length == 0) return nullable ? (null, null) : (null, "A value is required.");

        // Convert like the configuration binder
        try
        {
            object? value = TypeDescriptor.GetConverter(type).ConvertFromInvariantString(text);
            // Return normalized text
            return (OptionCatalog.Format(value), null);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or OverflowException || ex.InnerException is FormatException or OverflowException)
        {
            // Done; not a value of this type
            string example = type == typeof(TimeSpan) ? " Use hh:mm:ss, e.g. 00:00:30." : Choices.Count > 0 ? $" Use one of: {string.Join(", ", Choices)}." : "";
            return (null, $"Not a valid {type.Name}.{example}");
        }
    }
}

/// <summary>An options type bound to a configuration section, registered with <see cref="HostingExtensions.AddOptionSection"/>.</summary>
/// <param name="Name">Section name, e.g. <c>Ui</c>.</param>
/// <param name="Type">Options type whose settable properties are the options.</param>
public sealed record OptionSection(string Name, Type Type);

/// <summary>All options of an application: the properties of its registered <see cref="OptionSection"/>s, and logging levels.</summary>
/// <remarks>Descriptions come from the XML documentation of the option properties, so they match the API documentation; each assembly's documentation file is read once.</remarks>
public sealed partial class OptionCatalog
{
    private static readonly Dictionary<Assembly, Dictionary<string, string>> Summaries = [];
    private readonly IReadOnlyList<OptionSection> sections;

    /// <summary>List the options of <paramref name="sections"/>, in order.</summary>
    public OptionCatalog(IEnumerable<OptionSection> sections)
    {
        // Keep registration order
        this.sections = [.. sections];
    }

    /// <summary>Return all options; logging levels include the categories configured in <paramref name="configuration"/>.</summary>
    public IReadOnlyList<OptionInfo> GetAll(IConfiguration configuration)
    {
        // Option record properties
        List<OptionInfo> options = [];
        foreach ((string section, Type type) in sections)
        {
            object defaults = Activator.CreateInstance(type)!;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                options.Add(new OptionInfo
                {
                    Key = $"{section}:{property.Name}",
                    Type = property.PropertyType,
                    DefaultValue = Format(property.GetValue(defaults)),
                    Description = GetSummaries(type.Assembly).GetValueOrDefault($"P:{type.FullName}.{property.Name}", ""),
                    RestartRequired = property.GetCustomAttribute<RestartRequiredAttribute>() is not null,
                    Secret = property.GetCustomAttribute<SecretAttribute>() is not null,
                });
            }
        }

        // Logging levels: the default and each configured category
        IEnumerable<string> categories = configuration.GetSection("Logging:LogLevel").GetChildren().Select(c => c.Key).Where(k => k != "Default").Order(StringComparer.Ordinal);
        options.Add(new OptionInfo { Key = "Logging:LogLevel:Default", Type = typeof(LogLevel), DefaultValue = nameof(LogLevel.Information), Description = "Lowest level written to the log for categories without their own setting." });
        foreach (string category in categories)
            options.Add(new OptionInfo { Key = $"Logging:LogLevel:{category}", Type = typeof(LogLevel), DefaultValue = null, Description = $"Lowest level written to the log for categories starting with {category}." });
        // Return options
        return options;
    }

    /// <summary>Format a value as configuration text: invariant culture, lowercase booleans, <c>c</c> format for time spans.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Return the property summaries of <paramref name="assembly"/>, reading them on first use.</summary>
    private static Dictionary<string, string> GetSummaries(Assembly assembly)
    {
        // One read per assembly
        lock (Summaries)
        {
            if (!Summaries.TryGetValue(assembly, out Dictionary<string, string>? summaries)) Summaries[assembly] = summaries = LoadSummaries(assembly);
            return summaries;
        }
    }

    /// <summary>Read property summaries from an assembly's XML documentation file in the application directory, as plain text.</summary>
    private static Dictionary<string, string> LoadSummaries(Assembly assembly)
    {
        // Assert the documentation file; by name in the application directory, as single-file apps have no assembly location
        string path = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
        if (!File.Exists(path)) return [];

        // Summary of each member
        XDocument document = XDocument.Load(path);
        // Return summaries by member ID
        return document.Descendants("member")
            .Where(m => m.Attribute("name")?.Value.StartsWith("P:") == true && m.Element("summary") is not null)
            .ToDictionary(m => m.Attribute("name")!.Value, m => ToText(m.Element("summary")!));
    }

    /// <summary>Convert a summary element to text: references become their short names, code its text.</summary>
    private static string ToText(XElement summary)
    {
        // Each node in order
        string text = string.Concat(summary.Nodes().Select(node => node switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" or "seealso" } e when e.Attribute("cref") is XAttribute cref => ShortName(cref.Value),
            XElement { Name.LocalName: "see" } e when e.Attribute("href") is XAttribute href => e.Value.Length > 0 ? e.Value : href.Value,
            XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? "",
            XElement e => e.Value,
            _ => "",
        }));
        // Return text on one line
        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>Return the readable part of a documentation ID: <c>T:A.B.Type</c> becomes <c>Type</c>, <c>P:A.B.Type.Member</c> becomes <c>Type.Member</c>.</summary>
    private static string ShortName(string id)
    {
        // Drop the kind prefix and namespace
        string[] parts = id[(id.IndexOf(':') + 1)..].Split('(')[0].Split('.');
        // Return last one or two parts
        return id.StartsWith("T:") ? parts[^1] : string.Join('.', parts[^2..]);
    }

    /// <summary>Run of whitespace.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
