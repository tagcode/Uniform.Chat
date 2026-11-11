namespace Uniform.LiveKit.Server;

using Uniform.LiveKit.Hosting;

/// <summary>Reads lk-jwt-service's environment variables as settings, so that this program replaces it as is, e.g. in a container.</summary>
internal static class CompatibleEnvironment
{
    /// <summary>Return the settings of the variables that are set: <c>LIVEKIT_URL</c>, <c>LIVEKIT_KEY</c>, <c>LIVEKIT_SECRET</c> (or <c>LIVEKIT_KEY_FILE</c> and <c>LIVEKIT_SECRET_FILE</c>), <c>LIVEKIT_FULL_ACCESS_HOMESERVERS</c> and <c>LIVEKIT_JWT_BIND</c>.</summary>
    /// <param name="variable">Returns an environment variable, or null.</param>
    public static Dictionary<string, string?> Read(Func<string, string?> variable)
    {
        // Each one that is set
        Dictionary<string, string?> settings = [];
        Set(settings, $"{LiveKitOptions.Section}:{nameof(LiveKitOptions.Url)}", variable("LIVEKIT_URL"));
        Set(settings, $"{LiveKitOptions.Section}:{nameof(LiveKitOptions.ApiKey)}", variable("LIVEKIT_KEY") ?? FromFile(variable("LIVEKIT_KEY_FILE")));
        Set(settings, $"{LiveKitOptions.Section}:{nameof(LiveKitOptions.ApiSecret)}", variable("LIVEKIT_SECRET") ?? FromFile(variable("LIVEKIT_SECRET_FILE")));
        Set(settings, $"{LiveKitOptions.Section}:{nameof(LiveKitOptions.FullAccessHomeservers)}", variable("LIVEKIT_FULL_ACCESS_HOMESERVERS"));
        if (variable("LIVEKIT_JWT_BIND") is string bind && bind.Length > 0) settings["Urls"] = bind.Contains("://", StringComparison.Ordinal) ? bind : $"http://{(bind.StartsWith(':') ? "0.0.0.0" : "")}{bind}";
        // Return settings
        return settings;
    }

    /// <summary>Return the first setting the service lacks, or null when it can run.</summary>
    public static string? Missing(LiveKitOptions options) =>
        string.IsNullOrWhiteSpace(options.Url) ? "LiveKit's URL"
        : string.IsNullOrWhiteSpace(options.ApiKey) ? "LiveKit's API key"
        : string.IsNullOrWhiteSpace(options.ApiSecret) ? "LiveKit's API secret"
        : string.IsNullOrWhiteSpace(options.FullAccessHomeservers) ? "the homeservers whose users may start calls, LiveKit:FullAccessHomeservers or LIVEKIT_FULL_ACCESS_HOMESERVERS, e.g. example.org, or * for all"
        : null;

    /// <summary>Set <paramref name="key"/> when <paramref name="value"/> is not empty.</summary>
    private static void Set(Dictionary<string, string?> settings, string key, string? value)
    {
        // Only given values
        if (string.IsNullOrWhiteSpace(value)) return;
        // Done
        settings[key] = value.Trim();
    }

    /// <summary>Return the trimmed content of file <paramref name="path"/>, or null without a path.</summary>
    private static string? FromFile(string? path) => string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path).Trim();
}
