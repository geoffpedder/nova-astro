using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Nova.Core.Imaging;

public sealed record ImageStatistics(double Min, double Max, double Mean, double Median, double Mad);

public sealed record PreviewImage(byte[] Png, int Width, int Height, int Binning, ImageStatistics Statistics);

/// <summary>
/// Turns raw sensor data into something a browser can show: downsample, auto-stretch, encode PNG.
/// Raw FITS never goes to the browser; it is far too large to send for every frame.
/// </summary>
public static class Preview
{
    private const int HistogramBins = 65536;

    /// <param name="stretch">Auto-stretch (true) or show the data linearly over the sensor's full range (false).</param>
    public static PreviewImage Render(RawImage image, int maxSize = 1600, bool stretch = true)
    {
        var stats = ComputeStatistics(image.Pixels);
        var binning = BinningFor(image.Width, image.Height, maxSize);
        var (small, w, h) = Downsample(image.Pixels, image.Width, image.Height, binning);
        var gray = stretch ? AutoStretch(small, stats) : Linear(small, FullScale(image, stats));
        return new PreviewImage(Png.EncodeGray8(w, h, gray), w, h, binning, stats);
    }

    /// <summary>
    /// Debayers a one-shot-colour frame and stretches each channel on its own ("unlinked"),
    /// which also neutralises the background colour cast from light pollution and the extra green pixels.
    /// <see cref="PreviewImage.Binning"/> is relative to the raw sensor, so it is at least 2.
    /// </summary>
    public static PreviewImage RenderColor(RawImage image, BayerPattern pattern, int maxSize = 1600, bool stretch = true)
    {
        var stats = ComputeStatistics(image.Pixels);
        var planes = Debayer.SuperPixel(image, pattern);
        // Unstretched colour uses one shared range, so the sensor's real colour balance shows.
        var range = stretch ? default : FullScale(image, stats);

        // Full resolution asked for: superpixels would only give half, so interpolate the missing colours instead,
        // stretched just as the half-size preview is (each channel's statistics come from its superpixel plane).
        if (BinningFor(image.Width, image.Height, maxSize) == 1)
        {
            Func<float, byte> MapFor(float[] plane) => stretch ? StretchMap(ComputeStatistics(plane)) : LinearMap(range);
            var maps = new[] { MapFor(planes.Red), MapFor(planes.Green), MapFor(planes.Blue) };
            var full = Debayer.Bilinear(image, pattern, maps);
            return new PreviewImage(Png.EncodeRgb8(image.Width, image.Height, full), image.Width, image.Height, 1, stats);
        }

        var binning = BinningFor(planes.Width, planes.Height, maxSize);

        (byte[], int, int) StretchPlane(float[] plane)
        {
            var (small, w, h) = Downsample(plane, planes.Width, planes.Height, binning);
            return (stretch ? AutoStretch(small, ComputeStatistics(small)) : Linear(small, range), w, h);
        }

        byte[]? r = null, g = null, b = null;
        int w = 0, h = 0;
        Parallel.Invoke(
            () => (r, w, h) = StretchPlane(planes.Red),
            () => (g, _, _) = StretchPlane(planes.Green),
            () => (b, _, _) = StretchPlane(planes.Blue));

        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = r![i];
            rgb[i * 3 + 1] = g![i];
            rgb[i * 3 + 2] = b![i];
        }
        return new PreviewImage(Png.EncodeRgb8(w, h, rgb), w, h, binning * 2, stats);
    }

    /// <summary>
    /// The range that maps to black..white without stretching: 0 to the sensor's full scale for integer
    /// data (65535 for 16-bit), so a half-full flat looks mid-grey. Float data uses its own min..max.
    /// </summary>
    public static (double Low, double High) FullScale(RawImage image, ImageStatistics stats) => image.BitsPerPixel switch
    {
        8 => (0, 255),
        16 => (0, 65535),
        32 => (0, Math.Max(stats.Max, 1)),
        _ => (stats.Min, stats.Max),
    };

    /// <summary>Linear mapping of <paramref name="range"/> to 0..255, clipped.</summary>
    public static byte[] Linear(ReadOnlySpan<float> pixels, (double Low, double High) range)
    {
        var output = new byte[pixels.Length];
        var span = range.High - range.Low;
        if (span <= 0) return output;
        var scale = 255 / span;
        for (var i = 0; i < pixels.Length; i++)
            output[i] = (byte)Math.Clamp((pixels[i] - range.Low) * scale + 0.5, 0, 255);
        return output;
    }

    private static int BinningFor(int width, int height, int maxSize) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)maxSize));

    /// <summary>Min/max/mean exactly; median and MAD from a 65536-bin histogram (accurate to 1 ADU for 16-bit data).</summary>
    public static ImageStatistics ComputeStatistics(ReadOnlySpan<float> pixels)
    {
        if (pixels.IsEmpty) return new ImageStatistics(0, 0, 0, 0, 0);

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var p in pixels)
        {
            if (p < min) min = p;
            if (p > max) max = p;
            sum += p;
        }
        var range = max - min;
        if (range <= 0) return new ImageStatistics(min, max, min, min, 0);

        var scale = (HistogramBins - 1) / range;
        var histogram = new int[HistogramBins];
        foreach (var p in pixels) histogram[(int)((p - min) * scale)]++;

        var medianBin = FindMedianBin(histogram, pixels.Length);

        // Absolute deviation histogram, built from the first one without touching pixels again.
        var deviations = new int[HistogramBins];
        for (var i = 0; i < HistogramBins; i++)
            if (histogram[i] != 0) deviations[Math.Abs(i - medianBin)] += histogram[i];
        var madBins = FindMedianBin(deviations, pixels.Length);

        return new ImageStatistics(min, max, sum / pixels.Length, min + medianBin / scale, madBins / scale);
    }

    /// <summary>
    /// Screen-transfer-function style auto stretch (the same idea as PixInsight's STF and NINA's
    /// auto-stretch): clip shadows a few MADs below the median, then a midtones transfer that puts
    /// the background at <paramref name="targetBackground"/>.
    /// </summary>
    public static byte[] AutoStretch(ReadOnlySpan<float> pixels, ImageStatistics stats,
        double targetBackground = 0.25, double shadowClipping = -2.8)
    {
        var output = new byte[pixels.Length];
        if (stats.Max - stats.Min <= 0) return output;
        var map = StretchMap(stats, targetBackground, shadowClipping);
        for (var i = 0; i < pixels.Length; i++) output[i] = map(pixels[i]);
        return output;
    }

    /// <summary>The auto stretch for data with these statistics, as a function of one pixel value.</summary>
    public static Func<float, byte> StretchMap(ImageStatistics stats, double targetBackground = 0.25, double shadowClipping = -2.8)
    {
        var range = stats.Max - stats.Min;
        if (range <= 0) return _ => 0;

        var median = (stats.Median - stats.Min) / range;
        var madn = 1.4826 * stats.Mad / range;
        var c0 = Math.Clamp(median + shadowClipping * madn, 0, 1);
        var midtones = Mtf(targetBackground, median - c0);

        // A lookup table keeps the per-pixel cost to one multiply and one index.
        const int lutSize = 65536;
        var lut = new byte[lutSize];
        for (var i = 0; i < lutSize; i++)
        {
            var x = i / (double)(lutSize - 1);
            var clipped = c0 >= 1 ? 0 : Math.Clamp((x - c0) / (1 - c0), 0, 1);
            lut[i] = (byte)Math.Round(Mtf(midtones, clipped) * 255);
        }

        var scale = (lutSize - 1) / range;
        var min = stats.Min;
        return value => lut[Math.Clamp((int)((value - min) * scale), 0, lutSize - 1)];
    }

    /// <summary><see cref="Linear"/> as a function of one pixel value.</summary>
    public static Func<float, byte> LinearMap((double Low, double High) range)
    {
        var span = range.High - range.Low;
        if (span <= 0) return _ => 0;
        var scale = 255 / span;
        var low = range.Low;
        return value => (byte)Math.Clamp((value - low) * scale + 0.5, 0, 255);
    }

    /// <summary>Midtones transfer function: maps <paramref name="m"/> to 0.5, keeps 0 and 1 fixed.</summary>
    public static double Mtf(double m, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (m <= 0) return 1;
        if (m >= 1) return 0;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    private static (float[] Pixels, int Width, int Height) Downsample(float[] pixels, int width, int height, int factor)
    {
        if (factor == 1) return (pixels, width, height);

        var w = width / factor;
        var h = height / factor;
        var output = new float[w * h];
        var norm = 1f / (factor * factor);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var dy = 0; dy < factor; dy++)
            {
                var row = (y * factor + dy) * width + x * factor;
                for (var dx = 0; dx < factor; dx++) sum += pixels[row + dx];
            }
            output[y * w + x] = sum * norm;
        }
        return (output, w, h);
    }

    private static int FindMedianBin(int[] histogram, int count)
    {
        var half = (count + 1) / 2;
        var running = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= half) return i;
        }
        return histogram.Length - 1;
    }
}

/// <summary>Dependency-free 8-bit grayscale and RGB PNG encoder.</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeGray8(int width, int height, ReadOnlySpan<byte> pixels) =>
        Encode(width, height, pixels, colorType: 0, bytesPerPixel: 1);

    /// <summary>Pixels are interleaved R, G, B.</summary>
    public static byte[] EncodeRgb8(int width, int height, ReadOnlySpan<byte> pixels) =>
        Encode(width, height, pixels, colorType: 2, bytesPerPixel: 3);

    private static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels, byte colorType, int bytesPerPixel)
    {
        if (pixels.Length != width * height * bytesPerPixel) throw new ArgumentException("Pixel count does not match dimensions");

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = colorType;  // 0 = grayscale, 2 = RGB
        WriteChunk(output, "IHDR", ihdr);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // filter: none
                    zlib.Write(pixels.Slice(y * width * bytesPerPixel, width * bytesPerPixel));
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(0xFFFFFFFF, typeBytes);
        crc = Crc32(crc, data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
