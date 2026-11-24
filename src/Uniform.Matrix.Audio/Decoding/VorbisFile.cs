namespace Uniform.Matrix.Audio.Decoding;

using NVorbis;

/// <summary>Decodes Ogg Vorbis files with <see href="https://github.com/NVorbis/NVorbis">NVorbis</see>, in C#.</summary>
internal static class VorbisFile
{
    /// <summary>Decode <paramref name="data"/> into <paramref name="clip"/>.</summary>
    public static void Decode(byte[] data, ClipBuilder clip)
    {
        // Format, then samples until the end
        using VorbisReader reader = new(new MemoryStream(data, writable: false), closeOnDispose: true);
        clip.SetFormat(reader.SampleRate, reader.Channels);
        float[] buffer = new float[4096 * reader.Channels];
        int read;
        // Done
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0) clip.Append(buffer.AsSpan(0, read));
    }
}
