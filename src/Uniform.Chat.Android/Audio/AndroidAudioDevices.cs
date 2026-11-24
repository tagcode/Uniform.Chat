namespace Uniform.Chat.Android.Audio;

using global::Android.Content;
using NAudio.Wave;
using Uniform.Matrix.Audio;

/// <summary>The phone's microphones and speakers as the shared audio code expects them: built in, Bluetooth, wired and USB, opened as <paramref name="choice"/> names them.</summary>
/// <param name="context">Context for the audio service.</param>
/// <param name="choice">The microphone and speaker to open, by the names <see cref="ListMicrophones"/> and <see cref="ListSpeakers"/> give; null ones are the phone's defaults.</param>
public sealed class AndroidAudioDevices(Context context, AudioChoice choice) : IAudioDevices
{
    /// <summary>Open the chosen microphone.</summary>
    public IWaveIn OpenMicrophone() => new AndroidMicrophone(context, choice.Microphone);

    /// <summary>Open the chosen speaker.</summary>
    public IWavePlayer OpenSpeaker() => new AndroidSpeaker(context, choice.Speaker);

    /// <summary>Open the media stream, which plays to headphones or the loudspeaker at the media volume.</summary>
    public IWavePlayer OpenMediaSpeaker() => new AndroidSpeaker(context, null, media: true);

    /// <summary>Return the microphones: the phone's, then attached headsets'.</summary>
    public IReadOnlyList<AudioDevice> ListMicrophones() => AudioRoutes.Microphones(context);

    /// <summary>Return the speakers: earpiece, loudspeaker, then attached devices.</summary>
    public IReadOnlyList<AudioDevice> ListSpeakers() => AudioRoutes.Speakers(context);
}
