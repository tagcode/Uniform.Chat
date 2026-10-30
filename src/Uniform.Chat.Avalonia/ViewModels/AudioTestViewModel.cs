namespace Uniform.Matrix.Client.Avalonia.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Uniform.Matrix.Audio;

/// <summary>The audio test: plays the microphone back a second later, through Opus as in a call, to try the microphone and the speaker or earpiece.</summary>
/// <param name="audio">Opens the platform's devices, given whether to use the loudspeaker.</param>
/// <param name="hasEarpiece">Whether the loudspeaker is a choice, as on phones.</param>
/// <param name="requestMicrophone">Asks for the microphone permission where needed, or null.</param>
public sealed partial class AudioTestViewModel(Func<bool, IAudioDevices> audio, bool hasEarpiece, Func<Task<bool>>? requestMicrophone) : ViewModelBase, IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);
    private VoiceLoopback? loopback;

    /// <summary>Whether the loudspeaker is a choice.</summary>
    public bool HasEarpiece { get; } = hasEarpiece;

    /// <summary>Whether to play through the loudspeaker rather than the earpiece; only where <see cref="HasEarpiece"/>.</summary>
    [ObservableProperty]
    public partial bool Loudspeaker { get; set; } = true;

    /// <summary>Whether the test runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial bool IsRunning { get; set; }

    /// <summary>What the test does or did, e.g. the bit rate after a run.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Start, then speak: you hear yourself a second later, through the Opus codec of calls.";

    /// <summary>Text of the start and stop button.</summary>
    public string ButtonText => IsRunning ? "Stop" : "Start";

    /// <summary>Start the test, asking for the microphone first where needed, or stop it.</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        // Stop a running test
        if (IsRunning)
        {
            Stop();
            return;
        }

        // Permission, then the devices
        if (requestMicrophone is not null && !await requestMicrophone())
        {
            Status = "The microphone permission is needed for the test.";
            return;
        }
        try
        {
            loopback = new VoiceLoopback(audio(HasEarpiece && Loudspeaker), Delay);
            loopback.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            loopback?.Dispose();
            loopback = null;
            Status = $"The microphone or speaker could not be opened: {ex.Message}";
            return;
        }
        // Running
        IsRunning = true;
        Status = "Speak: you hear yourself a second later. Silence means the input is not a microphone.";
    }

    /// <summary>Stop the test and show what went through.</summary>
    public void Stop()
    {
        // Close the devices, then the numbers
        if (loopback is not VoiceLoopback running) return;
        loopback = null;
        running.Dispose();
        IsRunning = false;
        double seconds = running.Frames * VoiceFormat.FrameMilliseconds / 1000.0;
        Status = seconds > 0 ? $"Recorded {seconds:F0} s, sent at {running.Bytes * 8 / seconds / 1000:F0} kbit/s." : "Nothing was recorded.";
    }

    /// <summary>Stop the test.</summary>
    public void Dispose() => Stop();
}
