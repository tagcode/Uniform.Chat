namespace Uniform.Matrix.Audio.Decoding;

/// <summary>A format of audio files that <see cref="AudioFile"/> decodes.</summary>
public enum AudioFormat
{
    /// <summary>Not recognized.</summary>
    Unknown,
    /// <summary>WAV of integer or float PCM.</summary>
    Wav,
    /// <summary>MP3, MPEG audio layer III.</summary>
    Mp3,
    /// <summary>FLAC.</summary>
    Flac,
    /// <summary>Opus in Ogg, e.g. Element's voice messages.</summary>
    OggOpus,
    /// <summary>Vorbis in Ogg.</summary>
    OggVorbis,
}

/// <summary>Decodes audio files to play: WAV, MP3, FLAC, Ogg Opus and Ogg Vorbis, recognized by their content, into an <see cref="AudioClip"/>.</summary>
/// <example>
/// <code><![CDATA[
/// AudioClip clip = await AudioFile.DecodeFileAsync("voice.ogg");
/// using AudioClipPlayer player = new(() => devices.OpenMediaSpeaker());
/// player.Play(clip);
/// ]]></code>
/// </example>
public static class AudioFile
{
    /// <summary>The most samples a clip may have, 128 MB of 16-bit samples: about 12 minutes of stereo at 44.1 kHz, or 23 of mono at 48 kHz.</summary>
    public const long DefaultMaxSamples = 64L * 1024 * 1024;

    /// <summary>MIME types of the formats, as messages carry them.</summary>
    private static readonly string[] MimeTypes = ["audio/wav", "audio/wave", "audio/x-wav", "audio/vnd.wave", "audio/mpeg", "audio/mp3", "audio/mpeg3", "audio/x-mpeg-3", "audio/flac", "audio/x-flac", "audio/ogg", "audio/opus", "application/ogg", "audio/vorbis"];

    /// <summary>File name extensions of the formats.</summary>
    private static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".ogg", ".oga", ".opus"];

    /// <summary>Return whether a file of <paramref name="mimeType"/> or <paramref name="fileName"/> is likely one to play, before it is downloaded; an Ogg file may still hold another codec.</summary>
    public static bool IsPlayable(string? mimeType, string? fileName)
    {
        // By type, then by name
        string? type = mimeType?.Split(';')[0].Trim().ToLowerInvariant();
        if (type is not null && MimeTypes.Contains(type)) return true;
        // Return by extension
        return fileName is not null && Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
    }

    /// <summary>Return the format of <paramref name="data"/> from its first bytes.</summary>
    public static AudioFormat Detect(ReadOnlySpan<byte> data)
    {
        // Containers by their magic
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8)) return AudioFormat.Wav;
        if (data.Length >= 4 && data[..4].SequenceEqual("fLaC"u8)) return AudioFormat.Flac;
        if (data.Length >= 4 && data[..4].SequenceEqual("OggS"u8))
        {
            ReadOnlySpan<byte> first = data[..Math.Min(data.Length, 128)];
            if (first.IndexOf("OpusHead"u8) >= 0) return AudioFormat.OggOpus;
            if (first.IndexOf("\x01vorbis"u8) >= 0) return AudioFormat.OggVorbis;
            return AudioFormat.Unknown;
        }
        // An ID3v2 tag before FLAC or MP3
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8))
        {
            int size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
            int after = 10 + size;
            return data.Length >= after + 4 && data.Slice(after, 4).SequenceEqual("fLaC"u8) ? AudioFormat.Flac : AudioFormat.Mp3;
        }
        // Return MP3 by its frame sync, or none
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE6) == 0xE2 ? AudioFormat.Mp3 : AudioFormat.Unknown;
    }

    /// <summary>Decode a whole file of a format <see cref="Detect"/> recognizes.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="maxSamples">The most samples the clip may have.</param>
    /// <exception cref="NotSupportedException">When the format is not recognized or not supported, e.g. AAC or Ogg of another codec, or the clip would be longer than <paramref name="maxSamples"/>.</exception>
    /// <exception cref="InvalidDataException">When the file is broken, or has no audio.</exception>
    public static AudioClip Decode(byte[] data, long maxSamples = DefaultMaxSamples)
    {
        // By format; the libraries' own failures on broken files as broken
        ClipBuilder clip = new(maxSamples);
        AudioFormat format = Detect(data);
        try
        {
            switch (format)
            {
                case AudioFormat.Wav: WavFile.Decode(data, clip); break;
                case AudioFormat.Mp3: Mp3File.Decode(data, clip); break;
                case AudioFormat.Flac: FlacFile.Decode(data, clip); break;
                case AudioFormat.OggOpus: OpusFile.Decode(data, clip); break;
                case AudioFormat.OggVorbis: VorbisFile.Decode(data, clip); break;
                default: throw new NotSupportedException("The audio format is not supported.");
            }
        }
        catch (Exception exception) when (exception is not (NotSupportedException or InvalidDataException or OutOfMemoryException or OperationCanceledException))
        {
            throw new InvalidDataException($"The {format} file is broken: {exception.Message}", exception);
        }
        // Return the clip
        return clip.Build();
    }

    /// <summary>Read and decode the file at <paramref name="path"/> on the thread pool; see <see cref="Decode"/>.</summary>
    public static async Task<AudioClip> DecodeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        // Read, then decode off the caller's thread
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // Return the clip
        return await Task.Run(() => Decode(data), cancellationToken).ConfigureAwait(false);
    }
}
