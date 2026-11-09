namespace Uniform.Matrix.Video.Vp8;

// The loop filter, RFC 6386 section 15
public sealed partial class Vp8Decoder
{
    /// <summary>Filter the edges of macroblock row <paramref name="row"/> in the current frame: per macroblock its left edge, inner vertical edges, top edge and inner horizontal edges.</summary>
    private void LoopFilterRow(int row)
    {
        // Planes at the row's first macroblock
        Vp8FrameBuffer frame = references[Vp8Modes.CurrentFrame]!;
        int stride = frame.Stride, uvStride = frame.UvStride;
        int y = frame.YOrigin + row * 16 * stride, uv = frame.UvOrigin + row * 8 * uvStride;
        for (int column = 0; column < columns; column++, y += 16, uv += 8)
        {
            // Levels of this macroblock; inner edges only where there is residual or per-subblock prediction
            ref MacroblockInfo mb = ref macroblocks[MacroblockIndex(row, column)];
            (int edgeLimit, int interior, int hev) = FilterParameters(in mb);
            if (edgeLimit == 0) continue;
            bool inner = mb.EobMask != 0 || mb.YMode == Vp8Modes.Split || mb.YMode == Vp8Modes.Subblocks;

            // Simple filter: luma only
            if (simpleFilter)
            {
                int mbLimit = (edgeLimit + 2) * 2 + interior, blockLimit = edgeLimit * 2 + interior;
                if (column > 0) SimpleEdge(frame.Y, y, 1, stride, mbLimit);
                if (inner)
                {
                    for (int x = 4; x < 16; x += 4) SimpleEdge(frame.Y, y + x, 1, stride, blockLimit);
                }
                if (row > 0) SimpleEdge(frame.Y, y, stride, 1, mbLimit);
                if (!inner) continue;
                for (int x = 4; x < 16; x += 4) SimpleEdge(frame.Y, y + x * stride, stride, 1, blockLimit);
                continue;
            }

            // Normal filter: left edge, inner vertical edges, top edge, inner horizontal edges
            if (column > 0)
            {
                MacroblockEdge(frame.Y, y, 1, stride, 16, edgeLimit + 2, interior, hev);
                MacroblockEdge(frame.U, uv, 1, uvStride, 8, edgeLimit + 2, interior, hev);
                MacroblockEdge(frame.V, uv, 1, uvStride, 8, edgeLimit + 2, interior, hev);
            }
            if (inner)
            {
                for (int x = 4; x < 16; x += 4) SubblockEdge(frame.Y, y + x, 1, stride, 16, edgeLimit, interior, hev);
                SubblockEdge(frame.U, uv + 4, 1, uvStride, 8, edgeLimit, interior, hev);
                SubblockEdge(frame.V, uv + 4, 1, uvStride, 8, edgeLimit, interior, hev);
            }
            if (row > 0)
            {
                MacroblockEdge(frame.Y, y, stride, 1, 16, edgeLimit + 2, interior, hev);
                MacroblockEdge(frame.U, uv, uvStride, 1, 8, edgeLimit + 2, interior, hev);
                MacroblockEdge(frame.V, uv, uvStride, 1, 8, edgeLimit + 2, interior, hev);
            }
            if (!inner) continue;
            for (int x = 4; x < 16; x += 4) SubblockEdge(frame.Y, y + x * stride, stride, 1, 16, edgeLimit, interior, hev);
            SubblockEdge(frame.U, uv + 4 * uvStride, uvStride, 1, 8, edgeLimit, interior, hev);
            SubblockEdge(frame.V, uv + 4 * uvStride, uvStride, 1, 8, edgeLimit, interior, hev);
        }
    }

    /// <summary>Return the edge limit, interior limit and high edge variance threshold of a macroblock: the frame's level with its segment's and the reference and mode deltas, and the sharpness.</summary>
    private (int EdgeLimit, int Interior, int Hev) FilterParameters(in MacroblockInfo mb)
    {
        // Level by segment, then the deltas
        int level = filterLevel;
        if (segmentationEnabled) level = segmentAbsolute ? segmentFilterLevel[mb.SegmentId] : level + segmentFilterLevel[mb.SegmentId];
        level = Math.Clamp(level, 0, 63);
        if (filterDeltasEnabled)
        {
            level += refFilterDeltas[mb.RefFrame];
            if (mb.RefFrame == Vp8Modes.CurrentFrame)
            {
                if (mb.YMode == Vp8Modes.Subblocks) level += modeFilterDeltas[0];
            }
            else if (mb.YMode == Vp8Modes.Zero) level += modeFilterDeltas[1];
            else if (mb.YMode == Vp8Modes.Split) level += modeFilterDeltas[3];
            else level += modeFilterDeltas[2];
            level = Math.Clamp(level, 0, 63);
        }

        // Interior limit by sharpness, at least 1
        int interior = level;
        if (sharpness != 0)
        {
            interior >>= sharpness > 4 ? 2 : 1;
            if (interior > 9 - sharpness) interior = 9 - sharpness;
        }
        if (interior < 1) interior = 1;

        // High edge variance threshold by level, higher in inter frames
        int hev = level >= 15 ? 1 : 0;
        if (level >= 40) hev++;
        if (level >= 20 && !keyframe) hev++;
        // Return parameters
        return (level, interior, hev);
    }

    /// <summary>Filter a macroblock edge of <paramref name="length"/> pixels at <paramref name="at"/>: <paramref name="step"/> crosses the edge, <paramref name="along"/> follows it.</summary>
    private static void MacroblockEdge(byte[] p, int at, int step, int along, int length, int edgeLimit, int interior, int hev)
    {
        // Each pixel across the edge
        for (int i = 0; i < length; i++, at += along)
        {
            if (!NormalThreshold(p, at, step, edgeLimit, interior)) continue;
            if (HighEdgeVariance(p, at, step, hev)) Common(p, at, step, true);
            else MacroblockFilter(p, at, step);
        }
    }

    /// <summary>Filter a subblock edge of <paramref name="length"/> pixels, as <see cref="MacroblockEdge"/>.</summary>
    private static void SubblockEdge(byte[] p, int at, int step, int along, int length, int edgeLimit, int interior, int hev)
    {
        // Each pixel across the edge
        for (int i = 0; i < length; i++, at += along)
        {
            if (NormalThreshold(p, at, step, edgeLimit, interior)) Common(p, at, step, HighEdgeVariance(p, at, step, hev));
        }
    }

    /// <summary>Filter a luma edge of 16 pixels with the simple filter.</summary>
    private static void SimpleEdge(byte[] p, int at, int step, int along, int limit)
    {
        // Each pixel across the edge
        for (int i = 0; i < 16; i++, at += along)
        {
            if (SimpleThreshold(p, at, step, limit)) Common(p, at, step, true);
        }
    }

    /// <summary>Return whether the difference across the edge is small enough to filter.</summary>
    private static bool SimpleThreshold(byte[] p, int at, int step, int limit) =>
        Math.Abs(p[at - step] - p[at]) * 2 + (Math.Abs(p[at - 2 * step] - p[at + step]) >> 1) <= limit;

    /// <summary>Return whether the edge and the pixels on both sides are smooth enough to filter.</summary>
    private static bool NormalThreshold(byte[] p, int at, int step, int edgeLimit, int interior)
    {
        // Edge, then each neighbouring pair
        int p3 = p[at - 4 * step], p2 = p[at - 3 * step], p1 = p[at - 2 * step], p0 = p[at - step];
        int q0 = p[at], q1 = p[at + step], q2 = p[at + 2 * step], q3 = p[at + 3 * step];
        // Return whether all are within
        return Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) <= 2 * edgeLimit + interior
            && Math.Abs(p3 - p2) <= interior && Math.Abs(p2 - p1) <= interior && Math.Abs(p1 - p0) <= interior
            && Math.Abs(q3 - q2) <= interior && Math.Abs(q2 - q1) <= interior && Math.Abs(q1 - q0) <= interior;
    }

    /// <summary>Return whether either side next to the edge changes by more than <paramref name="threshold"/>.</summary>
    private static bool HighEdgeVariance(byte[] p, int at, int step, int threshold) =>
        Math.Abs(p[at - 2 * step] - p[at - step]) > threshold || Math.Abs(p[at + step] - p[at]) > threshold;

    /// <summary>Adjust the two pixels next to the edge, and with <paramref name="outerTaps"/> unset also the next two.</summary>
    private static void Common(byte[] p, int at, int step, bool outerTaps)
    {
        // Filter value from the step across the edge
        int p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
        int a = 3 * (q0 - p0);
        if (outerTaps) a += SaturateInt8(p1 - q1);
        a = SaturateInt8(a);
        int f1 = (a + 4 > 127 ? 127 : a + 4) >> 3;
        int f2 = (a + 3 > 127 ? 127 : a + 3) >> 3;
        p[at - step] = Vp8Transform.Clamp(p0 + f2);
        p[at] = Vp8Transform.Clamp(q0 - f1);
        if (outerTaps) return;
        // Done: the outer pixels by half
        a = (f1 + 1) >> 1;
        p[at - 2 * step] = Vp8Transform.Clamp(p1 + a);
        p[at + step] = Vp8Transform.Clamp(q1 - a);
    }

    /// <summary>Adjust three pixels on each side of a macroblock edge, by 27, 18 and 9 of 128 of the filter value.</summary>
    private static void MacroblockFilter(byte[] p, int at, int step)
    {
        // Filter value
        int p2 = p[at - 3 * step], p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step], q2 = p[at + 2 * step];
        int w = SaturateInt8(SaturateInt8(p1 - q1) + 3 * (q0 - p0));

        // Nearest, then outward
        int a = (27 * w + 63) >> 7;
        p[at - step] = Vp8Transform.Clamp(p0 + a);
        p[at] = Vp8Transform.Clamp(q0 - a);
        a = (18 * w + 63) >> 7;
        p[at - 2 * step] = Vp8Transform.Clamp(p1 + a);
        p[at + step] = Vp8Transform.Clamp(q1 - a);
        a = (9 * w + 63) >> 7;
        p[at - 3 * step] = Vp8Transform.Clamp(p2 + a);
        // Done
        p[at + 2 * step] = Vp8Transform.Clamp(q2 - a);
    }

    /// <summary>Return <paramref name="x"/> saturated to a signed byte.</summary>
    private static int SaturateInt8(int x) => x < -128 ? -128 : x > 127 ? 127 : x;
}
