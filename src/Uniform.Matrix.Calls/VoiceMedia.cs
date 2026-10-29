namespace Uniform.Matrix.Calls;

using Uniform.Matrix.Audio;
using Uniform.Matrix.WebRtc;
using Uniform.Matrix.WebRtc.Rtp;

/// <summary>The voice of a call: the microphone's frames encoded with Opus and sent over a peer connection, and the frames received put in order by a jitter buffer, decoded, and played as the speaker needs them.</summary>
/// <remarks>The speaker's clock paces playing: a timer tops up its buffer to <see cref="SpeakerTarget"/>, one frame at a time from the jitter buffer, concealing lost ones. So the sender's and the speaker's clocks may drift apart without the delay growing.</remarks>
public sealed class VoiceMedia : IAsyncDisposable
{
    /// <summary>How much audio the speaker is kept queued.</summary>
    public static readonly TimeSpan SpeakerTarget = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);
    private readonly AudioPeerConnection connection;
    private readonly MicrophoneFrames microphone;
    private readonly SpeakerFrames speaker;
    private readonly VoiceEncoder encoder = new();
    private readonly VoiceDecoder decoder = new();
    private readonly JitterBuffer jitter = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock encoding = new();
    private Task? playing;

    /// <summary>Open the devices for a connection; nothing records or plays until <see cref="Start"/>.</summary>
    public VoiceMedia(AudioPeerConnection connection, IAudioDevices devices)
    {
        // Devices, and the connection's audio into the jitter buffer
        this.connection = connection;
        microphone = new MicrophoneFrames(devices.OpenMicrophone());
        speaker = new SpeakerFrames(devices.OpenSpeaker());
        microphone.FrameCaptured += OnFrame;
        connection.AudioReceived += jitter.Add;
    }

    /// <summary>Whether the microphone is muted; muted frames are not sent, and the next one sent is marked.</summary>
    public bool Muted { get; set; }

    /// <summary>Frames received that were lost, and concealed.</summary>
    public long Lost => jitter.Lost;

    /// <summary>Start recording and playing.</summary>
    public void Start()
    {
        // Speaker first, then the microphone
        speaker.Start();
        microphone.Start();
        playing = PlayAsync(stopping.Token);
    }

    /// <summary>Stop, and close the devices.</summary>
    public async ValueTask DisposeAsync()
    {
        // Microphone, the player, then the speaker
        connection.AudioReceived -= jitter.Add;
        microphone.FrameCaptured -= OnFrame;
        microphone.Stop();
        microphone.Dispose();
        await stopping.CancelAsync();
        if (playing is not null) await playing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        speaker.Stop();
        speaker.Dispose();
    }

    /// <summary>Encode and send a recorded frame, or skip it while muted.</summary>
    private void OnFrame(short[] frame)
    {
        // Muted: time passes, nothing is sent
        if (Muted)
        {
            connection.SkipAudio(frame.Length);
            return;
        }
        // One frame at a time; a failed frame is dropped, not the microphone's thread
        byte[] packet;
        try
        {
            lock (encoding) packet = encoder.Encode(frame);
        }
        catch (Exception)
        {
            connection.SkipAudio(frame.Length);
            return;
        }
        // Done
        _ = connection.SendAudioAsync(packet, frame.Length).AsTask();
    }

    /// <summary>Top up the speaker from the jitter buffer every tick, until stopped.</summary>
    private async Task PlayAsync(CancellationToken cancellationToken)
    {
        // Frames while the speaker is short, concealing gaps once playing
        using PeriodicTimer timer = new(Tick);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            while (speaker.Buffered < SpeakerTarget)
            {
                RtpPacket? packet = jitter.Next(out bool isPlaying);
                if (!isPlaying) break;
                speaker.Play(decoder.Decode(packet?.Payload));
            }
        }
    }
}
