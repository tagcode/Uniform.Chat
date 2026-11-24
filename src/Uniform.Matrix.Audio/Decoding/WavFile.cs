namespace Uniform.Matrix.Audio.Decoding;

using NAudio.Wave;

/// <summary>Decodes WAV files with NAudio's <see cref="WaveFileReader"/>: integer PCM of 8, 16, 24 and 32 bits, and IEEE float.</summary>
/// <remarks>Compressed WAV, e.g. ADPCM or µ-law, is not supported by NAudio's cross-platform build.</remarks>
internal static class WavFile
{
    /// <summary>Decode <paramref name="data"/> into <paramref name="clip"/>.</summary>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // Format, then samples until the end
        using WaveFileReader reader = new(new MemoryStream(data, writable: false));
        clip.SetFormat(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        ISampleProvider samples = reader.ToSampleProvider();
        float[] buffer = new float[4096 * reader.WaveFormat.Channels];
        int read;
        // Done
        while ((read = samples.Read(buffer.AsSpan())) > 0) clip.Append(buffer.AsSpan(0, read));
    }
}
