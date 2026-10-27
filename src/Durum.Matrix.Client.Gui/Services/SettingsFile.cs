namespace Durum.Matrix.Client.Gui.Services;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Reads and merges the app's <c>settings.json</c>, keeping sections other parts of the app wrote.</summary>
internal static class SettingsFile
{
    /// <summary>Return the settings file at <paramref name="path"/> as JSON, or an empty object when it is missing or unreadable.</summary>
    public static JsonObject Read(string path)
    {
        // Missing or unreadable files start empty
        if (!File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Return the section <paramref name="name"/> of <paramref name="root"/>, adding it when missing.</summary>
    public static JsonObject Section(JsonObject root, string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());

    /// <summary>Write <paramref name="root"/> to <paramref name="path"/>, creating its folder.</summary>
    public static void Write(string path, JsonObject root)
    {
        // Indented like the settings window writes it
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
