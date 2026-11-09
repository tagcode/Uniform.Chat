namespace Uniform.Matrix.Video.Vp8;

/// <summary>Decodes VP8 frames, <see href="https://www.rfc-editor.org/rfc/rfc6386">RFC 6386</see>, in pure C#: a port of the RFC's reference decoder, exact to libvpx, so that its test vectors decode bit for bit.</summary>
/// <remarks>
/// Give the frames in order, starting with a keyframe; a frame that cannot be decoded throws <see cref="InvalidDataException"/>, after which decoding needs a keyframe again.
/// Partial files:
/// <list type="bullet">
/// <item><description>Vp8Decoder.cs: state, frame headers, the macroblock row loop and the reference frames.</description></item>
/// <item><description>Vp8Decoder.Modes.cs: macroblock modes and motion vectors.</description></item>
/// <item><description>Vp8Decoder.Tokens.cs: DCT coefficients.</description></item>
/// <item><description>Vp8Decoder.Prediction.cs: intra and inter prediction, and adding the residual.</description></item>
/// <item><description>Vp8Decoder.LoopFilter.cs: the loop filter.</description></item>
/// </list>
/// </remarks>
public sealed partial class Vp8Decoder
{
    private const int CoefficientsPerMacroblock = 25 * 16;
    private readonly BoolDecoder header = new();
    private readonly BoolDecoder[] partitions = [new(), new(), new(), new(), new(), new(), new(), new()];
    private readonly Entropy entropy = new();
    private readonly Entropy savedEntropy = new();
    private readonly Vp8FrameBuffer?[] references = new Vp8FrameBuffer?[4];
    private readonly int[] signBias = new int[4];
    private readonly int[] segmentQuant = new int[4];
    private readonly int[] segmentFilterLevel = new int[4];
    private readonly byte[] segmentTreeProbs = [255, 255, 255];
    private readonly int[] refFilterDeltas = new int[4];
    private readonly int[] modeFilterDeltas = new int[4];
    private readonly short[] dequant = new short[4 * 6];
    private readonly int[] leftContext = new int[9];
    private byte[] data = [];
    private Vp8FrameBuffer[] storage = [];
    private MacroblockInfo[] macroblocks = [];
    private MotionVector[] splitMvs = [];
    private byte[] splitModes = [];
    private int[] aboveContext = [];
    private short[] coefficients = [];
    private bool hasKeyframe;

    // Frame header
    private bool keyframe;
    private int version;
    private int width;
    private int height;
    private int horizontalScale;
    private int verticalScale;
    private int columns;
    private int rows;

    // Segmentation, loop filter, partitions and quantizers
    private bool segmentationEnabled;
    private bool updateSegmentMap;
    private bool segmentAbsolute;
    private bool simpleFilter;
    private int filterLevel;
    private int sharpness;
    private bool filterDeltasEnabled;
    private int partitionCount;
    private int quantIndex;
    private int y1DcDelta;
    private int y2DcDelta;
    private int y2AcDelta;
    private int uvDcDelta;
    private int uvAcDelta;

    // References
    private bool refreshLast;
    private bool refreshGolden;
    private bool refreshAltref;
    private int copyToGolden;
    private int copyToAltref;
    private bool refreshEntropy;

    /// <summary>Probabilities a frame may update and the next frame inherit, unless it says otherwise.</summary>
    private sealed class Entropy
    {
        /// <summary>Coefficient probabilities by block type, band, context and node.</summary>
        public readonly byte[] CoeffProbs = new byte[4 * 8 * 3 * 11];
        /// <summary>Motion vector probabilities, rows then columns.</summary>
        public readonly byte[] MvProbs = new byte[2 * 19];
        /// <summary>Luma mode probabilities of inter frames.</summary>
        public readonly byte[] YModeProbs = new byte[4];
        /// <summary>Chroma mode probabilities of inter frames.</summary>
        public readonly byte[] UvModeProbs = new byte[3];
        /// <summary>Whether macroblocks tell whether they have coefficients.</summary>
        public bool SkipEnabled;
        /// <summary>Probability that a macroblock has coefficients.</summary>
        public int SkipProb;
        /// <summary>Probability that a macroblock is intra.</summary>
        public int ProbInter;
        /// <summary>Probability that an inter macroblock uses the last frame.</summary>
        public int ProbLast;
        /// <summary>Probability that an inter macroblock uses the golden frame rather than the alternate one.</summary>
        public int ProbGolden;

        /// <summary>Copy <paramref name="other"/> into this.</summary>
        public void CopyFrom(Entropy other)
        {
            // Arrays and values
            other.CoeffProbs.CopyTo(CoeffProbs, 0);
            other.MvProbs.CopyTo(MvProbs, 0);
            other.YModeProbs.CopyTo(YModeProbs, 0);
            other.UvModeProbs.CopyTo(UvModeProbs, 0);
            SkipEnabled = other.SkipEnabled;
            SkipProb = other.SkipProb;
            ProbInter = other.ProbInter;
            ProbLast = other.ProbLast;
            // Done
            ProbGolden = other.ProbGolden;
        }
    }

    /// <summary>Decode one frame; return the picture when the frame is shown, or null for a frame only kept as a reference.</summary>
    /// <exception cref="InvalidDataException">The frame is corrupt or unsupported, or no keyframe came first.</exception>
    public VideoFrame? Decode(ReadOnlySpan<byte> frame)
    {
        // A copy the partitions read from
        if (data.Length < frame.Length) data = new byte[frame.Length];
        frame.CopyTo(data);
        try
        {
            bool shown = DecodeFrame(frame.Length);
            hasKeyframe = true;
            // Return the picture
            return shown ? Picture() : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            // On data the reference decoder would also have rejected, or read past
            hasKeyframe = false;
            throw new InvalidDataException("Corrupt VP8 frame.", ex);
        }
        catch (InvalidDataException)
        {
            // On a frame header this decoder refuses
            hasKeyframe = false;
            throw;
        }
    }

    /// <summary>Decode the frame of <paramref name="size"/> bytes in <see cref="data"/>; return whether it is shown.</summary>
    private bool DecodeFrame(int size)
    {
        // Frame tag: keyframe flag, version, shown flag and first partition size; keyframes add a start code and the size
        if (size < 10) throw new InvalidDataException("VP8 frame too short.");
        int tag = data[0] | data[1] << 8 | data[2] << 16;
        keyframe = (tag & 1) == 0;
        int frameVersion = tag >> 1 & 7;
        bool shown = (tag >> 4 & 1) != 0;
        int firstPartition = tag >> 5 & 0x7FFFF;
        if (frameVersion > 3) throw new InvalidDataException($"Unsupported VP8 version {frameVersion}.");
        if (size <= firstPartition + (keyframe ? 10 : 3)) throw new InvalidDataException("Truncated VP8 frame.");
        if (!keyframe && !hasKeyframe) throw new InvalidDataException("A VP8 stream must start with a keyframe.");
        int offset = 3;
        bool sizeChanged = false;
        if (keyframe)
        {
            if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a) throw new InvalidDataException("Missing VP8 start code.");
            int dimensions = data[6] | data[7] << 8 | data[8] << 16 | data[9] << 24;
            int newWidth = dimensions & 0x3FFF, newHorizontalScale = dimensions >> 14 & 3, newHeight = dimensions >> 16 & 0x3FFF, newVerticalScale = dimensions >> 30 & 3;
            if (newWidth == 0 || newHeight == 0) throw new InvalidDataException("VP8 frame without a size.");
            sizeChanged = newWidth != width || newHeight != height || newHorizontalScale != horizontalScale || newVerticalScale != verticalScale || storage.Length == 0;
            (width, height, horizontalScale, verticalScale) = (newWidth, newHeight, newHorizontalScale, newVerticalScale);
            columns = (width + 15) / 16;
            rows = (height + 15) / 16;
            version = frameVersion;
            offset = 10;
        }

        // First partition: the frame's headers, then per macroblock its modes
        header.Init(data, offset, firstPartition);
        if (keyframe) header.ReadLiteral(2);
        ReadSegmentation();
        ReadLoopFilter();
        ReadPartitions(offset + firstPartition, size - offset - firstPartition);
        ReadQuantizers();
        ReadReferences();
        if (keyframe)
        {
            Vp8Tables.DefaultCoeffProbs.CopyTo(entropy.CoeffProbs);
            Vp8Tables.DefaultMvProbs.CopyTo(entropy.MvProbs);
            Vp8Tables.DefaultYModeProbs.CopyTo(entropy.YModeProbs);
            Vp8Tables.DefaultUvModeProbs.CopyTo(entropy.UvModeProbs);
        }
        if (!refreshEntropy) savedEntropy.CopyFrom(entropy);
        ReadEntropy();

        // Buffers for this size, the frame to decode into, and the quantizers
        if (sizeChanged) Allocate();
        PrepareFrame();
        InitDequant();

        // Macroblock rows: modes, coefficients, prediction, and the loop filter one row behind
        for (int row = 0, partition = 0; row < rows; row++)
        {
            DecodeModes(row);
            DecodeTokens(partitions[partition], row);
            Predict(row);
            if (filterLevel != 0 && row > 0) LoopFilterRow(row - 1);
            if (++partition == partitionCount) partition = 0;
        }
        if (filterLevel != 0) LoopFilterRow(rows - 1);

        // Probabilities only for this frame go back, then the references follow
        if (!refreshEntropy) entropy.CopyFrom(savedEntropy);
        UpdateReferences();
        // Return whether shown
        return shown;
    }

    /// <summary>Read the segmentation header, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-9.3">RFC 6386 9.3</see>.</summary>
    private void ReadSegmentation()
    {
        // Keyframes start from defaults
        if (keyframe)
        {
            segmentationEnabled = segmentAbsolute = false;
            Array.Clear(segmentQuant);
            Array.Clear(segmentFilterLevel);
        }
        segmentationEnabled = header.ReadBit() != 0;
        if (!segmentationEnabled)
        {
            updateSegmentMap = false;
            return;
        }

        // Values per segment, and the map's probabilities
        updateSegmentMap = header.ReadBit() != 0;
        bool updateData = header.ReadBit() != 0;
        if (updateData)
        {
            segmentAbsolute = header.ReadBit() != 0;
            for (int i = 0; i < 4; i++) segmentQuant[i] = header.ReadOptionalSigned(7);
            for (int i = 0; i < 4; i++) segmentFilterLevel[i] = header.ReadOptionalSigned(6);
        }
        if (!updateSegmentMap) return;
        // Done
        for (int i = 0; i < 3; i++) segmentTreeProbs[i] = (byte)(header.ReadBit() != 0 ? header.ReadLiteral(8) : 255);
    }

    /// <summary>Read the loop filter header, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-9.6">RFC 6386 9.6</see>; deltas not updated keep their values, as in libvpx.</summary>
    private void ReadLoopFilter()
    {
        // Keyframes reset the deltas
        if (keyframe)
        {
            Array.Clear(refFilterDeltas);
            Array.Clear(modeFilterDeltas);
        }
        simpleFilter = header.ReadBit() != 0;
        filterLevel = header.ReadLiteral(6);
        sharpness = header.ReadLiteral(3);
        filterDeltasEnabled = header.ReadBit() != 0;
        if (!filterDeltasEnabled || header.ReadBit() == 0) return;

        // Deltas by reference frame and by mode
        for (int i = 0; i < 4; i++)
        {
            if (header.ReadBit() != 0) refFilterDeltas[i] = header.ReadSigned(6);
        }
        // Done
        for (int i = 0; i < 4; i++)
        {
            if (header.ReadBit() != 0) modeFilterDeltas[i] = header.ReadSigned(6);
        }
    }

    /// <summary>Read the number of coefficient partitions and start a decoder on each, from the <paramref name="length"/> bytes at <paramref name="start"/>.</summary>
    private void ReadPartitions(int start, int length)
    {
        // Sizes of all but the last, 3 bytes each
        partitionCount = 1 << header.ReadLiteral(2);
        int sizes = 3 * (partitionCount - 1);
        if (length < sizes) throw new InvalidDataException("Truncated VP8 partition sizes.");
        int position = start + sizes;
        int left = length - sizes;
        for (int i = 0; i < partitionCount; i++)
        {
            int partitionSize = i < partitionCount - 1 ? data[start + i * 3] | data[start + i * 3 + 1] << 8 | data[start + i * 3 + 2] << 16 : left;
            if (partitionSize > left) throw new InvalidDataException($"Truncated VP8 partition {i}.");
            partitions[i].Init(data, position, partitionSize);
            position += partitionSize;
            left -= partitionSize;
        }
    }

    /// <summary>Read the quantizer indices, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-9.6">RFC 6386 9.6</see>.</summary>
    private void ReadQuantizers()
    {
        // Base index and the deltas
        quantIndex = header.ReadLiteral(7);
        y1DcDelta = header.ReadOptionalSigned(4);
        y2DcDelta = header.ReadOptionalSigned(4);
        y2AcDelta = header.ReadOptionalSigned(4);
        uvDcDelta = header.ReadOptionalSigned(4);
        // Done
        uvAcDelta = header.ReadOptionalSigned(4);
    }

    /// <summary>Read which reference frames this frame updates, and the probability refresh flag.</summary>
    private void ReadReferences()
    {
        // Keyframes refresh all
        refreshGolden = keyframe || header.ReadBit() != 0;
        refreshAltref = keyframe || header.ReadBit() != 0;
        copyToGolden = !keyframe && !refreshGolden ? header.ReadLiteral(2) : 0;
        copyToAltref = !keyframe && !refreshAltref ? header.ReadLiteral(2) : 0;
        signBias[Vp8Modes.GoldenFrame] = keyframe ? 0 : header.ReadBit();
        signBias[Vp8Modes.AltrefFrame] = keyframe ? 0 : header.ReadBit();
        refreshEntropy = header.ReadBit() != 0;
        // Done
        refreshLast = keyframe || header.ReadBit() != 0;
    }

    /// <summary>Read probability updates, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-13.4">RFC 6386 13.4</see>.</summary>
    private void ReadEntropy()
    {
        // Coefficient probabilities, each maybe replaced
        ReadOnlySpan<byte> updates = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < entropy.CoeffProbs.Length; i++)
        {
            if (header.Read(updates[i]) != 0) entropy.CoeffProbs[i] = (byte)header.ReadLiteral(8);
        }
        entropy.SkipEnabled = header.ReadBit() != 0;
        entropy.SkipProb = entropy.SkipEnabled ? header.ReadLiteral(8) : 0;
        if (keyframe) return;

        // Inter frames: reference, mode and motion vector probabilities
        entropy.ProbInter = header.ReadLiteral(8);
        entropy.ProbLast = header.ReadLiteral(8);
        entropy.ProbGolden = header.ReadLiteral(8);
        if (header.ReadBit() != 0)
        {
            for (int i = 0; i < 4; i++) entropy.YModeProbs[i] = (byte)header.ReadLiteral(8);
        }
        if (header.ReadBit() != 0)
        {
            for (int i = 0; i < 3; i++) entropy.UvModeProbs[i] = (byte)header.ReadLiteral(8);
        }
        ReadOnlySpan<byte> mvUpdates = Vp8Tables.MvUpdateProbs;
        // Done
        for (int i = 0; i < entropy.MvProbs.Length; i++)
        {
            if (header.Read(mvUpdates[i]) == 0) continue;
            int x = header.ReadLiteral(7);
            entropy.MvProbs[i] = (byte)(x != 0 ? x << 1 : 1);
        }
    }

    /// <summary>Allocate the buffers of the frame size: macroblock info with a border row and column, coefficient and context rows, and the frame buffers.</summary>
    private void Allocate()
    {
        // Per macroblock, with the border
        int count = (columns + 1) * (rows + 1);
        macroblocks = new MacroblockInfo[count];
        splitMvs = new MotionVector[count * 16];
        splitModes = new byte[count * 16];
        aboveContext = new int[columns * 9];
        coefficients = new short[columns * CoefficientsPerMacroblock];

        // Frames: the current one and up to three references
        storage = [new(columns, rows), new(columns, rows), new(columns, rows), new(columns, rows)];
        // Done
        Array.Clear(references);
    }

    /// <summary>Take a free frame buffer to decode into.</summary>
    private void PrepareFrame()
    {
        // The previous current frame stays only as a reference
        if (references[Vp8Modes.CurrentFrame] is Vp8FrameBuffer previous) previous.References--;
        Vp8FrameBuffer free = storage.FirstOrDefault(b => b.References == 0) ?? throw new InvalidOperationException("No free VP8 frame buffer.");
        free.References = 1;
        // Done
        references[Vp8Modes.CurrentFrame] = free;
    }

    /// <summary>Compute the dequantization factors of each segment, <see href="https://www.rfc-editor.org/rfc/rfc6386#section-14.1">RFC 6386 14.1</see>: Y1, UV and Y2, each DC then AC; every frame, as any delta may have changed.</summary>
    private void InitDequant()
    {
        // One segment, or four
        for (int i = 0; i < (segmentationEnabled ? 4 : 1); i++)
        {
            int q = quantIndex;
            if (segmentationEnabled) q = segmentAbsolute ? segmentQuant[i] : q + segmentQuant[i];
            int at = i * 6;
            dequant[at] = Dc(q + y1DcDelta);
            dequant[at + 1] = Ac(q);
            dequant[at + 2] = (short)Math.Min((int)Dc(q + uvDcDelta), 132);
            dequant[at + 3] = Ac(q + uvAcDelta);
            dequant[at + 4] = (short)(Dc(q + y2DcDelta) * 2);
            dequant[at + 5] = (short)Math.Max(Ac(q + y2AcDelta) * 155 / 100, 8);
        }

        // Quantizers by clamped index
        static short Dc(int q) => Vp8Tables.DcQuant[Math.Clamp(q, 0, 127)];
        static short Ac(int q) => Vp8Tables.AcQuant[Math.Clamp(q, 0, 127)];
    }

    /// <summary>Copy, golden and altref frames as the header says; then the last, golden and altref frames may become this one.</summary>
    private void UpdateReferences()
    {
        // Copies first, from the old references
        if (copyToAltref == 1) SetReference(Vp8Modes.AltrefFrame, references[Vp8Modes.LastFrame]);
        else if (copyToAltref == 2) SetReference(Vp8Modes.AltrefFrame, references[Vp8Modes.GoldenFrame]);
        if (copyToGolden == 1) SetReference(Vp8Modes.GoldenFrame, references[Vp8Modes.LastFrame]);
        else if (copyToGolden == 2) SetReference(Vp8Modes.GoldenFrame, references[Vp8Modes.AltrefFrame]);

        // Then the refreshes with this frame
        Vp8FrameBuffer current = references[Vp8Modes.CurrentFrame]!;
        if (refreshGolden) SetReference(Vp8Modes.GoldenFrame, current);
        if (refreshAltref) SetReference(Vp8Modes.AltrefFrame, current);
        // Done
        if (refreshLast) SetReference(Vp8Modes.LastFrame, current);
    }

    /// <summary>Point reference <paramref name="slot"/> at <paramref name="buffer"/>, counting references.</summary>
    private void SetReference(int slot, Vp8FrameBuffer? buffer)
    {
        // Release the old, hold the new
        if (references[slot] is Vp8FrameBuffer old) old.References--;
        if (buffer is not null) buffer.References++;
        // Done
        references[slot] = buffer;
    }

    /// <summary>Return the current frame's visible part as a picture.</summary>
    private VideoFrame Picture()
    {
        // Each plane's rows, without the border and the padding to whole macroblocks
        Vp8FrameBuffer frame = references[Vp8Modes.CurrentFrame]!;
        int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
        byte[] y = new byte[width * height], u = new byte[chromaWidth * chromaHeight], v = new byte[chromaWidth * chromaHeight];
        for (int row = 0; row < height; row++) Array.Copy(frame.Y, frame.YOrigin + row * frame.Stride, y, row * width, width);
        for (int row = 0; row < chromaHeight; row++)
        {
            Array.Copy(frame.U, frame.UvOrigin + row * frame.UvStride, u, row * chromaWidth, chromaWidth);
            Array.Copy(frame.V, frame.UvOrigin + row * frame.UvStride, v, row * chromaWidth, chromaWidth);
        }
        // Return picture
        return new VideoFrame(width, height, y, u, v);
    }

    /// <summary>Return the index of macroblock <paramref name="column"/> of <paramref name="row"/> in <see cref="macroblocks"/>; -1 means the border.</summary>
    private int MacroblockIndex(int row, int column) => (row + 1) * (columns + 1) + column + 1;
}
