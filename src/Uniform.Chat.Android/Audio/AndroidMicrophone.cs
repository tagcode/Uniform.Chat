namespace Uniform.Chat.Android.Audio;

using global::Android.Content;
using global::Android.Media;
using global::Android.Media.Audiofx;
using NAudio.Wave;
using Uniform.Matrix.Audio;

/// <summary>The phone's microphone through <see cref="AudioRecord"/>, as an NAudio <see cref="IWaveIn"/> recording the voice format in 20 ms pieces.</summary>
/// <remarks>Records in <see cref="CommunicationMode"/> from Android's voice communication source, which applies the phone's own echo cancellation and noise suppression where it has them; those effects are also turned on explicitly when available, and kept for the recording, as a collected effect is released.</remarks>
/// <param name="context">Context for the audio service.</param>
public sealed class AndroidMicrophone(Context context) : IWaveIn
{
    private const int ChunkBytes = VoiceFormat.FrameSamples * 2;
    private AudioRecord? record;
    private AcousticEchoCanceler? echoCanceler;
    private NoiseSuppressor? noiseSuppressor;
    private bool inCommunication;
    private Thread? reader;
    private volatile bool recording;

    /// <summary>The voice format: 48 kHz mono 16-bit.</summary>
    public WaveFormat WaveFormat { get; set; } = VoiceFormat.WaveFormat;

    /// <summary>Raised with each recorded piece, on the recording thread.</summary>
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    /// <summary>Raised when recording ends, with the error that ended it, if any.</summary>
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Start recording on a thread of its own.</summary>
    public void StartRecording()
    {
        // Assert not running, then the recorder with room for a few pieces
        if (recording) return;
        if (!inCommunication) CommunicationMode.Enter(context);
        inCommunication = true;
        int minimum = AudioRecord.GetMinBufferSize(VoiceFormat.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        record = new AudioRecord(AudioSource.VoiceCommunication, VoiceFormat.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minimum, ChunkBytes * 4));
        if (record.State != State.Initialized) throw new InvalidOperationException("The microphone could not be opened; is the microphone permission granted?");
        echoCanceler ??= AcousticEchoCanceler.IsAvailable ? AcousticEchoCanceler.Create(record.AudioSessionId) : null;
        echoCanceler?.SetEnabled(true);
        noiseSuppressor ??= NoiseSuppressor.IsAvailable ? NoiseSuppressor.Create(record.AudioSessionId) : null;
        noiseSuppressor?.SetEnabled(true);

        // Read until stopped
        recording = true;
        record.StartRecording();
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "Microphone" };
        reader.Start();
    }

    /// <summary>Stop recording and wait for the thread.</summary>
    public void StopRecording()
    {
        // The thread ends at its next read
        if (!recording) return;
        recording = false;
        reader?.Join(1000);
        reader = null;
    }

    /// <summary>Stop and free the recorder.</summary>
    public void Dispose()
    {
        // Stop, then release
        StopRecording();
        echoCanceler?.Release();
        noiseSuppressor?.Release();
        echoCanceler = null;
        noiseSuppressor = null;
        record?.Release();
        record = null;
        if (inCommunication) CommunicationMode.Exit(context);
        inCommunication = false;
    }

    /// <summary>Read 20 ms pieces and raise them, until stopped or failed.</summary>
    private void ReadLoop()
    {
        // Each read blocks until a piece is there
        Exception? error = null;
        try
        {
            byte[] buffer = new byte[ChunkBytes];
            while (recording && record is AudioRecord current)
            {
                int read = current.Read(buffer, 0, buffer.Length);
                if (read < 0) throw new InvalidOperationException($"Reading the microphone failed with code {read}.");
                if (read > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, read));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex;
        }
        finally
        {
            record?.Stop();
            recording = false;
        }
        // Done
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }
}
