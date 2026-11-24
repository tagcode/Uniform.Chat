namespace Uniform.Matrix.Video.Webm;

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

/// <summary>A track of a <see cref="WebmFile"/>.</summary>
/// <param name="Number">The number its frames name it by.</param>
/// <param name="Kind">Video or audio.</param>
/// <param name="CodecId">The codec, e.g. <c>V_VP8</c>, <c>V_VP9</c>, <c>A_OPUS</c> or <c>A_VORBIS</c>.</param>
/// <param name="CodecPrivate">The codec's setup data, e.g. Opus's identification header; null for none.</param>
public sealed record WebmTrack(int Number, WebmTrackKind Kind, string CodecId, byte[]? CodecPrivate)
{
    /// <summary>Width of a video track's pictures in pixels; 0 for audio.</summary>
    public int Width { get; init; }

    /// <summary>Height of a video track's pictures in pixels; 0 for audio.</summary>
    public int Height { get; init; }

    /// <summary>Sample rate of an audio track; 0 for video.</summary>
    public double SampleRate { get; init; }

    /// <summary>Channels of an audio track; 0 for video.</summary>
    public int Channels { get; init; }
}

/// <summary>What a <see cref="WebmTrack"/> carries.</summary>
public enum WebmTrackKind
{
    /// <summary>Neither video nor audio, e.g. subtitles.</summary>
    Other,
    /// <summary>Pictures.</summary>
    Video,
    /// <summary>Sound.</summary>
    Audio,
}

/// <summary>A frame of a <see cref="WebmFile"/>'s track: a VP8 picture, or an Opus or Vorbis packet.</summary>
/// <param name="Track">The <see cref="WebmTrack.Number"/> of its track.</param>
/// <param name="Timestamp">When it plays, from the start; frames laced in one block share their block's.</param>
/// <param name="IsKeyframe">Whether it decodes without the frames before it.</param>
/// <param name="Data">The frame's bytes.</param>
public readonly record struct WebmFrame(int Track, TimeSpan Timestamp, bool IsKeyframe, ReadOnlyMemory<byte> Data);

/// <summary>A <see href="https://www.webmproject.org/docs/container/">WebM</see> file, the Matroska subset browsers and phones record video in: its tracks and their frames, in file order.</summary>
/// <remarks>Reads files of known sizes and the live ones browsers' MediaRecorder writes, whose segment and clusters have unknown sizes and no duration; frames are cut from blocks and laced blocks of every lacing.</remarks>
/// <see href="https://www.matroska.org/technical/elements.html"/>
public sealed class WebmFile
{
    private const uint EbmlId = 0x1A45DFA3, SegmentId = 0x18538067, InfoId = 0x1549A966, TracksId = 0x1654AE6B, ClusterId = 0x1F43B675;
    private const uint TimestampScaleId = 0x2AD7B1, DurationId = 0x4489, TrackEntryId = 0xAE, TrackNumberId = 0xD7, TrackTypeId = 0x83, CodecIdId = 0x86, CodecPrivateId = 0x63A2;
    private const uint VideoId = 0xE0, PixelWidthId = 0xB0, PixelHeightId = 0xBA, AudioId = 0xE1, SamplingFrequencyId = 0xB5, ChannelsId = 0x9F;
    private const uint TimestampId = 0xE7, SimpleBlockId = 0xA3, BlockGroupId = 0xA0, BlockId = 0xA1, ReferenceBlockId = 0xFB;

    /// <summary>The elements a cluster may hold; another one ends a cluster of unknown size.</summary>
    private static readonly HashSet<uint> ClusterChildren = [TimestampId, SimpleBlockId, BlockGroupId, 0xA7, 0xAB, 0x5854, 0xBF, 0xEC];

    private readonly List<WebmTrack> tracks = [];
    private readonly List<WebmFrame> frames = [];
    private long timestampScale = 1_000_000;
    private double? duration;

    /// <summary>The tracks.</summary>
    public IReadOnlyList<WebmTrack> Tracks => tracks;

    /// <summary>Every track's frames, in file order, which is time order per track.</summary>
    public IReadOnlyList<WebmFrame> Frames => frames;

    /// <summary>How long it plays: as the file tells, or else to its last frame.</summary>
    public TimeSpan Duration => duration is double ticks ? TimeSpan.FromTicks((long)(ticks * timestampScale / 100)) : frames.Count == 0 ? TimeSpan.Zero : frames.Max(f => f.Timestamp);

    /// <summary>Return whether <paramref name="data"/> starts like a Matroska or WebM file.</summary>
    public static bool IsWebm(ReadOnlySpan<byte> data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == EbmlId;

    /// <summary>Read the whole file in <paramref name="data"/>, which its frames refer into.</summary>
    /// <exception cref="InvalidDataException">When it is not WebM or has no segment.</exception>
    public static WebmFile Parse(byte[] data)
    {
        // The EBML header, then the segment's elements
        if (!IsWebm(data)) throw new InvalidDataException("Not a WebM file.");
        WebmFile file = new();
        int position = 0;
        while (position < data.Length && ReadElement(data, ref position, out uint id, out long size))
        {
            long end = size < 0 ? data.Length : Math.Min(data.Length, position + size);
            if (id == SegmentId) file.ReadSegment(data, position, (int)end);
            position = (int)end;
        }
        // Return it, with something to play
        return file.tracks.Count > 0 ? file : throw new InvalidDataException("The WebM file has no tracks.");
    }

    /// <summary>Read the segment's info, tracks and clusters between <paramref name="start"/> and <paramref name="end"/>.</summary>
    private void ReadSegment(byte[] data, int start, int end)
    {
        // Each element; a cluster of unknown size ends where another top-level element starts
        int position = start;
        while (position < end && ReadElement(data, ref position, out uint id, out long size))
        {
            int elementEnd = size < 0 ? (id == ClusterId ? FindClusterEnd(data, position, end) : end) : (int)Math.Min(end, position + size);
            switch (id)
            {
                case InfoId: ReadInfo(data, position, elementEnd); break;
                case TracksId: ReadTracks(data, position, elementEnd); break;
                case ClusterId: ReadCluster(data, position, elementEnd); break;
            }
            position = elementEnd;
        }
    }

    /// <summary>Read the timestamp scale and the duration.</summary>
    private void ReadInfo(byte[] data, int position, int end)
    {
        // Two of the info's elements
        while (position < end && ReadElement(data, ref position, out uint id, out long size))
        {
            int elementEnd = (int)Math.Min(end, position + Math.Max(0, size));
            if (id == TimestampScaleId) timestampScale = Math.Max(1, (long)ReadUnsigned(data, position, elementEnd));
            else if (id == DurationId) duration = ReadFloat(data, position, elementEnd);
            position = elementEnd;
        }
    }

    /// <summary>Read the track entries.</summary>
    private void ReadTracks(byte[] data, int position, int end)
    {
        // Each entry
        while (position < end && ReadElement(data, ref position, out uint id, out long size))
        {
            int elementEnd = (int)Math.Min(end, position + Math.Max(0, size));
            if (id == TrackEntryId && ReadTrack(data, position, elementEnd) is WebmTrack track) tracks.Add(track);
            position = elementEnd;
        }
    }

    /// <summary>Return the track of an entry, or null without a number or codec.</summary>
    private static WebmTrack? ReadTrack(byte[] data, int position, int end)
    {
        // The entry's elements, and its video or audio settings
        int number = 0, type = 0, width = 0, height = 0, channels = 1;
        double rate = 8000;
        string? codec = null;
        byte[]? codecPrivate = null;
        while (position < end && ReadElement(data, ref position, out uint id, out long size))
        {
            int elementEnd = (int)Math.Min(end, position + Math.Max(0, size));
            switch (id)
            {
                case TrackNumberId: number = (int)ReadUnsigned(data, position, elementEnd); break;
                case TrackTypeId: type = (int)ReadUnsigned(data, position, elementEnd); break;
                case CodecIdId: codec = Encoding.ASCII.GetString(data, position, elementEnd - position).TrimEnd('\0'); break;
                case CodecPrivateId: codecPrivate = data[position..elementEnd]; break;
                case VideoId:
                    for (int at = position; at < elementEnd && ReadElement(data, ref at, out uint child, out long childSize);)
                    {
                        int childEnd = (int)Math.Min(elementEnd, at + Math.Max(0, childSize));
                        if (child == PixelWidthId) width = (int)ReadUnsigned(data, at, childEnd);
                        else if (child == PixelHeightId) height = (int)ReadUnsigned(data, at, childEnd);
                        at = childEnd;
                    }
                    break;
                case AudioId:
                    for (int at = position; at < elementEnd && ReadElement(data, ref at, out uint child, out long childSize);)
                    {
                        int childEnd = (int)Math.Min(elementEnd, at + Math.Max(0, childSize));
                        if (child == SamplingFrequencyId) rate = ReadFloat(data, at, childEnd);
                        else if (child == ChannelsId) channels = (int)ReadUnsigned(data, at, childEnd);
                        at = childEnd;
                    }
                    break;
            }
            position = elementEnd;
        }
        // Return the track
        if (number == 0 || codec is null) return null;
        WebmTrackKind kind = type switch { 1 => WebmTrackKind.Video, 2 => WebmTrackKind.Audio, _ => WebmTrackKind.Other };
        return new WebmTrack(number, kind, codec, codecPrivate) { Width = width, Height = height, SampleRate = kind == WebmTrackKind.Audio ? rate : 0, Channels = kind == WebmTrackKind.Audio ? channels : 0 };
    }

    /// <summary>Read a cluster's timestamp and its blocks' frames.</summary>
    private void ReadCluster(byte[] data, int position, int end)
    {
        // The timestamp comes first; simple blocks, and blocks in groups, which are keyframes without a reference
        long clusterTime = 0;
        while (position < end && ReadElement(data, ref position, out uint id, out long size))
        {
            int elementEnd = (int)Math.Min(end, position + Math.Max(0, size));
            switch (id)
            {
                case TimestampId:
                    clusterTime = (long)ReadUnsigned(data, position, elementEnd);
                    break;
                case SimpleBlockId:
                    ReadBlock(data, position, elementEnd, clusterTime, null);
                    break;
                case BlockGroupId:
                    int block = -1, blockEnd = 0;
                    bool referenced = false;
                    for (int at = position; at < elementEnd && ReadElement(data, ref at, out uint child, out long childSize);)
                    {
                        int childEnd = (int)Math.Min(elementEnd, at + Math.Max(0, childSize));
                        if (child == BlockId) (block, blockEnd) = (at, childEnd);
                        else if (child == ReferenceBlockId) referenced = true;
                        at = childEnd;
                    }
                    if (block >= 0) ReadBlock(data, block, blockEnd, clusterTime, !referenced);
                    break;
            }
            position = elementEnd;
        }
    }

    /// <summary>Add the frames of a block: its track, its time from the cluster's, its keyframe flag (a simple block's own when <paramref name="keyframe"/> is null), and its lacing.</summary>
    private void ReadBlock(byte[] data, int position, int end, long clusterTime, bool? keyframe)
    {
        // Header: track number, relative time, flags
        if (!ReadVint(data, ref position, out long track, keepMarker: false) || position + 3 > end) return;
        long time = clusterTime + BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(position));
        byte flags = data[position + 2];
        position += 3;
        TimeSpan timestamp = TimeSpan.FromTicks(Math.Max(0, time) * timestampScale / 100);
        bool isKeyframe = keyframe ?? (flags & 0x80) != 0;
        int lacing = (flags >> 1) & 3;
        if (lacing == 0)
        {
            frames.Add(new WebmFrame((int)track, timestamp, isKeyframe, data.AsMemory(position, end - position)));
            return;
        }

        // Laced: the count, then the sizes of all but the last
        int count = data[position++] + 1;
        int[] sizes = new int[count];
        int known = 0;
        switch (lacing)
        {
            case 1:
                for (int i = 0; i < count - 1; i++)
                {
                    int laced = 0;
                    while (position < end && data[position] == 255) { laced += 255; position++; }
                    if (position >= end) return;
                    sizes[i] = laced + data[position++];
                    known += sizes[i];
                }
                break;
            case 2:
                Array.Fill(sizes, (end - position) / count);
                known = sizes[0] * (count - 1);
                break;
            default:
                if (!ReadVint(data, ref position, out long first, keepMarker: false)) return;
                sizes[0] = (int)first;
                known = sizes[0];
                for (int i = 1; i < count - 1; i++)
                {
                    int start = position;
                    if (!ReadVint(data, ref position, out long raw, keepMarker: false)) return;
                    int length = position - start;
                    long bias = (1L << (7 * length - 1)) - 1;
                    sizes[i] = sizes[i - 1] + (int)(raw - bias);
                    known += sizes[i];
                }
                break;
        }
        sizes[count - 1] = end - position - known;
        // Done: each laced frame, at the block's time
        for (int i = 0; i < count && sizes[i] >= 0 && position + sizes[i] <= end; position += sizes[i], i++) frames.Add(new WebmFrame((int)track, timestamp, isKeyframe, data.AsMemory(position, sizes[i])));
    }

    /// <summary>Return where a cluster of unknown size ends: at the first element that cannot be in a cluster, e.g. the next cluster.</summary>
    private static int FindClusterEnd(byte[] data, int position, int end)
    {
        // Each child of a cluster
        while (position < end)
        {
            int start = position;
            if (!ReadElement(data, ref position, out uint id, out long size) || !ClusterChildren.Contains(id) || size < 0) return start;
            position = (int)Math.Min(end, position + size);
        }
        // Return the end
        return end;
    }

    /// <summary>Read an element's ID, with its length marker, and its size, -1 for unknown; leave <paramref name="position"/> at its data.</summary>
    private static bool ReadElement(byte[] data, ref int position, out uint id, out long size)
    {
        // ID, then size
        size = 0;
        id = 0;
        if (!ReadVint(data, ref position, out long rawId, keepMarker: true) || rawId > uint.MaxValue) return false;
        id = (uint)rawId;
        int start = position;
        if (!ReadVint(data, ref position, out size, keepMarker: false)) return false;
        int length = position - start;
        // Done: all ones means unknown
        if (size == (1L << (7 * length)) - 1) size = -1;
        return true;
    }

    /// <summary>Read an EBML variable-length integer of 1 to 8 bytes; an ID keeps its length marker, a size does not.</summary>
    private static bool ReadVint(byte[] data, ref int position, out long value, bool keepMarker)
    {
        // The length from the leading zeros of the first byte
        value = 0;
        if (position >= data.Length || data[position] == 0) return false;
        int length = BitOperations.LeadingZeroCount((uint)data[position]) - 23;
        if (position + length > data.Length) return false;
        value = keepMarker ? data[position] : data[position] & (0xFF >> length);
        for (int i = 1; i < length; i++) value = value << 8 | data[position + i];
        position += length;
        // Done
        return true;
    }

    /// <summary>Read a big-endian unsigned integer of up to 8 bytes.</summary>
    private static ulong ReadUnsigned(byte[] data, int position, int end)
    {
        // Each byte
        ulong value = 0;
        for (int i = position; i < end && i < position + 8; i++) value = value << 8 | data[i];
        // Return it
        return value;
    }

    /// <summary>Read a big-endian float of 4 or 8 bytes.</summary>
    private static double ReadFloat(byte[] data, int position, int end) => (end - position) switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(position)),
        8 => BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(position)),
        _ => 0,
    };
}
