namespace Uniform.Matrix.Server.Configuration;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv1mediapreview_url">URL previews</see>: the server fetches pages that users link to and returns their title, description and image, configuration section <see cref="Section"/>.</summary>
/// <remarks>Off by default: fetching tells the linked site that someone on this server posted the link, and in encrypted rooms it shows the server what was linked.</remarks>
public sealed record UrlPreviewOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "UrlPreviews";

    /// <summary>Whether the server makes previews.</summary>
    public bool Enabled { get; set; }
    /// <summary>Whether pages at private, loopback and link-local addresses may be previewed; off, so accounts cannot make the server reach machines inside its network.</summary>
    public bool AllowPrivateAddresses { get; set; }
    /// <summary>Most bytes read of a page or its image.</summary>
    public long MaxSize { get; set; } = 2 * 1024 * 1024;
    /// <summary>How long to wait for a page and its image.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long a preview is reused for the same address.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
}
