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
}
