using Nova.Core.Imaging;

namespace Nova.Core.Guiding;

/// <summary>
/// Dark frames for the guide camera: hot pixels and warm clusters glow the same in every frame, so a guider can take
/// them for stars (and a "star" that never moves ruins calibration and guiding). A master dark taken with the scope
/// covered holds just them; subtracting it leaves only the sky.
/// </summary>
public static class DarkFrames
{
    /// <summary>The per-pixel median of several darks: noise and cosmic-ray hits average out, hot pixels stay.</summary>
    public static RawImage Combine(IReadOnlyList<RawImage> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("No dark frames");
        var first = frames[0];
        if (frames.Any(f => f.Width != first.Width || f.Height != first.Height))
            throw new ArgumentException("The dark frames aren't all the same size");

        var result = new float[first.Pixels.Length];
        var values = new float[frames.Count];
        for (var i = 0; i < result.Length; i++)
        {
            for (var f = 0; f < frames.Count; f++) values[f] = frames[f].Pixels[i];
            Array.Sort(values);
            var mid = values.Length / 2;
            result[i] = values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
        }
        return new RawImage(first.Width, first.Height, result, first.BitsPerPixel);
    }

    /// <summary>
    /// Takes a dark's extra signal (each pixel above the dark's median) out of a frame. The median stays, so the frame
    /// keeps its background level. <paramref name="ratio"/> scales the dark when its exposure differs from the
    /// frame's (dark current grows with exposure time).
    /// </summary>
    public static RawImage Subtract(RawImage frame, RawImage dark, double ratio = 1)
    {
        if (frame.Width != dark.Width || frame.Height != dark.Height)
            throw new ArgumentException($"The dark is {dark.Width}×{dark.Height}, the frame {frame.Width}×{frame.Height}");
        var median = (float)Preview.ComputeStatistics(dark.Pixels).Median;
        var r = (float)ratio;
        var pixels = new float[frame.Pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = Math.Max(0, frame.Pixels[i] - (dark.Pixels[i] - median) * r);
        var result = new RawImage(frame.Width, frame.Height, pixels, frame.BitsPerPixel);
        foreach (var (key, value) in frame.Headers) result.Headers[key] = value;
        return result;
    }

    /// <summary>How many pixels of a master dark stand well clear of the rest (hot or warm pixels).</summary>
    public static int HotPixels(RawImage dark, double sigma = 8)
    {
        var stats = Preview.ComputeStatistics(dark.Pixels);
        var threshold = stats.Median + sigma * Math.Max(1.4826 * stats.Mad, 1);
        return dark.Pixels.Count(p => p > threshold);
    }

    /// <summary>
    /// The dark to use for an exposure: the same exposure if there is one, else the nearest (scaled to fit).
    /// Returns its index and the scale, or null when there are none.
    /// </summary>
    public static (int Index, double Ratio)? Choose(IReadOnlyList<double> darkExposures, double exposure)
    {
        if (darkExposures.Count == 0) return null;
        var best = Enumerable.Range(0, darkExposures.Count).MinBy(i => Math.Abs(Math.Log(darkExposures[i] / exposure)));
        var ratio = Math.Abs(darkExposures[best] - exposure) < 0.001 ? 1 : exposure / darkExposures[best];
        return (best, ratio);
    }
}
