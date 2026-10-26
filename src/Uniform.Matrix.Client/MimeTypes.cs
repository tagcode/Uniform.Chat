namespace Uniform.Matrix.Client;

/// <summary>Guesses MIME types from file extensions for uploads.</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
    };

    /// <summary>Return the MIME type for <paramref name="path"/>, or <c>application/octet-stream</c> when unknown.</summary>
    public static string FromPath(string path) => ByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
}
