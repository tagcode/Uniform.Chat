namespace Uniform.Chat.Android.Audio;

using global::Android.Content;
using global::Android.Media;
using NAudio.Wave;
using Uniform.Matrix.Audio;

/// <summary>The phone's microphone and earpiece or loudspeaker, as the shared audio code expects them.</summary>
/// <param name="context">Context for the audio service.</param>
/// <param name="loudspeaker">Whether to play through the loudspeaker rather than the earpiece.</param>
public sealed class AndroidAudioDevices(Context context, bool loudspeaker) : IAudioDevices
{
    /// <summary>Open the microphone.</summary>
    public IWaveIn OpenMicrophone() => new AndroidMicrophone(context);

    /// <summary>Open the earpiece or loudspeaker.</summary>
    public IWavePlayer OpenSpeaker() => new AndroidSpeaker(context, loudspeaker);

    /// <summary>Return the phone's inputs, by product name and kind.</summary>
    public IReadOnlyList<AudioDevice> ListMicrophones() => List(GetDevicesTargets.Inputs);

    /// <summary>Return the phone's outputs, by product name and kind.</summary>
    public IReadOnlyList<AudioDevice> ListSpeakers() => List(GetDevicesTargets.Outputs);

    /// <summary>Return the devices of a direction.</summary>
    private IReadOnlyList<AudioDevice> List(GetDevicesTargets direction)
    {
        // From the audio service
        if (context.GetSystemService(Context.AudioService) is not AudioManager audio) return [];
        // Return names
        return [.. (audio.GetDevices(direction) ?? []).Select(d => new AudioDevice($"{d.ProductName} ({d.Type})", $"{d.ProductName} ({d.Type})"))];
    }
}
