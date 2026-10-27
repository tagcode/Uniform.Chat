namespace Uniform.Matrix.Server.Configuration;

/// <summary>Limits of the <see href="https://spec.matrix.org/latest/client-server-api/#content-repository">media repository</see>, configuration section <see cref="Section"/>.</summary>
public sealed record MediaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Media";

    /// <summary>Largest upload in bytes; 50 MiB by default.</summary>
    public long MaxUploadSize { get; set; } = 50 * 1024 * 1024;
    /// <summary>Largest image, in pixels, that thumbnails are made from; larger images get no thumbnail.</summary>
    public long MaxThumbnailSourcePixels { get; set; } = 40_000_000;
    /// <summary>Whether to serve the deprecated downloads and thumbnails of <c>/_matrix/media/v3</c>, which need no access token, for older clients; anyone with a link can then fetch the file.</summary>
    public bool AllowUnauthenticatedDownloads { get; set; }
    /// <summary>How long media created with <c>/media/v1/create</c> waits for its content before it can no longer be uploaded.</summary>
    public TimeSpan PendingUploadLifetime { get; set; } = TimeSpan.FromHours(24);
    /// <summary>How many created but not yet uploaded media a user may have at once.</summary>
    public int MaxPendingUploads { get; set; } = 10;
}
