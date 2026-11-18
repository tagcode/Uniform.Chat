namespace Uniform.MatrixRtc.Server;

using Uniform.MatrixRtc.Hosting;

/// <summary>Reads the service's environment variables as settings, e.g. in a container: lk-jwt-service's, with the prefix <c>MATRIXRTC_</c> instead of <c>LIVEKIT_</c>.</summary>
internal static class CompatibleEnvironment
{
    /// <summary>Return the settings of the variables that are set: <c>MATRIXRTC_URL</c>, <c>MATRIXRTC_KEY</c>, <c>MATRIXRTC_SECRET</c> (or <c>MATRIXRTC_KEY_FILE</c> and <c>MATRIXRTC_SECRET_FILE</c>), <c>MATRIXRTC_FULL_ACCESS_HOMESERVERS</c> and <c>MATRIXRTC_JWT_BIND</c>.</summary>
    /// <param name="variable">Returns an environment variable, or null.</param>
    public static Dictionary<string, string?> Read(Func<string, string?> variable)
    {
        // Each one that is set
        Dictionary<string, string?> settings = [];
        Set(settings, $"{MatrixRtcOptions.Section}:{nameof(MatrixRtcOptions.Url)}", variable("MATRIXRTC_URL"));
        Set(settings, $"{MatrixRtcOptions.Section}:{nameof(MatrixRtcOptions.ApiKey)}", variable("MATRIXRTC_KEY") ?? FromFile(variable("MATRIXRTC_KEY_FILE")));
        Set(settings, $"{MatrixRtcOptions.Section}:{nameof(MatrixRtcOptions.ApiSecret)}", variable("MATRIXRTC_SECRET") ?? FromFile(variable("MATRIXRTC_SECRET_FILE")));
        Set(settings, $"{MatrixRtcOptions.Section}:{nameof(MatrixRtcOptions.FullAccessHomeservers)}", variable("MATRIXRTC_FULL_ACCESS_HOMESERVERS"));
        if (variable("MATRIXRTC_JWT_BIND") is string bind && bind.Length > 0) settings["Urls"] = bind.Contains("://", StringComparison.Ordinal) ? bind : $"http://{(bind.StartsWith(':') ? "0.0.0.0" : "")}{bind}";
        // Return settings
        return settings;
    }

    /// <summary>Return the first setting the service lacks, or null when it can run.</summary>
    public static string? Missing(MatrixRtcOptions options) =>
        string.IsNullOrWhiteSpace(options.Url) ? "LiveKit's URL"
        : string.IsNullOrWhiteSpace(options.ApiKey) ? "LiveKit's API key"
        : string.IsNullOrWhiteSpace(options.ApiSecret) ? "LiveKit's API secret"
        : string.IsNullOrWhiteSpace(options.FullAccessHomeservers) ? "the homeservers whose users may start calls, MatrixRtc:FullAccessHomeservers or MATRIXRTC_FULL_ACCESS_HOMESERVERS, e.g. example.org, or * for all"
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
