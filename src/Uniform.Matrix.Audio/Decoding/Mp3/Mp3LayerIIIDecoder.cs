namespace Uniform.Matrix.Audio.Decoding.Mp3;

/// <summary>Decode MPEG-1, MPEG-2 and MPEG-2.5 layer III frames to float PCM. Keeps bit reservoir, overlap and synthesis state between frames.</summary>
/// <remarks>Steps per granule and channel: scalefactors, Huffman decoding, requantization, stereo processing, reordering, antialiasing, IMDCT with overlap-add, frequency inversion and polyphase synthesis.</remarks>
/// <see href="https://www.iso.org/standard/22412.html">ISO/IEC 11172-3</see>
/// <see href="https://www.iso.org/standard/26797.html">ISO/IEC 13818-3</see>
sealed class Mp3LayerIIIDecoder
{
    /// <summary>Lines per granule and channel.</summary>
    const int LineCount = 576;
    /// <summary>Largest Huffman magnitude including linbits, 15 + 2^13 - 1.</summary>
    const int MaxMagnitude = 8206;

    /// <summary>|i|^(4/3) for i = 0..<see cref="MaxMagnitude"/>.</summary>
    static readonly double[] power43 = Enumerable.Range(0, MaxMagnitude + 1).Select(i => Math.Pow(i, 4.0 / 3.0)).ToArray();
    /// <summary>Antialias butterfly coefficients cs and ca.</summary>
    static readonly double[] antialiasCs, antialiasCa;
    /// <summary>IMDCT windows of block types 0, 1, 2 (12 values) and 3.</summary>
    static readonly double[][] windows;
    /// <summary>36-point IMDCT cosines [i * 18 + k].</summary>
    static readonly double[] longCos = new double[36 * 18];
    /// <summary>12-point IMDCT cosines [i * 6 + k].</summary>
    static readonly double[] shortCos = new double[12 * 6];
    /// <summary>Synthesis matrix N[i * 32 + k] = cos((16 + i)(2k + 1)π / 64).</summary>
    static readonly double[] synthesisCos = new double[64 * 32];
    /// <summary>MPEG-1 intensity stereo factors of left and right channel by is_pos 0..6.</summary>
    static readonly double[] intensityLeft = new double[7], intensityRight = new double[7];

    /// <summary>Bit reservoir of main data, with valid length.</summary>
    readonly byte[] reservoir = new byte[4096];
    int reservoirLength;
    /// <summary>Scalefactors of long bands [channel][sfb 0..21], kept for scalefactor reuse (scfsi) of second granule.</summary>
    readonly int[][] longScalefactors = [new int[22], new int[22]];
    /// <summary>Scalefactors of short bands [channel][sfb 0..12 * 3 + window].</summary>
    readonly int[][] shortScalefactors = [new int[13 * 3], new int[13 * 3]];
    /// <summary>MPEG-2 illegal intensity position per long band and per short band, (1 &lt;&lt; slen) - 1, of right channel.</summary>
    readonly int[] longIllegal = new int[22], shortIllegal = new int[13];
    /// <summary>Quantized lines [channel][line] in coded order.</summary>
    readonly int[][] quantized = [new int[LineCount], new int[LineCount]];
    /// <summary>Requantized lines [channel][line] in coded order.</summary>
    readonly double[][] lines = [new double[LineCount], new double[LineCount]];
    /// <summary>Number of lines up to last non-zero quantized line, per channel.</summary>
    readonly int[] nonZeroCount = new int[2];
    /// <summary>Overlap of previous granule [channel][subband * 18 + i].</summary>
    readonly double[][] overlap = [new double[LineCount], new double[LineCount]];
    /// <summary>Synthesis V buffer [channel][1024] and its start offset.</summary>
    readonly double[][] synthesisBuffer = [new double[1024], new double[1024]];
    readonly int[] synthesisOffset = new int[2];
    /// <summary>Work buffers of reordering and time domain samples.</summary>
    readonly double[] reordered = new double[LineCount], timeSamples = new double[LineCount], imdct = new double[36];

    static Mp3LayerIIIDecoder()
    {
        // Antialias coefficients
        double[] c = [-0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037];
        antialiasCs = c.Select(value => 1 / Math.Sqrt(1 + value * value)).ToArray();
        antialiasCa = c.Select(value => value / Math.Sqrt(1 + value * value)).ToArray();
        // Windows of block types 0..3
        double[] normal = Enumerable.Range(0, 36).Select(i => Math.Sin(Math.PI / 36 * (i + 0.5))).ToArray();
        double[] start = Enumerable.Range(0, 36).Select(i => i < 18 ? Math.Sin(Math.PI / 36 * (i + 0.5)) : i < 24 ? 1 : i < 30 ? Math.Sin(Math.PI / 12 * (i - 18 + 0.5)) : 0).ToArray();
        double[] shortWindow = Enumerable.Range(0, 12).Select(i => Math.Sin(Math.PI / 12 * (i + 0.5))).ToArray();
        double[] stop = Enumerable.Range(0, 36).Select(i => i < 6 ? 0 : i < 12 ? Math.Sin(Math.PI / 12 * (i - 6 + 0.5)) : i < 18 ? 1 : Math.Sin(Math.PI / 36 * (i + 0.5))).ToArray();
        windows = [normal, start, shortWindow, stop];
        // IMDCT cosines
        for (int i = 0; i < 36; i++) for (int k = 0; k < 18; k++) longCos[i * 18 + k] = Math.Cos(Math.PI / 72 * (2 * i + 1 + 18) * (2 * k + 1));
        for (int i = 0; i < 12; i++) for (int k = 0; k < 6; k++) shortCos[i * 6 + k] = Math.Cos(Math.PI / 24 * (2 * i + 1 + 6) * (2 * k + 1));
        // Synthesis matrix
        for (int i = 0; i < 64; i++) for (int k = 0; k < 32; k++) synthesisCos[i * 32 + k] = Math.Cos((16 + i) * (2 * k + 1) * Math.PI / 64);
        // MPEG-1 intensity ratios tan(is_pos π / 12); is_pos 6 puts all to left
        for (int position = 0; position < 7; position++)
        {
            if (position == 6) { intensityLeft[position] = 1; intensityRight[position] = 0; continue; }
            double ratio = Math.Tan(position * Math.PI / 12);
            intensityLeft[position] = ratio / (1 + ratio);
            intensityRight[position] = 1 / (1 + ratio);
        }
    }

    /// <summary>Side information of one granule of one channel.</summary>
    struct GranuleInfo
    {
        /// <summary>Bits of scalefactors and Huffman data.</summary>
        public int Part23Length;
        /// <summary>Number of big value pairs.</summary>
        public int BigValues;
        /// <summary>Global gain.</summary>
        public int GlobalGain;
        /// <summary>Scalefactor compression index.</summary>
        public int ScalefacCompress;
        /// <summary>'true' if block type is other than 0.</summary>
        public bool WindowSwitching;
        /// <summary>Block type: 0 normal, 1 start, 2 short, 3 stop.</summary>
        public int BlockType;
        /// <summary>'true' if two lowest subbands are long and rest short.</summary>
        public bool Mixed;
        /// <summary>Huffman table of region 0, 1 and 2.</summary>
        public int Table0, Table1, Table2;
        /// <summary>Subblock gains of short windows.</summary>
        public int Gain0, Gain1, Gain2;
        /// <summary>Region 0 and 1 counts in long bands.</summary>
        public int Region0Count, Region1Count;
        /// <summary>Pre-emphasis flag.</summary>
        public bool Preflag;
        /// <summary>Scalefactor scale: 0 for step √2, 1 for step 2.</summary>
        public int ScalefacScale;
        /// <summary>Count1 table: 0 for table 32, 1 for table 33.</summary>
        public int Count1Table;
        /// <summary>'true' if block type is 2, short windows.</summary>
        public readonly bool IsShort => WindowSwitching && BlockType == 2;
    }

    /// <summary>Decode frame of <paramref name="header"/> at <paramref name="frame"/>, and append interleaved samples to <paramref name="output"/>.</summary>
    /// <param name="frame">Whole frame from header to end.</param>
    /// <param name="header">Parsed header of frame.</param>
    /// <param name="output">Interleaved float PCM, <see cref="Mp3FrameHeader.ChannelCount"/> channels.</param>
    public void DecodeFrame(ReadOnlySpan<byte> frame, Mp3FrameHeader header, IList<float> output)
    {
        // Side information
        int channels = header.ChannelCount, granules = header.GranuleCount;
        byte[] sideBytes = frame.Slice(header.SideInfoOffset, header.SideInfoLength).ToArray();
        Mp3BitReader side = new(sideBytes, 0);
        int mainDataBegin = side.ReadBits(header.IsMpeg1 ? 9 : 8);
        side.ReadBits(header.IsMpeg1 ? (channels == 1 ? 5 : 3) : (channels == 1 ? 1 : 2));
        int[,] scfsi = new int[2, 4];
        if (header.IsMpeg1) for (int ch = 0; ch < channels; ch++) for (int band = 0; band < 4; band++) scfsi[ch, band] = side.ReadBit();
        GranuleInfo[,] info = new GranuleInfo[2, 2];
        for (int gr = 0; gr < granules; gr++) for (int ch = 0; ch < channels; ch++) info[gr, ch] = ReadGranuleInfo(ref side, header.IsMpeg1);
        // Append main data to reservoir, keeping room for largest back reference
        ReadOnlySpan<byte> mainData = frame[(header.SideInfoOffset + header.SideInfoLength)..];
        int mainStart = reservoirLength - mainDataBegin;
        if (reservoirLength + mainData.Length > reservoir.Length)
        {
            int keep = Math.Min(reservoirLength, 512);
            Array.Copy(reservoir, reservoirLength - keep, reservoir, 0, keep);
            mainStart -= reservoirLength - keep;
            reservoirLength = keep;
        }
        mainData.CopyTo(reservoir.AsSpan(reservoirLength));
        reservoirLength += mainData.Length;
        // Missing reservoir, e.g. first frames after seek, gives silence
        if (mainStart < 0) { for (int i = 0; i < header.SampleCount * channels; i++) output.Add(0); return; }
        Mp3BitReader reader = new(reservoir, mainStart * 8);
        float[] pcm = new float[LineCount * channels];
        for (int gr = 0; gr < granules; gr++)
        {
            // Scalefactors and Huffman data of each channel
            for (int ch = 0; ch < channels; ch++)
            {
                int part2Start = reader.Position;
                if (header.IsMpeg1) ReadMpeg1Scalefactors(ref reader, info[gr, ch], ch, gr, scfsi);
                else ReadMpeg2Scalefactors(ref reader, ref info[gr, ch], ch, header);
                ReadHuffman(ref reader, info[gr, ch], ch, part2Start + info[gr, ch].Part23Length, header.SampleRateTableIndex);
                reader.Position = part2Start + info[gr, ch].Part23Length;
                Requantize(info[gr, ch], ch, header.SampleRateTableIndex);
            }
            // Joint stereo
            if (channels == 2 && header.Mode == 1 && header.ModeExtension != 0) ProcessStereo(header, info[gr, 0], info[gr, 1]);
            // Time domain and interleaving
            for (int ch = 0; ch < channels; ch++)
            {
                Synthesize(info[gr, ch], ch, header.SampleRateTableIndex);
                for (int i = 0; i < LineCount; i++) pcm[i * channels + ch] = (float)timeSamples[i];
            }
            foreach (float sample in pcm) output.Add(sample);
        }
    }

    /// <summary>Read side information of one granule and channel.</summary>
    static GranuleInfo ReadGranuleInfo(ref Mp3BitReader side, bool isMpeg1)
    {
        GranuleInfo info = new()
        {
            Part23Length = side.ReadBits(12),
            BigValues = Math.Min(side.ReadBits(9), 288),
            GlobalGain = side.ReadBits(8),
            ScalefacCompress = side.ReadBits(isMpeg1 ? 4 : 9),
            WindowSwitching = side.ReadBit() == 1,
        };
        if (info.WindowSwitching)
        {
            info.BlockType = side.ReadBits(2);
            info.Mixed = side.ReadBit() == 1;
            info.Table0 = side.ReadBits(5);
            info.Table1 = side.ReadBits(5);
            info.Gain0 = side.ReadBits(3);
            info.Gain1 = side.ReadBits(3);
            info.Gain2 = side.ReadBits(3);
            // Implicit regions: region 0 up to 36 lines, region 1 to end
            info.Region0Count = info.BlockType == 2 && !info.Mixed ? 8 : 7;
            info.Region1Count = 20 - info.Region0Count;
        }
        else
        {
            info.Table0 = side.ReadBits(5);
            info.Table1 = side.ReadBits(5);
            info.Table2 = side.ReadBits(5);
            info.Region0Count = side.ReadBits(4);
            info.Region1Count = side.ReadBits(3);
        }
        if (isMpeg1) info.Preflag = side.ReadBit() == 1;
        info.ScalefacScale = side.ReadBit();
        info.Count1Table = side.ReadBit();
        // Return info
        return info;
    }

    /// <summary>Read MPEG-1 scalefactors of <paramref name="ch"/> in granule <paramref name="gr"/>. Second granule may reuse long scalefactors of first by <paramref name="scfsi"/>.</summary>
    void ReadMpeg1Scalefactors(ref Mp3BitReader reader, GranuleInfo info, int ch, int gr, int[,] scfsi)
    {
        int slen1 = Mp3Tables.Slen1[info.ScalefacCompress], slen2 = Mp3Tables.Slen2[info.ScalefacCompress];
        int[] longs = longScalefactors[ch], shorts = shortScalefactors[ch];
        if (info.IsShort)
        {
            // Mixed: long bands 0..7, then short bands from 3
            int firstShort = 0;
            if (info.Mixed) { for (int sfb = 0; sfb < 8; sfb++) longs[sfb] = reader.ReadBits(slen1); firstShort = 3; }
            for (int sfb = firstShort; sfb < 12; sfb++) for (int window = 0; window < 3; window++) shorts[sfb * 3 + window] = reader.ReadBits(sfb < 6 ? slen1 : slen2);
            for (int window = 0; window < 3; window++) shorts[12 * 3 + window] = 0;
            return;
        }
        // Long bands in 4 groups, reused from first granule if scfsi is set
        int[] groupStart = [0, 6, 11, 16, 21];
        for (int group = 0; group < 4; group++)
        {
            if (gr == 1 && scfsi[ch, group] == 1) continue;
            for (int sfb = groupStart[group]; sfb < groupStart[group + 1]; sfb++) longs[sfb] = reader.ReadBits(group < 2 ? slen1 : slen2);
        }
        longs[21] = 0;
    }

    /// <summary>Read MPEG-2 and MPEG-2.5 scalefactors of <paramref name="ch"/>, with partitions of ISO/IEC 13818-3. Sets preflag of <paramref name="info"/>.</summary>
    void ReadMpeg2Scalefactors(ref Mp3BitReader reader, ref GranuleInfo info, int ch, Mp3FrameHeader header)
    {
        // Bit lengths and partition table
        int compress = info.ScalefacCompress;
        int[] slen = new int[4];
        int table;
        bool intensityChannel = ch == 1 && header.Mode == 1 && (header.ModeExtension & 1) != 0;
        if (!intensityChannel)
        {
            if (compress < 400) { slen = [(compress >> 4) / 5, (compress >> 4) % 5, (compress & 15) >> 2, compress & 3]; info.Preflag = false; table = 0; }
            else if (compress < 500) { compress -= 400; slen = [(compress >> 2) / 5, (compress >> 2) % 5, compress & 3, 0]; info.Preflag = false; table = 1; }
            else { compress -= 500; slen = [compress / 3, compress % 3, 0, 0]; info.Preflag = true; table = 2; }
        }
        else
        {
            int half = compress >> 1;
            if (half < 180) { slen = [half / 36, (half % 36) / 6, (half % 36) % 6, 0]; table = 3; }
            else if (half < 244) { half -= 180; slen = [(half % 64) >> 4, (half % 16) >> 2, half % 4, 0]; table = 4; }
            else { half -= 244; slen = [half / 3, half % 3, 0, 0]; table = 5; }
            info.Preflag = false;
        }
        int kind = info.IsShort ? (info.Mixed ? 2 : 1) : 0;
        int[] counts = Mp3Tables.Mpeg2PartitionCounts[table][kind];
        // Slots in transmission order: long bands, then short bands and windows
        List<(bool IsShort, int Sfb, int Window)> slots = [];
        if (kind == 0) for (int sfb = 0; sfb < 21; sfb++) slots.Add((false, sfb, 0));
        if (kind == 2) for (int sfb = 0; sfb < 6; sfb++) slots.Add((false, sfb, 0));
        if (kind != 0) for (int sfb = kind == 2 ? 3 : 0; sfb < 12; sfb++) for (int window = 0; window < 3; window++) slots.Add((true, sfb, window));
        int[] longs = longScalefactors[ch], shorts = shortScalefactors[ch];
        Array.Clear(longs);
        Array.Clear(shorts);
        // Read partitions
        int slotIx = 0;
        for (int partition = 0; partition < 4; partition++)
        {
            for (int i = 0; i < counts[partition] && slotIx < slots.Count; i++, slotIx++)
            {
                (bool isShort, int sfb, int window) = slots[slotIx];
                int value = reader.ReadBits(slen[partition]);
                int illegal = (1 << slen[partition]) - 1;
                if (isShort) { shorts[sfb * 3 + window] = value; shortIllegal[sfb] = illegal; }
                else { longs[sfb] = value; longIllegal[sfb] = illegal; }
            }
        }
        // Bands without scalefactors use those of band below for intensity stereo
        longIllegal[21] = longIllegal[20];
        shortIllegal[12] = shortIllegal[11];
    }

    /// <summary>Decode Huffman coded lines of <paramref name="ch"/> until bit <paramref name="end"/>.</summary>
    void ReadHuffman(ref Mp3BitReader reader, GranuleInfo info, int ch, int end, int rateIndex)
    {
        int[] values = quantized[ch];
        Array.Clear(values);
        int[] longBands = Mp3Tables.LongBands[rateIndex];
        // Region boundaries
        int region1Start, region2Start;
        if (info.WindowSwitching) { region1Start = info.BlockType == 2 && !info.Mixed ? Mp3Tables.ShortBands[rateIndex][3] * 3 : longBands[8]; region2Start = LineCount; }
        else { region1Start = longBands[Math.Min(info.Region0Count + 1, 22)]; region2Start = longBands[Math.Min(info.Region0Count + info.Region1Count + 2, 22)]; }
        int bigEnd = Math.Min(info.BigValues * 2, LineCount);
        // Big values in pairs
        int line = 0;
        for (; line < bigEnd; line += 2)
        {
            int tableNumber = line < region1Start ? info.Table0 : line < region2Start ? info.Table1 : info.Table2;
            int tableIx = Mp3Tables.BigValueTableIndex[tableNumber];
            if (tableIx < 0) continue;
            int size = Mp3Tables.BigValueTableSize[tableNumber], linBits = Mp3Tables.LinBits[tableNumber];
            int pair = Mp3Huffman.Decode(tableIx, ref reader);
            int x = pair / size, y = pair % size;
            if (x == 15 && linBits > 0) x += reader.ReadBits(linBits);
            if (x != 0 && reader.ReadBit() == 1) x = -x;
            if (y == 15 && linBits > 0) y += reader.ReadBits(linBits);
            if (y != 0 && reader.ReadBit() == 1) y = -y;
            values[line] = x;
            values[line + 1] = y;
        }
        // Count1 quadruples until end of part 2 and 3
        int count1Table = info.Count1Table == 0 ? 15 : 16;
        while (line + 4 <= LineCount && reader.Position < end)
        {
            int quad = Mp3Huffman.Decode(count1Table, ref reader);
            Span<int> four = [(quad >> 3) & 1, (quad >> 2) & 1, (quad >> 1) & 1, quad & 1];
            for (int i = 0; i < 4; i++) if (four[i] != 0 && reader.ReadBit() == 1) four[i] = -1;
            // Quadruple that overruns end is not part of the data
            if (reader.Position > end) break;
            for (int i = 0; i < 4; i++) values[line + i] = four[i];
            line += 4;
        }
        // Last non-zero line
        int count = line;
        while (count > 0 && values[count - 1] == 0) count--;
        nonZeroCount[ch] = count;
    }

    /// <summary>Requantize lines of <paramref name="ch"/> with gains and scalefactors, in coded order.</summary>
    void Requantize(GranuleInfo info, int ch, int rateIndex)
    {
        int[] values = quantized[ch], longs = longScalefactors[ch], shorts = shortScalefactors[ch];
        double[] result = lines[ch];
        Array.Clear(result);
        double scale = 0.5 * (1 + info.ScalefacScale);
        int[] longBands = Mp3Tables.LongBands[rateIndex], shortBands = Mp3Tables.ShortBands[rateIndex];
        // Long part: whole granule, or 36 lowest lines of mixed blocks
        int longEnd = info.IsShort ? (info.Mixed ? longBands[rateIndex < 3 ? 8 : 6] : 0) : LineCount;
        for (int sfb = 0; sfb < 22 && longBands[sfb] < longEnd; sfb++)
        {
            double factor = Math.Pow(2, 0.25 * (info.GlobalGain - 210) - scale * (longs[sfb] + (info.Preflag ? Mp3Tables.Pretab[sfb] : 0)));
            for (int i = longBands[sfb]; i < Math.Min(longBands[sfb + 1], longEnd); i++) result[i] = Dequantize(values[i], factor);
        }
        if (!info.IsShort) return;
        // Short part in order band, window, line
        int[] gains = [info.Gain0, info.Gain1, info.Gain2];
        int index = longEnd;
        for (int sfb = info.Mixed ? 3 : 0; sfb < 13; sfb++)
        {
            int width = shortBands[sfb + 1] - shortBands[sfb];
            for (int window = 0; window < 3; window++)
            {
                double factor = Math.Pow(2, 0.25 * (info.GlobalGain - 210 - 8 * gains[window]) - scale * shorts[sfb * 3 + window]);
                for (int i = 0; i < width && index < LineCount; i++, index++) result[index] = Dequantize(values[index], factor);
            }
        }
    }

    /// <summary>Calculate sign(<paramref name="value"/>) |value|^(4/3) × <paramref name="factor"/>.</summary>
    static double Dequantize(int value, double factor) => value == 0 ? 0 : value > 0 ? power43[Math.Min(value, MaxMagnitude)] * factor : -power43[Math.Min(-value, MaxMagnitude)] * factor;

    /// <summary>Apply mid/side and intensity stereo of joint stereo frame to requantized lines of both channels.</summary>
    void ProcessStereo(Mp3FrameHeader header, GranuleInfo left, GranuleInfo right)
    {
        bool midSide = (header.ModeExtension & 2) != 0, intensity = (header.ModeExtension & 1) != 0;
        double[] l = lines[0], r = lines[1];
        int rateIndex = header.SampleRateTableIndex;
        int[] longBands = Mp3Tables.LongBands[rateIndex], shortBands = Mp3Tables.ShortBands[rateIndex];
        // Intensity factors per line, NaN where line is not intensity coded
        Span<double> leftFactor = stackalloc double[LineCount], rightFactor = stackalloc double[LineCount];
        leftFactor.Fill(double.NaN);
        if (intensity) MarkIntensity(header, right, longBands, shortBands, leftFactor, rightFactor);
        // Mid/side below intensity, intensity above
        for (int i = 0; i < LineCount; i++)
        {
            if (!double.IsNaN(leftFactor[i])) { double value = l[i]; l[i] = value * leftFactor[i]; r[i] = value * rightFactor[i]; continue; }
            if (!midSide) continue;
            double mid = l[i], sideValue = r[i];
            l[i] = (mid + sideValue) * Math.Sqrt(0.5);
            r[i] = (mid - sideValue) * Math.Sqrt(0.5);
        }
    }

    /// <summary>Mark intensity coded lines with factors of left and right channel. Intensity starts above highest band where right channel has non-zero lines.</summary>
    void MarkIntensity(Mp3FrameHeader header, GranuleInfo right, int[] longBands, int[] shortBands, Span<double> leftFactor, Span<double> rightFactor)
    {
        int[] rightValues = quantized[1], longs = longScalefactors[1], shorts = shortScalefactors[1];
        if (!right.IsShort)
        {
            // Long: first band at or above last non-zero line
            int start = 0;
            while (start < 22 && longBands[start] < nonZeroCount[1]) start++;
            for (int sfb = start; sfb < 22; sfb++)
            {
                int position = longs[Math.Min(sfb, 20)];
                if (!TryIntensityFactors(header, right, position, header.IsMpeg1 ? 7 : longIllegal[sfb], out double kl, out double kr)) continue;
                for (int i = longBands[sfb]; i < longBands[sfb + 1]; i++) { leftFactor[i] = kl; rightFactor[i] = kr; }
            }
            return;
        }
        // Short: each window separately, above highest band with non-zero line; long part of mixed blocks is not intensity coded
        int firstSfb = right.Mixed ? 3 : 0, longEnd = right.Mixed ? shortBands[3] * 3 : 0;
        for (int window = 0; window < 3; window++)
        {
            int start = firstSfb;
            for (int sfb = 12, index; sfb >= firstSfb; sfb--)
            {
                int width = shortBands[sfb + 1] - shortBands[sfb];
                index = longEnd + (shortBands[sfb] - shortBands[firstSfb]) * 3 + window * width;
                bool nonZero = false;
                for (int i = 0; i < width && index + i < LineCount; i++) if (rightValues[index + i] != 0) { nonZero = true; break; }
                if (nonZero) { start = sfb + 1; break; }
            }
            for (int sfb = start; sfb < 13; sfb++)
            {
                int position = shorts[Math.Min(sfb, 11) * 3 + window];
                if (!TryIntensityFactors(header, right, position, header.IsMpeg1 ? 7 : shortIllegal[sfb], out double kl, out double kr)) continue;
                int width = shortBands[sfb + 1] - shortBands[sfb];
                int index = longEnd + (shortBands[sfb] - shortBands[firstSfb]) * 3 + window * width;
                for (int i = 0; i < width && index + i < LineCount; i++) { leftFactor[index + i] = kl; rightFactor[index + i] = kr; }
            }
        }
    }

    /// <summary>Get intensity factors of left and right channel for intensity <paramref name="position"/>.</summary>
    /// <returns>'false' if position is illegal, so band is coded as mid/side or plain stereo.</returns>
    static bool TryIntensityFactors(Mp3FrameHeader header, GranuleInfo right, int position, int illegal, out double kl, out double kr)
    {
        kl = kr = 0;
        if (position == illegal) return false;
        if (header.IsMpeg1) { kl = intensityLeft[position]; kr = intensityRight[position]; return true; }
        // MPEG-2: powers of io, by lowest bit of scalefac_compress
        double io = (right.ScalefacCompress & 1) == 1 ? Math.Sqrt(0.5) : Math.Pow(0.5, 0.25);
        if (position == 0) { kl = kr = 1; return true; }
        if ((position & 1) == 1) { kl = Math.Pow(io, (position + 1) / 2); kr = 1; return true; }
        kl = 1;
        kr = Math.Pow(io, position / 2);
        // Done
        return true;
    }

    /// <summary>Convert requantized lines of <paramref name="ch"/> to 576 time samples in <see cref="timeSamples"/>.</summary>
    void Synthesize(GranuleInfo info, int ch, int rateIndex)
    {
        double[] source = lines[ch], prev = overlap[ch];
        int[] shortBands = Mp3Tables.ShortBands[rateIndex];
        // Reorder short blocks to [subband][window][6 lines]
        Array.Copy(source, reordered, LineCount);
        if (info.IsShort)
        {
            int firstSfb = info.Mixed ? 3 : 0, index = info.Mixed ? shortBands[3] * 3 : 0;
            for (int sfb = firstSfb; sfb < 13; sfb++)
            {
                int width = shortBands[sfb + 1] - shortBands[sfb];
                for (int window = 0; window < 3; window++)
                {
                    for (int i = 0; i < width && index < LineCount; i++, index++)
                    {
                        int line = shortBands[sfb] + i;
                        reordered[(line / 6) * 18 + window * 6 + line % 6] = source[index];
                    }
                }
            }
        }
        // Antialias between long subbands
        int aliasLimit = info.IsShort ? (info.Mixed ? 2 : 1) : 32;
        for (int sb = 1; sb < aliasLimit; sb++)
        {
            for (int i = 0; i < 8; i++)
            {
                int lower = sb * 18 - 1 - i, upper = sb * 18 + i;
                double a = reordered[lower], b = reordered[upper];
                reordered[lower] = a * antialiasCs[i] - b * antialiasCa[i];
                reordered[upper] = b * antialiasCs[i] + a * antialiasCa[i];
            }
        }
        // IMDCT, window and overlap-add per subband
        for (int sb = 0; sb < 32; sb++)
        {
            bool shortSubband = info.IsShort && (!info.Mixed || sb >= 2);
            Array.Clear(imdct);
            if (shortSubband)
            {
                double[] window = windows[2];
                for (int w = 0; w < 3; w++)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        double sum = 0;
                        for (int k = 0; k < 6; k++) sum += reordered[sb * 18 + w * 6 + k] * shortCos[i * 6 + k];
                        imdct[6 + w * 6 + i] += sum * window[i];
                    }
                }
            }
            else
            {
                double[] window = windows[info.Mixed && sb < 2 ? 0 : info.BlockType];
                for (int i = 0; i < 36; i++)
                {
                    double sum = 0;
                    for (int k = 0; k < 18; k++) sum += reordered[sb * 18 + k] * longCos[i * 18 + k];
                    imdct[i] = sum * window[i];
                }
            }
            for (int i = 0; i < 18; i++)
            {
                double value = imdct[i] + prev[sb * 18 + i];
                prev[sb * 18 + i] = imdct[i + 18];
                // Frequency inversion of odd subbands
                timeSamples[sb * 18 + i] = (sb & 1) == 1 && (i & 1) == 1 ? -value : value;
            }
        }
        // Polyphase synthesis, 18 slots of 32 subband samples to 32 time samples
        double[] v = synthesisBuffer[ch];
        Span<double> slot = stackalloc double[32];
        Span<double> output = stackalloc double[LineCount];
        for (int t = 0; t < 18; t++)
        {
            for (int sb = 0; sb < 32; sb++) slot[sb] = timeSamples[sb * 18 + t];
            int offset = synthesisOffset[ch] = (synthesisOffset[ch] - 64) & 1023;
            for (int i = 0; i < 64; i++)
            {
                double sum = 0;
                for (int k = 0; k < 32; k++) sum += synthesisCos[i * 32 + k] * slot[k];
                v[(offset + i) & 1023] = sum;
            }
            for (int j = 0; j < 32; j++)
            {
                double sum = 0;
                for (int i = 0; i < 8; i++)
                {
                    sum += v[(offset + i * 128 + j) & 1023] * Mp3Tables.SynthesisWindow[i * 64 + j];
                    sum += v[(offset + i * 128 + 96 + j) & 1023] * Mp3Tables.SynthesisWindow[i * 64 + 32 + j];
                }
                output[t * 32 + j] = sum;
            }
        }
        output.CopyTo(timeSamples);
    }
}
