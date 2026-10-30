namespace Uniform.Chat.Terminal.Configuration;

using Uniform.Chat.Terminal.Ui.Images;
using Uniform.Matrix.Hosting;

/// <summary>Display settings, configuration section <see cref="Section"/>.</summary>
public sealed record UiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Ui";

    /// <summary>Room ID or alias to open at start; the first room in the list when null.</summary>
    [RestartRequired]
    public string? StartRoom { get; set; }
    /// <summary>Whether to show ASCII previews of images in the message log.</summary>
    public bool InlineImages { get; set; } = true;
    /// <summary>Widest preview in terminal columns; 0 fills the width of the message log.</summary>
    public int ImagePreviewColumns { get; set; }
    /// <summary>Tallest preview in terminal rows; 0 allows three quarters of the message log's height.</summary>
    public int ImagePreviewRows { get; set; }
    /// <summary>Drawing style: <see cref="ImagePreviewStyle.Blocks"/> shades, <see cref="ImagePreviewStyle.Ascii"/> characters or <see cref="ImagePreviewStyle.Braille"/> dots.</summary>
    public ImagePreviewStyle ImagePreviewStyle { get; set; } = ImagePreviewStyle.Blocks;
    /// <summary>Whether the terminal background is dark, so bright pixels get dense characters.</summary>
    public bool DarkBackground { get; set; } = true;
    /// <summary>Folder that saved files go to; when null, <c>~/Downloads</c> if it exists, otherwise the home folder.</summary>
    public string? DownloadDirectory { get; set; }
    /// <summary>Whether Save file asks where to save, starting from <see cref="DownloadDirectory"/>; otherwise files are saved there under a free name.</summary>
    public bool AskWhereToSave { get; set; } = true;
}
