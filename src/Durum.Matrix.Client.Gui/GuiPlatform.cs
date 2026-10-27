namespace Durum.Matrix.Client.Gui;

using Durum.Matrix.Client.Gui.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>What the desktop and Android apps tell the shared user interface about their platform.</summary>
public sealed record GuiPlatform
{
    /// <summary>Application name, which names the data and settings folders, e.g. <c>Durum.Matrix.Client.Desktop</c>.</summary>
    public required string ApplicationName { get; init; }
    /// <summary>Display name of the device created at sign-in, e.g. <c>Durum on Linux</c>.</summary>
    public required string DeviceName { get; init; }
    /// <summary>Whether the screen shows one pane at a time, the room list or a room, as on phones.</summary>
    public bool SinglePane { get; init; }
    /// <summary>Extra configuration of the client engine, e.g. <c>Storage:DataDirectory</c>.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();
    /// <summary>System notifications, or null where the platform has none yet.</summary>
    public INotifier? Notifier { get; init; }
    /// <summary>Changes to the engine's services, e.g. a test HTTP handler.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Return the platform of the desktop app on this operating system.</summary>
    public static GuiPlatform Desktop() => new()
    {
        ApplicationName = "Durum.Matrix.Client.Desktop",
        DeviceName = OperatingSystem.IsWindows() ? "Durum on Windows" : OperatingSystem.IsMacOS() ? "Durum on macOS" : "Durum on Linux",
    };
}
