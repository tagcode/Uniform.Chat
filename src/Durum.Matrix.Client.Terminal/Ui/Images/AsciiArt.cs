namespace Durum.Matrix.Client.Terminal.Ui.Images;

using System.Text;

/// <summary>How <see cref="AsciiArt"/> draws an image.</summary>
public enum ImagePreviewStyle
{
    /// <summary>Shade blocks <c> ░▒▓█</c>, dithered between the five levels.</summary>
    Blocks,
    /// <summary>One character per cell from the 70-step ASCII ramp.</summary>
    Ascii,
    /// <summary>Braille patterns: 2×4 dots per cell, dithered, for about eight times the detail.</summary>
    Braille,
}

/// <summary>Renders images as text: shade blocks, ASCII art or Braille dots.</summary>
public static class AsciiArt
{
    /// <summary>Shade blocks from empty to full, used by <see cref="ImagePreviewStyle.Blocks"/>.</summary>
    public const string BlockRamp = " ░▒▓█";

    /// <summary>Characters from sparse to dense, the classic 70-step ramp, used by <see cref="ImagePreviewStyle.Ascii"/>.</summary>
    public const string AsciiRamp = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";

    /// <summary>Height of a terminal cell relative to its width; cells are about twice as tall as wide.</summary>
    public const double CellAspect = 2.0;

    /// <summary>First Braille pattern; dots add bits to it.</summary>
    private const int BrailleBase = 0x2800;

    /// <summary>Braille dot bits by position within a cell, as [x, y] for 2 columns and 4 rows.</summary>
    private static readonly int[,] BrailleBits = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };

    /// <summary>Render <paramref name="image"/> within <paramref name="maxColumns"/>×<paramref name="maxRows"/> cells, keeping its proportions.</summary>
    /// <param name="image">Image to render.</param>
    /// <param name="maxColumns">Widest result in columns.</param>
    /// <param name="maxRows">Tallest result in rows.</param>
    /// <param name="darkBackground">Whether bright pixels are drawn, as on a dark terminal; otherwise dark pixels are. Transparent pixels stay blank either way.</param>
    /// <param name="style">Shade blocks, ASCII ramp or Braille dots.</param>
    /// <example><code><![CDATA[foreach (string line in AsciiArt.Render(image, 120, 30, style: ImagePreviewStyle.Braille)) Console.WriteLine(line);]]></code></example>
    public static IReadOnlyList<string> Render(DecodedImage image, int maxColumns, int maxRows, bool darkBackground = true, ImagePreviewStyle style = ImagePreviewStyle.Blocks)
    {
        // Fit to the box, correcting for tall cells
        (int columns, int rows) = FitCells(image.Width, image.Height, maxColumns, maxRows);
        // Return lines in the chosen style; few shade levels need dithering, the long ramp does not
        return style switch
        {
            ImagePreviewStyle.Braille => RenderBraille(image, columns, rows, darkBackground),
            ImagePreviewStyle.Ascii => RenderRamp(image, columns, rows, darkBackground, AsciiRamp, dither: false),
            _ => RenderRamp(image, columns, rows, darkBackground, BlockRamp, dither: true),
        };
    }

    /// <summary>Largest upscaling in columns per source pixel, so small images grow with the window but icons do not fill it.</summary>
    public const int MaxUpscale = 2;

    /// <summary>Return the cell size of a <paramref name="width"/>×<paramref name="height"/> pixel image fitted into <paramref name="maxColumns"/>×<paramref name="maxRows"/>.</summary>
    public static (int Columns, int Rows) FitCells(int width, int height, int maxColumns, int maxRows)
    {
        // Rows per column for this image on tall cells
        double rowsPerColumn = height / (double)width / CellAspect;
        int columns = Math.Max(1, Math.Min(maxColumns, width * MaxUpscale));
        int rows = Math.Max(1, (int)Math.Round(columns * rowsPerColumn));

        // Too tall: shrink width to fit the rows
        if (rows > maxRows)
        {
            rows = Math.Max(1, maxRows);
            columns = Math.Max(1, Math.Min(columns, (int)Math.Round(rows / rowsPerColumn)));
        }
        // Return size
        return (columns, rows);
    }

    /// <summary>Render one <paramref name="ramp"/> character per cell, optionally dithering between its levels.</summary>
    private static List<string> RenderRamp(DecodedImage image, int columns, int rows, bool darkBackground, string ramp, bool dither)
    {
        // Density per cell
        double[,] density = SampleGrid(image, columns, rows, darkBackground);
        StretchContrast(density);
        if (dither) Dither(density, ramp.Length);

        // Map to the ramp
        List<string> lines = new(rows);
        StringBuilder line = new(columns);
        for (int y = 0; y < rows; y++)
        {
            line.Clear();
            for (int x = 0; x < columns; x++) line.Append(ramp[(int)Math.Round(Math.Clamp(density[x, y], 0, 1) * (ramp.Length - 1))]);
            lines.Add(line.ToString().TrimEnd());
        }
        // Return lines
        return lines;
    }

    /// <summary>Render 2×4 dithered dots per cell as Braille patterns.</summary>
    private static List<string> RenderBraille(DecodedImage image, int columns, int rows, bool darkBackground)
    {
        // Density per dot, dithered to on or off
        double[,] dots = SampleGrid(image, columns * 2, rows * 4, darkBackground);
        StretchContrast(dots);
        Dither(dots, 2);

        // Combine each 2×4 block into one pattern
        List<string> lines = new(rows);
        StringBuilder line = new(columns);
        for (int row = 0; row < rows; row++)
        {
            line.Clear();
            for (int column = 0; column < columns; column++)
            {
                int bits = 0;
                for (int dy = 0; dy < 4; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        if (dots[column * 2 + dx, row * 4 + dy] >= 0.5) bits |= BrailleBits[dx, dy];
                line.Append(bits == 0 ? ' ' : (char)(BrailleBase + bits));
            }
            lines.Add(line.ToString().TrimEnd());
        }
        // Return lines
        return lines;
    }

    /// <summary>Sample <paramref name="width"/>×<paramref name="height"/> densities from 0 to 1, as [x, y]; transparent areas are 0.</summary>
    private static double[,] SampleGrid(DecodedImage image, int width, int height, bool darkBackground)
    {
        // Average each block of source pixels
        double[,] grid = new double[width, height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (double brightness, double coverage) = Sample(image, x * image.Width / width, (x + 1) * image.Width / width, y * image.Height / height, (y + 1) * image.Height / height);
                grid[x, y] = darkBackground ? brightness : coverage - brightness;
            }
        // Return grid
        return grid;
    }

    /// <summary>Spread the 2nd to 98th percentile of <paramref name="grid"/> over 0 to 1, so dim or flat images use the whole range; nearly uniform images are left as they are.</summary>
    private static void StretchContrast(double[,] grid)
    {
        // Find the range ignoring outliers
        List<double> values = [.. grid.Cast<double>()];
        values.Sort();
        double low = values[(int)((values.Count - 1) * 0.02)];
        double high = values[(int)((values.Count - 1) * 0.98)];
        if (high - low < 0.05) return;

        // Rescale and clamp
        for (int y = 0; y < grid.GetLength(1); y++)
            for (int x = 0; x < grid.GetLength(0); x++)
                grid[x, y] = Math.Clamp((grid[x, y] - low) / (high - low), 0, 1);
    }

    /// <summary>Quantize densities to <paramref name="levels"/> even steps from 0 to 1 with Floyd–Steinberg error diffusion, so in-between shades become patterns.</summary>
    private static void Dither(double[,] grid, int levels)
    {
        // Threshold each value and push the error to unvisited neighbors
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double old = grid[x, y];
                double value = Math.Round(Math.Clamp(old, 0, 1) * (levels - 1)) / (levels - 1);
                grid[x, y] = value;
                double error = old - value;
                if (x + 1 < width) grid[x + 1, y] += error * 7 / 16;
                if (y + 1 >= height) continue;
                if (x > 0) grid[x - 1, y + 1] += error * 3 / 16;
                grid[x, y + 1] += error * 5 / 16;
                if (x + 1 < width) grid[x + 1, y + 1] += error * 1 / 16;
            }
    }

    /// <summary>Return the average alpha-weighted brightness and the average alpha, both from 0 to 1, of a pixel block; transparent pixels add to neither.</summary>
    private static (double Brightness, double Coverage) Sample(DecodedImage image, int x0, int x1, int y0, int y1)
    {
        // At least one pixel per block
        x1 = Math.Max(x1, x0 + 1);
        y1 = Math.Max(y1, y0 + 1);

        // Sum luminance weighted by alpha, and alpha
        double brightness = 0;
        double coverage = 0;
        int count = 0;
        for (int y = y0; y < y1 && y < image.Height; y++)
            for (int x = x0; x < x1 && x < image.Width; x++)
            {
                (byte r, byte g, byte b, byte a) = image.GetPixel(x, y);
                double alpha = a / 255.0;
                brightness += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 * alpha;
                coverage += alpha;
                count++;
            }
        // Return averages
        return count == 0 ? (0, 0) : (brightness / count, coverage / count);
    }
}
