namespace Nova.Core.Guiding;

/// <summary>
/// The recent guide steps (pixels, kept for the graph and the RMS), with which were settling (after guiding started or
/// a dither) and which came first after a dither. A dither moves the star on purpose, so the steps until guiding has
/// settled again aren't guiding error: they're left out of the RMS and peaks, and the graph marks them instead.
/// Not thread-safe: callers lock.
/// </summary>
public sealed class GuideHistory(int capacity)
{
    private readonly List<GuidePoint> _points = [];
    private bool _ditherPending;

    public int Count => _points.Count;

    /// <summary>One guide step's error (pixels), and whether guiding was settling then.</summary>
    public void Add(double ra, double dec, bool settling)
    {
        _points.Add(new GuidePoint(ra, dec, settling, _ditherPending));
        _ditherPending = false;
        if (_points.Count > capacity) _points.RemoveAt(0);
    }

    /// <summary>A dither was just sent: the next step is marked as the first after it.</summary>
    public void MarkDither() => _ditherPending = true;

    public void Clear()
    {
        _points.Clear();
        _ditherPending = false;
    }

    /// <summary>RMS and peaks over the steady steps (not settling); arcseconds when the scale is known, else pixels.</summary>
    public GuideStats Stats(double? arcsecPerPixel)
    {
        var scale = arcsecPerPixel ?? 1;
        var units = arcsecPerPixel is null ? "px" : "arcsec";
        var steady = _points.Where(p => !p.Settling).ToList();
        if (steady.Count == 0) return new GuideStats(0, 0, 0, 0, 0, 0, units);
        var ra = Math.Sqrt(steady.Average(p => p.Ra * p.Ra)) * scale;
        var dec = Math.Sqrt(steady.Average(p => p.Dec * p.Dec)) * scale;
        return new GuideStats(ra, dec, Math.Sqrt(ra * ra + dec * dec),
            steady.Max(p => Math.Abs(p.Ra)) * scale, steady.Max(p => Math.Abs(p.Dec)) * scale, steady.Count, units);
    }

    /// <summary>The steps for the graph, in arcseconds when the scale is known.</summary>
    public IReadOnlyList<GuidePoint> Points(double? arcsecPerPixel)
    {
        var scale = arcsecPerPixel ?? 1;
        return _points.Select(p => p with { Ra = p.Ra * scale, Dec = p.Dec * scale }).ToList();
    }
}
