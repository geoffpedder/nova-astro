using Nova.Core.Imaging;

namespace Nova.Core.Guiding;

/// <summary>One guide frame's measurement: how far the stars are from home (pixels, image axes).</summary>
/// <param name="X">Where the lock star is now (or would be, from the reference stars, when it wasn't found).</param>
/// <param name="Found">How many of the tracked stars were found.</param>
/// <param name="Snr">The lock star's SNR (0 when it wasn't found).</param>
public sealed record GuideMeasurement(double X, double Y, double Dx, double Dy, int Found, int Tracked, double Snr, double Hfr)
{
    public double Distance => Math.Sqrt(Dx * Dx + Dy * Dy);
}

/// <summary>
/// Follows a lock star and up to a few reference stars from frame to frame. The error is their SNR-weighted average
/// offset from home, which averages out seeing better than one star (PHD2's multi-star guiding). Lost when the lock
/// star and most references are missing; it keeps looking around where they should be and recovers when they're back.
/// </summary>
public sealed class GuideStarTracker
{
    private sealed class Tracked(DetectedStar star)
    {
        public double HomeX = star.X, HomeY = star.Y;      // where it should be
        public double LastX = star.X, LastY = star.Y;      // where it was last seen
        public double Snr = star.Snr;
        public bool Found = true;
    }

    private readonly List<Tracked> _stars = [];

    public int MaxReferenceStars { get; set; } = 8;
    /// <summary>How far (pixels) a star may have moved since the last frame and still be recognised.</summary>
    public double SearchRadius { get; set; } = 15;
    /// <summary>Stars closer than this to the image edge (pixels) aren't picked.</summary>
    public int EdgeMargin { get; set; } = 20;

    public bool HasLock => _stars.Count > 0;
    public (double X, double Y)? LockHome => HasLock ? (_stars[0].HomeX, _stars[0].HomeY) : null;
    public int Count => _stars.Count;

    /// <summary>The tracked stars where they were last seen (or expected), the lock star first, and whether the last frame found them.</summary>
    public IReadOnlyList<(double X, double Y, bool Found)> Stars => _stars.Select(s => (s.LastX, s.LastY, s.Found)).ToList();

    /// <summary>
    /// Picks the guide stars: the lock star is the brightest by SNR that isn't crowded or near the edge (saturated
    /// and hot pixels were already left out by the detector), references the next best. False when there's none.
    /// </summary>
    public bool Select(IReadOnlyList<DetectedStar> stars, int width, int height)
    {
        _stars.Clear();
        var minGap = 2.5 * SearchRadius / 1.5;
        var usable = stars
            .Where(s => s.X >= EdgeMargin && s.Y >= EdgeMargin && s.X < width - EdgeMargin && s.Y < height - EdgeMargin)
            .Where(s => !stars.Any(o => !ReferenceEquals(o, s) && Dist(o.X, o.Y, s.X, s.Y) < minGap && o.Flux > s.Flux / 4))
            .OrderByDescending(s => s.Snr)
            .Take(1 + MaxReferenceStars);
        foreach (var star in usable) _stars.Add(new Tracked(star));
        return HasLock;
    }

    /// <summary>Picks the lock star nearest a point (a click on the guide view), keeping the best others as references.</summary>
    public bool SelectAt(IReadOnlyList<DetectedStar> stars, int width, int height, double x, double y)
    {
        var nearest = stars.Where(s => Dist(s.X, s.Y, x, y) <= 2 * SearchRadius).MinBy(s => Dist(s.X, s.Y, x, y));
        if (nearest is null) return false;
        Select(stars.Where(s => !ReferenceEquals(s, nearest)).ToList(), width, height);
        _stars.Insert(0, new Tracked(nearest));
        if (_stars.Count > 1 + MaxReferenceStars) _stars.RemoveAt(_stars.Count - 1);
        return true;
    }

    public void Clear() => _stars.Clear();

    /// <summary>Makes where the stars are now their home (starting to guide, or after calibration moved them).</summary>
    public void SetHomeHere()
    {
        foreach (var s in _stars) (s.HomeX, s.HomeY) = (s.LastX, s.LastY);
    }

    /// <summary>Moves home by an offset (pixels): a dither. Guiding then walks the stars there.</summary>
    public void ShiftHome(double dx, double dy)
    {
        foreach (var s in _stars) { s.HomeX += dx; s.HomeY += dy; }
    }

    /// <summary>Finds the tracked stars in a new frame. Null when nothing is tracked or too few stars were found (lost).</summary>
    public GuideMeasurement? Measure(IReadOnlyList<DetectedStar> stars)
    {
        if (!HasLock) return null;

        var taken = new HashSet<DetectedStar>(ReferenceEqualityComparer.Instance);
        var matches = new List<(Tracked T, DetectedStar S)>();
        foreach (var t in _stars)
        {
            var match = stars
                .Where(s => !taken.Contains(s) && Dist(s.X, s.Y, t.LastX, t.LastY) <= SearchRadius)
                .MinBy(s => Dist(s.X, s.Y, t.LastX, t.LastY));
            if (match is null) continue;
            taken.Add(match);
            matches.Add((t, match));
        }

        // Moves that disagree with the rest (a star swapped for a neighbour, a passing satellite) are dropped.
        if (matches.Count >= 3)
        {
            var mx = Median(matches.Select(m => m.S.X - m.T.LastX));
            var my = Median(matches.Select(m => m.S.Y - m.T.LastY));
            matches = matches.Where(m => Dist(m.S.X - m.T.LastX, m.S.Y - m.T.LastY, mx, my) <= 3).ToList();
        }

        var lockFound = matches.Any(m => m.T == _stars[0]);
        if (matches.Count == 0 || (!lockFound && matches.Count * 2 < _stars.Count))
        {
            foreach (var t in _stars) t.Found = false;
            return null;
        }

        double sumW = 0, dx = 0, dy = 0;
        foreach (var (t, s) in matches)
        {
            var w = Math.Max(1, s.Snr);
            dx += w * (s.X - t.HomeX);
            dy += w * (s.Y - t.HomeY);
            sumW += w;
        }
        dx /= sumW;
        dy /= sumW;

        // Stars not found this frame are expected where the others say; the next frame looks there.
        var found = matches.ToDictionary(m => m.T, m => m.S);
        foreach (var t in _stars)
        {
            t.Found = found.TryGetValue(t, out var s);
            if (s is not null) { (t.LastX, t.LastY, t.Snr) = (s.X, s.Y, s.Snr); }
            else { (t.LastX, t.LastY) = (t.HomeX + dx, t.HomeY + dy); }
        }

        var lockStar = found.GetValueOrDefault(_stars[0]);
        return new GuideMeasurement(_stars[0].LastX, _stars[0].LastY, dx, dy, matches.Count, _stars.Count,
            lockStar?.Snr ?? 0, lockStar?.Hfr ?? 0);
    }

    private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
