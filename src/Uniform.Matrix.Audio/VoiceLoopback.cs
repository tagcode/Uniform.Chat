namespace Uniform.Matrix.Audio;

/// <summary>Plays the microphone back through the speaker after a round trip through Opus, a delay later, to test the audio path without a call: speak, and hear yourself.</summary>
public sealed class VoiceLoopback : IDisposable
{
    private readonly MicrophoneFrames microphone;
    private readonly SpeakerFrames speaker;
    private readonly VoiceEncoder encoder = new();
    private readonly VoiceDecoder decoder = new();
    private readonly Queue<byte[]> delayed = new();
    private readonly int delayFrames;
    private readonly Lock gate = new();

    /// <summary>Loop the microphone of <paramref name="devices"/> back to its speaker.</summary>
    /// <param name="devices">The platform's devices.</param>
    /// <param name="delay">How much later to play what was said; a second or so makes it easy to tell your own voice from the echo.</param>
    public VoiceLoopback(IAudioDevices devices, TimeSpan delay)
    {
        // Devices, and the delay in frames
        delayFrames = Math.Max(0, (int)(delay.TotalMilliseconds / VoiceFormat.FrameMilliseconds));
        microphone = new MicrophoneFrames(devices.OpenMicrophone());
        speaker = new SpeakerFrames(devices.OpenSpeaker(), delay + TimeSpan.FromMilliseconds(500));
        microphone.FrameCaptured += OnFrame;
    }

    /// <summary>Frames encoded so far.</summary>
    public int Frames { get; private set; }

    /// <summary>Bytes of Opus so far, to see the bit rate.</summary>
    public long Bytes { get; private set; }

    /// <summary>Start recording and playing.</summary>
    public void Start()
    {
        // Speaker first, so nothing recorded is lost
        speaker.Start();
        microphone.Start();
    }

    /// <summary>Stop and close the devices.</summary>
    public void Dispose()
    {
        // Microphone first, then the speaker
        microphone.FrameCaptured -= OnFrame;
        microphone.Stop();
        microphone.Dispose();
        speaker.Stop();
        speaker.Dispose();
    }

    /// <summary>Encode a frame, and play the one recorded the delay ago.</summary>
    private void OnFrame(short[] frame)
    {
        // Through Opus and the delay queue
        byte[] packet = encoder.Encode(frame);
        lock (gate)
        {
            Frames++;
            Bytes += packet.Length;
            delayed.Enqueue(packet);
            if (delayed.Count <= delayFrames) return;
            speaker.Play(decoder.Decode(delayed.Dequeue()));
        }
    }
}
