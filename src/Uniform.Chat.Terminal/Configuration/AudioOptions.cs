namespace Uniform.Chat.Terminal.Configuration;

/// <summary>Microphone and speaker for the audio test and, later, calls, configuration section <see cref="Section"/>.</summary>
public sealed record AudioOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Audio";

    /// <summary>Microphone: an ALSA device name on Linux, e.g. <c>pulse</c>, or part of the device's name on Windows; the system's default when null. The audio test lists the choices.</summary>
    public string? Microphone { get; set; }
    /// <summary>Speaker: an ALSA device name on Linux, or part of the device's name on Windows; the system's default when null. The audio test lists the choices.</summary>
    public string? Speaker { get; set; }
}
