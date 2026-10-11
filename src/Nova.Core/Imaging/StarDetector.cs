namespace Nova.Core.Imaging;

/// <param name="X">Centre in image pixels (0 = left edge of the first pixel's centre).</param>
/// <param name="Hfr">Half-flux radius in image pixels: the radius holding half the star's light.</param>
/// <param name="Snr">Total flux over the noise in the measuring circle.</param>
public sealed record DetectedStar(double X, double Y, double Hfr, double Flux, double Peak, double Snr);

/// <param name="MedianHfr">Median HFR in image pixels, or null when no stars were measured.</param>
/// <param name="HfrSpread">Robust spread (MAD) of the HFRs.</param>
public sealed record StarAnalysis(
    IReadOnlyList<DetectedStar> Stars, double? MedianHfr, double? HfrSpread, double Background, double Noise)
{
    /// <summary>FWHM estimate: for a Gaussian star FWHM = 2 × HFR.</summary>
    public double? MedianFwhm => MedianHfr * 2;
}

public sealed record StarDetectionOptions(
    double DetectionSigma = 5, double MinSnr = 10, int MaxStars = 500, int EdgeMargin = 10);

/// <summary>
/// Finds stars and measures their HFR (half-flux radius) — the standard focus measure (NINA, PHD2, Ekos).
/// Colour (Bayer) frames are first combined into 2×2 superpixels so the colour pattern isn't seen as structure.
/// Detection: robust background and noise, a light 3×3 smoothing, local maxima well above the noise.
/// Measurement: local background from a ring, flux-weighted centre, HFR = Σ(flux·r)/Σ(flux) inside a circle
/// sized to the star. Hot pixels, saturated stars, edge stars and faint blobs are left out.
/// </summary>
public static class StarDetector
{
    public static StarAnalysis Analyze(RawImage image, StarDetectionOptions? options = null)
    {
        var o = options ?? new StarDetectionOptions();

        // Bayer frames: average each 2×2 cell into one luminance pixel; results are scaled back up.
        var (lum, w, h, factor) = Debayer.FromHeaders(image) is not null
            ? Superpixel(image)
            : (image.Pixels, image.Width, image.Height, 1);

        var stats = Preview.ComputeStatistics(lum);
        var noise = Math.Max(1.4826 * stats.Mad, 1e-6);
        var background = stats.Median;
        var saturation = image.BitsPerPixel switch
        {
            8 => 250.0,
            16 => 64_000.0,
            _ => stats.Max * 0.995,
        };

        // Smoothing by 3×3 averages nine pixels, so its noise is a third of the raw noise.
        var smooth = BoxBlur3(lum, w, h);
        var threshold = background + o.DetectionSigma * noise / 3;

        var candidates = new List<(int X, int Y, float Value)>();
        var margin = Math.Max(2, o.EdgeMargin / factor);
        for (var y = margin; y < h - margin; y++)
        {
            var row = y * w;
            for (var x = margin; x < w - margin; x++)
            {
                var v = smooth[row + x];
                if (v <= threshold) continue;
                if (v < smooth[row + x - 1] || v < smooth[row + x + 1] ||
                    v < smooth[row - w + x - 1] || v < smooth[row - w + x] || v < smooth[row - w + x + 1] ||
                    v < smooth[row + w + x - 1] || v < smooth[row + w + x] || v < smooth[row + w + x + 1])
                    continue;
                candidates.Add((x, y, v));
            }
        }

        // Brightest first; drop candidates sitting on a brighter one (plateaus, double peaks in one star).
        candidates.Sort((a, b) => b.Value.CompareTo(a.Value));
        var accepted = new List<(int X, int Y)>();
        const int separation = 4;
        foreach (var c in candidates)
        {
            if (accepted.Count >= o.MaxStars * 4) break;
            var near = false;
            foreach (var a in accepted)
            {
                if (Math.Abs(a.X - c.X) <= separation && Math.Abs(a.Y - c.Y) <= separation) { near = true; break; }
            }
            if (!near) accepted.Add((c.X, c.Y));
        }

        var stars = new List<DetectedStar>();
        foreach (var (x, y) in accepted)
        {
            if (Measure(lum, w, h, x, y, noise, saturation, o) is { } star)
                stars.Add(star with
                {
                    // Back to full-resolution pixels for colour frames.
                    X = (star.X + 0.5) * factor - 0.5,
                    Y = (star.Y + 0.5) * factor - 0.5,
                    Hfr = star.Hfr * factor,
                });
        }

        stars = stars.OrderByDescending(s => s.Flux).Take(o.MaxStars).ToList();
        if (stars.Count == 0) return new StarAnalysis(stars, null, null, background, noise);

        var hfrs = stars.Select(s => s.Hfr).Order().ToList();
        var median = Median(hfrs);
        var spread = Median(hfrs.Select(v => Math.Abs(v - median)).Order().ToList());
        return new StarAnalysis(stars, median, spread, background, noise);
    }

    private static DetectedStar? Measure(float[] img, int w, int h, int px, int py, double noise, double saturation,
        StarDetectionOptions o)
    {
        double cx = px, cy = py;
        var radius = 8.0;
        double hfr = 0, flux = 0, peak = 0, bg = 0;
        var pixels = 0;

        // Measure twice: the first pass sizes the circle and refines the centre for the second.
        for (var pass = 0; pass < 2; pass++)
        {
            var r = (int)Math.Ceiling(radius);
            var ringOuter = r + 6;
            if (cx - ringOuter < 0 || cy - ringOuter < 0 || cx + ringOuter >= w || cy + ringOuter >= h) return null;

            bg = RingMedian(img, w, cx, cy, r + 2, ringOuter);

            double sum = 0, sumX = 0, sumY = 0, sumR = 0, sumPositive = 0;
            peak = 0;
            pixels = 0;
            var bright = 0; // pixels clearly above background: hot pixels have just one
            for (var y = (int)(cy - r); y <= (int)(cy + r); y++)
            for (var x = (int)(cx - r); x <= (int)(cx + r); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d > radius) continue;
                var raw = img[y * w + x];
                if (raw > peak) peak = raw;
                var v = raw - bg;
                pixels++;
                if (v > 3 * noise) bright++;
                // HFR uses the signed values: dropping pixels below the background but keeping those above it
                // would count positive noise as light far from the centre and inflate the HFR.
                sum += v;
                sumR += v * d;
                if (v <= 0) continue; // the centre, though, from positive light only (robust for faint stars)
                sumPositive += v;
                sumX += v * x;
                sumY += v * y;
            }

            if (sum <= 0 || sumPositive <= 0 || bright < 3) return null;
            cx = sumX / sumPositive;
            cy = sumY / sumPositive;
            flux = sum;
            hfr = sumR / sum;
            radius = Math.Clamp(Math.Ceiling(3 * hfr) + 2, 4, 40);
        }

        var snr = flux / (noise * Math.Sqrt(pixels));
        if (peak >= saturation || hfr < 0.5 || snr < o.MinSnr) return null;
        return new DetectedStar(cx, cy, hfr, flux, peak, snr);
    }

    private static double RingMedian(float[] img, int w, double cx, double cy, int inner, int outer)
    {
        var values = new List<float>();
        for (var y = (int)(cy - outer); y <= (int)(cy + outer); y++)
        for (var x = (int)(cx - outer); x <= (int)(cx + outer); x++)
        {
            var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            if (d2 >= inner * inner && d2 <= outer * outer) values.Add(img[y * w + x]);
        }
        values.Sort();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static (float[] Pixels, int Width, int Height, int Factor) Superpixel(RawImage image)
    {
        var w = image.Width / 2;
        var h = image.Height / 2;
        var output = new float[w * h];
        var src = image.Pixels;
        for (var y = 0; y < h; y++)
        {
            var r0 = 2 * y * image.Width;
            var r1 = r0 + image.Width;
            for (var x = 0; x < w; x++)
                output[y * w + x] = (src[r0 + 2 * x] + src[r0 + 2 * x + 1] + src[r1 + 2 * x] + src[r1 + 2 * x + 1]) * 0.25f;
        }
        return (output, w, h, 2);
    }

    /// <summary>3×3 mean, done as two 3-wide passes. Edge pixels are copied unchanged.</summary>
    private static float[] BoxBlur3(float[] src, int w, int h)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Array.Copy(src, tmp, src.Length);
        Array.Copy(src, dst, src.Length);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 1; x < w - 1; x++) tmp[row + x] = (src[row + x - 1] + src[row + x] + src[row + x + 1]) / 3f;
        }
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++) dst[row + x] = (tmp[row - w + x] + tmp[row + x] + tmp[row + w + x]) / 3f;
        }
        return dst;
    }
}
