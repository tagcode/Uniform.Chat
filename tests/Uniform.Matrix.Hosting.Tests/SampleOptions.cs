namespace Uniform.Matrix.Hosting.Tests;

/// <summary>Drawing styles of <see cref="SampleUiOptions.ImagePreviewStyle"/>.</summary>
public enum SamplePreviewStyle
{
    /// <summary>Shaded blocks.</summary>
    Blocks,
    /// <summary>ASCII characters.</summary>
    Ascii,
    /// <summary>Braille dots.</summary>
    Braille,
}

/// <summary>Account options for tests, section <c>Matrix</c>.</summary>
public sealed record SampleMatrixOptions
{
    /// <summary>Password for the first login; secret.</summary>
    [Secret]
    public string? Password { get; set; }
    /// <summary>Long-poll time of each sync request.</summary>
    [RestartRequired]
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Timeline events per room in each sync.</summary>
    [RestartRequired]
    public int TimelineLimit { get; set; } = 50;
}

/// <summary>Display options for tests, section <c>Ui</c>.</summary>
public sealed record SampleUiOptions
{
    /// <summary>Room to open at start, see <see cref="SampleMatrixOptions"/>.</summary>
    [RestartRequired]
    public string? StartRoom { get; set; }
    /// <summary>Whether to show <c>inline</c> previews.</summary>
    public bool InlineImages { get; set; } = true;
    /// <summary>Tallest preview in rows; 0 for automatic.</summary>
    public int ImagePreviewRows { get; set; }
    /// <summary>Drawing style, e.g. <see cref="SamplePreviewStyle.Ascii"/>.</summary>
    public SamplePreviewStyle ImagePreviewStyle { get; set; } = SamplePreviewStyle.Blocks;
    /// <summary>Whether the terminal background is dark.</summary>
    public bool DarkBackground { get; set; } = true;
}
