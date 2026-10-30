namespace Uniform.Chat.Terminal.Ui;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Uniform.Chat.Terminal.Configuration;
using Uniform.Matrix.Audio;
using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Hosting;

// Settings and log windows, and the audio test.
public sealed partial class MainWindow
{
    /// <summary>Open the settings window; ask before closing with unsaved changes.</summary>
    private void ShowSettings()
    {
        // Run until closed with nothing unsaved, or confirmed
        SettingsEditor editor = new(settings, optionCatalog.GetAll(configuration));
        using SettingsWindow window = new(app, editor);
        do app.Run(window);
        while (!window.ConfirmClose());
    }

    /// <summary>Open the log window on this session's log.</summary>
    private void ShowLogs()
    {
        // Session logs, and the single log of older versions
        string dataDirectory = storageOptions.ResolveDataDirectory(environment.ApplicationName);
        using LogWindow window = new(app, storageOptions.ResolveLogDirectory(environment.ApplicationName), fileLogger.FilePath, Path.Combine(dataDirectory, "client.log"));
        app.Run(window);
    }

    /// <summary>Redraw with new display settings, e.g. another preview style.</summary>
    private void OnUiOptionsChanged()
    {
        // Previews are rendered per setting
        renderedPreviews.Clear();
        timeline.Refresh();
    }

    /// <summary>Play the microphone back through the speaker a second later, through Opus as in a call, until the dialog closes; then show how much was encoded.</summary>
    private void TestAudio()
    {
        // Devices of this platform, as the settings choose them
        AudioOptions audio = configuration.GetSection(AudioOptions.Section).Get<AudioOptions>() ?? new AudioOptions();
        if (AudioDevices.ForThisPlatform(audio.Microphone, audio.Speaker) is not IAudioDevices devices)
        {
            Dialogs.Error(app, "Audio test", "Audio is not supported on this platform.");
            return;
        }

        // Loop until the dialog closes
        VoiceLoopback loopback;
        try
        {
            loopback = new VoiceLoopback(devices, TimeSpan.FromSeconds(1));
            loopback.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Audio test failed to start: {Error}", ex.Message);
            Dialogs.Error(app, "Audio test", $"Cannot open the microphone or speaker: {ex.Message}");
            return;
        }
        Dialogs.Info(app, "Audio test", string.Join('\n',
            "Speak: you hear yourself a second later, through Opus as in a call.",
            $"Microphone: {audio.Microphone ?? "system default"}. Speaker: {audio.Speaker ?? "system default"}.",
            "Silence means the input is not a microphone: choose one in the system's sound settings, or set Audio:Microphone in Settings.",
            "",
            $"Microphones: {DeviceNames(devices.ListMicrophones())}",
            $"Speakers: {DeviceNames(devices.ListSpeakers())}"));
        loopback.Dispose();

        // Done, with what went through
        double seconds = loopback.Frames * VoiceFormat.FrameMilliseconds / 1000.0;
        ShowStatus(seconds > 0 ? $"Audio test: {seconds:F0} s at {loopback.Bytes * 8 / seconds / 1000:F0} kbit/s" : "Audio test: nothing was recorded");
    }

    /// <summary>Return device names for a dialog, the first few of a long list.</summary>
    private static string DeviceNames(IReadOnlyList<AudioDevice> devices) =>
        devices.Count == 0 ? "none found" : string.Join(", ", devices.Take(8).Select(d => d.Name)) + (devices.Count > 8 ? $" and {devices.Count - 8} more" : "");
}

