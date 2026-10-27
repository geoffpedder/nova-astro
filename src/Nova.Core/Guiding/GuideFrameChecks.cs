namespace Nova.Core.Guiding;

/// <summary>
/// Automatic guide exposure: steps through the usual exposures (within a minimum and maximum) to keep the guide star's
/// SNR near a target. Longer when the star is faint or lost, shorter when it's well above the target; it changes at
/// most every few frames, and only once the SNR is well past the target either way, so it doesn't hunt.
/// </summary>
public sealed class GuideExposureController
{
    /// <summary>The exposures it steps through (seconds).</summary>
    public static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 15];

    /// <summary>Frames to judge (and to wait after a change) before changing again.</summary>
    public const int Frames = 3;

    private readonly Queue<double> _snr = new();
    private int _sinceChange;

    public double Min { get; private set; } = 1;
    public double Max { get; private set; } = 4;
    public double TargetSnr { get; private set; } = 20;

    /// <summary>The exposure for the next frame (seconds).</summary>
    public double Exposure { get; private set; } = 2;

    /// <summary>Sets the range and target, starting from <paramref name="start"/> (kept within the range).</summary>
    public void Configure(double min, double max, double targetSnr, double start)
    {
        (Min, Max, TargetSnr) = (min, max, targetSnr);
        Exposure = Math.Clamp(start, min, max);
        _snr.Clear();
        _sinceChange = 0;
    }

    /// <summary>
    /// Feeds one frame: the lock star's SNR, or null when there was no star. Returns the new exposure when it changed.
    /// </summary>
    public double? Add(double? snr)
    {
        _sinceChange++;
        if (snr is null)
        {
            _snr.Clear();
            // No star: longer, a frame at a time once the last change has had its frames.
            return _sinceChange >= Frames ? Change(+1) : null;
        }
        _snr.Enqueue(snr.Value);
        while (_snr.Count > Frames) _snr.Dequeue();
        if (_snr.Count < Frames || _sinceChange < Frames) return null;
        var average = _snr.Average();
        if (average < TargetSnr * 0.7) return Change(+1);
        if (average > TargetSnr * 2) return Change(-1);
        return null;
    }

    private double? Change(int direction)
    {
        var next = direction > 0
            ? Steps.Where(s => s > Exposure + 1e-6).DefaultIfEmpty(Max).Min()
            : Steps.Where(s => s < Exposure - 1e-6).DefaultIfEmpty(Min).Max();
        next = Math.Clamp(next, Min, Max);
        if (Math.Abs(next - Exposure) < 1e-6) return null;
        Exposure = next;
        _snr.Clear();
        _sinceChange = 0;
        return next;
    }
}

/// <summary>
/// PHD2's star mass check: a guide frame whose lock star suddenly has much more or less light than lately (a passing
/// cloud, a satellite or a hot pixel next to it, another star wandering into the box) gives a false position, so it
/// isn't corrected. After a few such frames in a row the new brightness is taken as the star's own (thin cloud
/// arriving, say).
/// </summary>
public sealed class StarMassMonitor
{
    /// <summary>Frames the brightness is judged against.</summary>
    public const int Window = 10;

    /// <summary>Changed frames in a row after which the new brightness is accepted.</summary>
    public const int AcceptAfter = 5;

    private readonly List<double> _masses = [];
    private readonly List<double> _changed = [];

    /// <summary>How far (fraction) the mass may differ from lately's before a frame is skipped; 0 turns the check off.</summary>
    public double Tolerance { get; set; } = 0.5;

    /// <summary>
    /// Feeds one frame's lock-star mass; false when it changed too much to trust the frame (and how much, as a fraction).
    /// </summary>
    public bool Add(double mass, out double change)
    {
        change = 0;
        if (Tolerance <= 0 || mass <= 0) return true;
        if (_masses.Count >= 3)
        {
            var sorted = _masses.Order().ToList();
            var typical = sorted[sorted.Count / 2];
            change = mass / typical - 1;
            if (Math.Abs(change) > Tolerance)
            {
                _changed.Add(mass);
                if (_changed.Count < AcceptAfter) return false;
                _masses.Clear();
                _masses.AddRange(_changed); // it's stayed changed: that's the star now
                _changed.Clear();
                return true;
            }
        }
        _changed.Clear();
        _masses.Add(mass);
        if (_masses.Count > Window) _masses.RemoveAt(0);
        return true;
    }

    public void Reset()
    {
        _masses.Clear();
        _changed.Clear();
    }
}
