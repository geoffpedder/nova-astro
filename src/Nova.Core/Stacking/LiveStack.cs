using Nova.Core.Imaging;

namespace Nova.Core.Stacking;

/// <summary>A frame made ready to stack: one plane per channel (R, G, B, or one for mono) at the stack's scale.</summary>
public sealed record StackFrame(float[][] Planes, int Width, int Height, bool Color);

/// <summary>
/// A running stack: frames aligned to the first one and averaged, pixel by pixel. Kept as a running mean and spread
/// (Welford's method), so it never holds the frames themselves; once a pixel has a few frames, a value far outside its
/// spread (a satellite or plane trail, a cosmic ray) is left out. Each frame's background is first matched to the
/// first frame's, so a sky that brightens or darkens through the night doesn't leave patches.
/// </summary>
public sealed class LiveStack
{
    private readonly float[][] _mean;
    private readonly float[][] _m2;
    private readonly ushort[] _count;
    private float[]? _referenceBackground;

    public LiveStack(int width, int height, int channels, double rejectSigma = 3, int rejectAfter = 5)
    {
        Width = width;
        Height = height;
        Channels = channels;
        RejectSigma = rejectSigma;
        RejectAfter = rejectAfter;
        _mean = Enumerable.Range(0, channels).Select(_ => new float[width * height]).ToArray();
        _m2 = Enumerable.Range(0, channels).Select(_ => new float[width * height]).ToArray();
        _count = new ushort[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public double RejectSigma { get; }
    public int RejectAfter { get; }

    /// <summary>Frames added.</summary>
    public int Frames { get; private set; }

    /// <summary>
    /// Adds a frame. <paramref name="frameToReference"/> takes the frame's pixels to the stack's (identity for the
    /// first frame). Pixels the frame doesn't cover (its edges after a shift) are left as they are.
    /// </summary>
    public void Add(StackFrame frame, Similarity frameToReference)
    {
        if (frame.Planes.Length != Channels) throw new ArgumentException($"A {Channels}-channel stack can't take a {frame.Planes.Length}-channel frame");
        var backgrounds = frame.Planes.Select(Median).ToArray();
        _referenceBackground ??= backgrounds;
        // Pull each stack pixel from where it falls in the frame.
        var toFrame = frameToReference.Inverse();
        var noise = frame.Planes.Select(p => Math.Max(Noise(p), 1e-3f)).ToArray();
        var reject = Frames >= RejectAfter;

        Parallel.For(0, Height, y =>
        {
            Span<float> values = stackalloc float[Channels];
            for (var x = 0; x < Width; x++)
            {
                var (fx, fy) = toFrame.Apply(x, y);
                if (fx < 0 || fy < 0 || fx > frame.Width - 1.001 || fy > frame.Height - 1.001) continue;
                var x0 = (int)fx;
                var y0 = (int)fy;
                var (dx, dy) = ((float)(fx - x0), (float)(fy - y0));
                var at = y * Width + x;
                var n = _count[at];
                if (n == ushort.MaxValue) continue;

                // All channels or none: a rejected value in one channel rejects the pixel, keeping colours true.
                var keep = true;
                for (var c = 0; c < Channels; c++)
                {
                    var plane = frame.Planes[c];
                    var i = y0 * frame.Width + x0;
                    var v = plane[i] * (1 - dx) * (1 - dy) + plane[i + 1] * dx * (1 - dy)
                            + plane[i + frame.Width] * (1 - dx) * dy + plane[i + frame.Width + 1] * dx * dy;
                    v += _referenceBackground[c] - backgrounds[c];
                    values[c] = v;
                    if (reject && n >= 2)
                    {
                        var spread = MathF.Sqrt(_m2[c][at] / (n - 1));
                        if (MathF.Abs(v - _mean[c][at]) > RejectSigma * MathF.Max(spread, noise[c])) keep = false;
                    }
                }
                if (!keep) continue;
                n++;
                _count[at] = n;
                for (var c = 0; c < Channels; c++)
                {
                    var delta = values[c] - _mean[c][at];
                    _mean[c][at] += delta / n;
                    _m2[c][at] += delta * (values[c] - _mean[c][at]);
                }
            }
        });
        Frames++;
    }

    /// <summary>The stack so far, one plane per channel; pixels no frame covered get the background.</summary>
    public float[][] Result()
    {
        var output = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var plane = (float[])_mean[c].Clone();
            var fill = _referenceBackground?[c] ?? 0;
            for (var i = 0; i < plane.Length; i++)
                if (_count[i] == 0) plane[i] = fill;
            output[c] = plane;
        }
        return output;
    }

    /// <summary>
    /// A raw frame made ready to stack, at half size: colour frames debayered into R, G and B super-pixels, mono
    /// frames binned 2×2. Isolated hot pixels (much brighter than all eight neighbours, which are only sky) are
    /// replaced by their neighbours' median; stars, whose neighbours are bright too, are left alone.
    /// </summary>
    public static StackFrame Prepare(RawImage image)
    {
        float[][] planes;
        int w, h;
        var pattern = Debayer.FromHeaders(image);
        var color = pattern is not null;
        if (pattern is { } bayer)
        {
            var rgb = Debayer.SuperPixel(image, bayer);
            (planes, w, h) = ([rgb.Red, rgb.Green, rgb.Blue], rgb.Width, rgb.Height);
        }
        else
        {
            (w, h) = (image.Width / 2, image.Height / 2);
            var binned = new float[w * h];
            var src = image.Pixels;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = 2 * y * image.Width + 2 * x;
                binned[y * w + x] = (src[i] + src[i + 1] + src[i + image.Width] + src[i + image.Width + 1]) * 0.25f;
            }
            planes = [binned];
        }
        foreach (var plane in planes) RemoveHotPixels(plane, w, h);
        return new StackFrame(planes, w, h, color);
    }

    /// <summary>Star positions (in the raw frame's pixels) in the stack's half-size pixels.</summary>
    public static StarPoint ToStackPixels(double x, double y, double flux) => new((x - 0.5) / 2, (y - 0.5) / 2, flux);

    public static void RemoveHotPixels(float[] plane, int width, int height)
    {
        var background = Median(plane);
        var noise = Math.Max(Noise(plane), 1e-3f);
        Span<float> around = stackalloc float[8];
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var at = y * width + x;
            var v = plane[at];
            if (v < background + 10 * noise) continue;
            var k = 0;
            var brightest = float.MinValue;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var n = plane[at + dy * width + dx];
                around[k++] = n;
                if (n > brightest) brightest = n;
            }
            if (brightest > background + 3 * noise) continue; // a star: its neighbours are lit too
            around.Sort();
            plane[at] = (around[3] + around[4]) * 0.5f;
        }
    }

    /// <summary>Median of a plane, from a sample (fast and plenty for a background level).</summary>
    public static float Median(float[] plane)
    {
        var sample = Sample(plane);
        Array.Sort(sample);
        return sample[sample.Length / 2];
    }

    /// <summary>Noise of a plane: 1.4826 × the median absolute deviation, from a sample.</summary>
    public static float Noise(float[] plane)
    {
        var sample = Sample(plane);
        Array.Sort(sample);
        var median = sample[sample.Length / 2];
        for (var i = 0; i < sample.Length; i++) sample[i] = MathF.Abs(sample[i] - median);
        Array.Sort(sample);
        return 1.4826f * sample[sample.Length / 2];
    }

    private static float[] Sample(float[] plane)
    {
        const int size = 20_000;
        if (plane.Length <= size) return (float[])plane.Clone();
        var step = plane.Length / size;
        var sample = new float[size];
        for (var i = 0; i < size; i++) sample[i] = plane[i * step];
        return sample;
    }
}
