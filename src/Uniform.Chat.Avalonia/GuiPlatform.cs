namespace Uniform.Matrix.Client.Avalonia.Core;

using Uniform.Matrix.Client.Avalonia.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Uniform.Matrix.Audio;

/// <summary>What the desktop and Android apps tell the shared user interface about their platform.</summary>
public sealed record GuiPlatform
{
    /// <summary>Application name, which names the data and settings folders, e.g. <c>Uniform.Matrix.Client.Desktop</c>.</summary>
    public required string ApplicationName { get; init; }
    /// <summary>Display name of the device created at sign-in, e.g. <c>Uniform.Matrix on Linux</c>.</summary>
    public required string DeviceName { get; init; }
    /// <summary>Whether the screen shows one pane at a time, the room list or a room, as on phones.</summary>
    public bool SinglePane { get; init; }
    /// <summary>Extra configuration of the client engine, e.g. <c>Storage:DataDirectory</c>.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();
    /// <summary>System notifications, or null where the platform has none yet.</summary>
    public INotifier? Notifier { get; init; }
    /// <summary>Changes to the engine's services, e.g. a test HTTP handler.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
    /// <summary>HTTP handler for requests outside the engine, such as creating an account; null for the default. Tests route them to a server in memory.</summary>
    public Func<HttpMessageHandler>? HttpHandler { get; init; }
    /// <summary>Open the platform's microphone and speaker, given whether to use the loudspeaker rather than an earpiece; null where there is no audio.</summary>
    public Func<bool, IAudioDevices>? Audio { get; init; }
    /// <summary>Whether the platform has an earpiece, so that the loudspeaker is a choice, as on phones.</summary>
    public bool HasEarpiece { get; init; }
    /// <summary>Ask for permission to record, where the platform needs it; return whether it is granted. Null where none is needed.</summary>
    public Func<Task<bool>>? RequestMicrophoneAsync { get; init; }
    /// <summary>Keep a call with the named peer going while the app is in the background, e.g. a foreground service on Android; dispose the result when the call ends. Null where nothing is needed.</summary>
    public Func<string, IDisposable>? KeepCallAlive { get; init; }
    /// <summary>Keep the engine syncing while the app is in the background or the screen is off, e.g. a foreground service on Android, which otherwise cuts the app's network; dispose the result when signed out. Null where nothing is needed.</summary>
    public Func<IDisposable>? KeepConnected { get; init; }
    /// <summary>Ring for an incoming call from the named peer, e.g. the ringtone and a full-screen call notification on Android; dispose the result to stop. Null where the platform only notifies.</summary>
    public Func<string, IDisposable>? Ring { get; init; }
    /// <summary>Turn the screen off while the phone is held to the ear, so that the cheek does not press buttons; dispose the result to stop. Null where there is no proximity sensor.</summary>
    public Func<IDisposable>? ScreenOffNearEar { get; init; }

    /// <summary>Return the platform of the desktop app on this operating system.</summary>
    public static GuiPlatform Desktop() => new()
    {
        ApplicationName = "Uniform.Matrix.Client.Desktop",
        DeviceName = OperatingSystem.IsWindows() ? "Uniform.Matrix on Windows" : OperatingSystem.IsMacOS() ? "Uniform.Matrix on macOS" : "Uniform.Matrix on Linux",
        Audio = AudioDevices.ForThisPlatform() is null ? null : _ => AudioDevices.ForThisPlatform()!,
    };
}
