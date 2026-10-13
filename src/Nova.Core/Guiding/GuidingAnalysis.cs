namespace Nova.Core.Guiding;

/// <summary>
/// What the guiding assistant measured with corrections off. Distances in <see cref="Units"/> ("arcsec" when the
/// guide scale is known, else "px"); the recommended min moves are always pixels (as the settings are).
/// </summary>
/// <param name="Axes">"RA/Dec" when calibrated (the star's motion split along the mount's axes), else "X/Y" (image).</param>
/// <param name="JitterRa">Frame-to-frame star motion (RMS, per axis): the seeing, plus wind and mount vibration.</param>
/// <param name="DriftRa">Steady drift per minute (+/− along the axis); Dec drift is mostly polar alignment.</param>
/// <param name="PeriodicRa">RA's slower wander left after the drift (peak to peak): periodic error and the like.</param>
/// <param name="PolarErrorArcmin">The least polar error that explains the Dec drift (needs the scale).</param>
/// <param name="PePeriods">The periodic error's period(s) in RA (seconds), when the run was long enough to see them:
/// a starting point for Predictive PEC's period.</param>
public sealed record GuidingAssistantResult(
    DateTime Utc, double Seconds, int Frames, string Axes, string Units,
    double JitterRa, double JitterDec, double DriftRa, double DriftDec, double PeriodicRa, double? PolarErrorArcmin,
    double RecommendedRaMinMove, double RecommendedDecMinMove, double? RecommendedExposure, string Summary,
    IReadOnlyList<double>? PePeriods = null);

/// <summary>
/// PHD2's Guiding Assistant, in short: follow the star for a few minutes with corrections off and split its motion
/// into the fast part (seeing: no guider can follow it, so it sets the floor and the min move) and the slow part
/// (drift and periodic error: what guiding is for).
/// </summary>
public sealed class GuidingAssistant
{
    private readonly List<(double T, double Ra, double Dec)> _samples = [];

    public int Count => _samples.Count;
    public double Seconds => _samples.Count < 2 ? 0 : _samples[^1].T - _samples[0].T;

    /// <summary>One frame: seconds since the start and the star's offset (pixels, along RA and Dec or image X and Y).</summary>
    public void Add(double seconds, double ra, double dec) => _samples.Add((seconds, ra, dec));

    /// <param name="arcsecPerPixel">The guide scale, for arcseconds and the polar error; null keeps pixels.</param>
    /// <param name="calibrated">The offsets are along RA and Dec (else image axes).</param>
    public GuidingAssistantResult? Result(DateTime utc, double? arcsecPerPixel, bool calibrated, double exposureSeconds)
    {
        if (_samples.Count < 10) return null;
        var scale = arcsecPerPixel ?? 1;
        var units = arcsecPerPixel is null ? "px" : "arcsec";

        double Jitter(Func<(double T, double Ra, double Dec), double> v)
        {
            double sum = 0;
            for (var i = 1; i < _samples.Count; i++)
            {
                var d = v(_samples[i]) - v(_samples[i - 1]);
                sum += d * d;
            }
            // Successive differences of independent noise have twice its variance.
            return Math.Sqrt(sum / (_samples.Count - 1) / 2);
        }

        (double Slope, double[] Residual) Trend(Func<(double T, double Ra, double Dec), double> v)
        {
            var n = _samples.Count;
            var mt = _samples.Average(s => s.T);
            var mv = _samples.Average(v);
            double sxy = 0, sxx = 0;
            foreach (var s in _samples)
            {
                sxy += (s.T - mt) * (v(s) - mv);
                sxx += (s.T - mt) * (s.T - mt);
            }
            var slope = sxx > 0 ? sxy / sxx : 0;
            return (slope, _samples.Select(s => v(s) - (mv + slope * (s.T - mt))).ToArray());
        }

        var jitterRa = Jitter(s => s.Ra);
        var jitterDec = Jitter(s => s.Dec);
        var (slopeRa, residualRa) = Trend(s => s.Ra);
        var (slopeDec, _) = Trend(s => s.Dec);

        // Periodic error: the RA residual smoothed over ~5 frames (so seeing mostly averages out), peak to peak.
        var window = Math.Min(5, residualRa.Length);
        var smooth = Enumerable.Range(0, residualRa.Length - window + 1).Select(i => residualRa.Skip(i).Take(window).Average()).ToArray();
        var periodic = smooth.Length > 0 ? smooth.Max() - smooth.Min() : 0;

        double? polar = arcsecPerPixel is { } s2 && calibrated
            ? Math.Abs(slopeDec * 60 * s2) / DecDriftMonitor.ArcsecPerMinutePerArcmin
            : null;

        // Min move just above the seeing jitter, so the guider doesn't chase it; PHD2 uses about 0.65 of it.
        static double MinMove(double jitterPx) => Math.Round(Math.Clamp(0.65 * jitterPx, 0.05, 2), 2);
        var jitterArcsec = arcsecPerPixel is { } s3 ? Math.Max(jitterRa, jitterDec) * s3 : (double?)null;
        // Poor seeing: longer exposures average it out before correcting. Otherwise no change is suggested.
        double? exposure = jitterArcsec switch
        {
            > 1.5 when exposureSeconds < 4 => 4,
            > 1.0 when exposureSeconds < 3 => 3,
            _ => null,
        };

        // The periodic error's period(s): what Predictive PEC would learn (needs two cycles in the run).
        IReadOnlyList<double> pePeriods = [];
        if (calibrated && Seconds >= 60)
        {
            var times = _samples.Select(s => s.T).ToList();
            var interval = Seconds / Math.Max(1, _samples.Count - 1);
            pePeriods = Periodogram.Peaks(times, _samples.Select(s => s.Ra).ToList(), Math.Max(PeriodicErrorPredictor.MinPeriod, 4 * interval), Seconds / 2)
                .Where(p => p.Power >= 0.2).Select(p => Math.Round(p.Seconds)).ToList();
        }

        var u = units == "arcsec" ? "″" : " px";
        var axes = calibrated ? ("RA", "Dec") : ("X", "Y");
        var summary = $"Seeing jitter {jitterRa * scale:0.00}{u} {axes.Item1}, {jitterDec * scale:0.00}{u} {axes.Item2} (RMS); " +
                      $"drift {slopeRa * 60 * scale:+0.00;-0.00}{u}/min {axes.Item1}, {slopeDec * 60 * scale:+0.00;-0.00}{u}/min {axes.Item2}" +
                      (calibrated ? $"; RA wander {periodic * scale:0.0}{u} peak to peak" : "") +
                      (pePeriods.Count > 0 ? $"; RA period {string.Join(" + ", pePeriods.Select(x => $"{x:0} s"))}" : "") +
                      (polar is { } p ? $"; polar alignment at least {DecDriftMonitor.Describe(p)} off" : "");

        return new GuidingAssistantResult(utc, Math.Round(Seconds, 1), _samples.Count, calibrated ? "RA/Dec" : "X/Y", units,
            Math.Round(jitterRa * scale, 3), Math.Round(jitterDec * scale, 3),
            Math.Round(slopeRa * 60 * scale, 3), Math.Round(slopeDec * 60 * scale, 3), Math.Round(periodic * scale, 3),
            polar is { } pe ? Math.Round(pe, 1) : null,
            MinMove(jitterRa), MinMove(jitterDec), exposure, summary, pePeriods);
    }
}

/// <summary>One axis's guiding, judged from its recent errors.</summary>
/// <param name="Lag1">How much each error resembles the one before (−1…1): near 0 random, negative see-sawing, positive slow wander.</param>
/// <param name="Verdict">"seeing-limited", "fine" (within the min move), "chasing", "lagging" or "random".</param>
public sealed record AxisDiagnosis(string Axis, double Rms, double Lag1, string Verdict, string Text);

/// <param name="Summary">One line for under the graph.</param>
/// <param name="BlurArcsec">How much guiding widens the stars (FWHM, ″): 2.355 × total RMS.</param>
public sealed record GuideDiagnosis(string Summary, IReadOnlyList<AxisDiagnosis> Axes, double? BlurArcsec);

/// <summary>
/// Tells seeing-limited guiding from guiding that settings could improve, from the recent steady steps: errors the size
/// of the seeing jitter (measured by the assistant) can't be guided out; errors that flip sign frame to frame mean the
/// corrections overshoot or chase the seeing; errors that linger over several frames mean they're too timid.
/// </summary>
public static class GuidingDiagnosis
{
    public const int MinimumSteps = 30;

    /// <param name="points">Recent guide steps, in <paramref name="units"/>; settling ones are skipped.</param>
    /// <param name="seeingRa">The seeing jitter per axis (same units) from the guiding assistant, if it has run.</param>
    /// <param name="minMoveRa">Each axis's min move (same units), when known: errors within it are left alone on
    /// purpose, so how they wander there says nothing about the settings.</param>
    public static GuideDiagnosis? Diagnose(IReadOnlyList<GuidePoint> points, string units, double? seeingRa, double? seeingDec,
        double? minMoveRa = null, double? minMoveDec = null)
    {
        var steady = points.Where(p => !p.Settling).TakeLast(100).ToList();
        if (steady.Count < MinimumSteps) return null;
        var u = units == "arcsec" ? "″" : " px";

        AxisDiagnosis Axis(string name, IReadOnlyList<double> v, double? seeing, double? minMove, bool dec)
        {
            var rms = Math.Sqrt(v.Average(x => x * x));
            var lag1 = Lag1(v);
            if (seeing is { } s && s > 0 && rms <= 1.25 * s)
                return new(name, rms, lag1, "seeing-limited", $"{name} {rms:0.00}{u} ≈ seeing {s:0.00}{u}: seeing-limited");
            if (minMove is { } mm && rms <= mm)
                return new(name, rms, lag1, "fine", $"{name} {rms:0.00}{u}, within the min move");
            if (lag1 < -0.3)
                return new(name, rms, lag1, "chasing",
                    $"{name} see-saws: corrections overshoot or chase the seeing; try lower aggressiveness or a higher min move");
            if (lag1 > 0.5)
                return dec
                    ? new(name, rms, lag1, "lagging", $"{name} drifts between corrections; a lower Dec min move holds it closer")
                    : new(name, rms, lag1, "lagging", $"{name} lags behind its errors; try higher aggressiveness");
            var context = seeing is { } s2 && s2 > 0 ? $" ({rms / s2:0.0}× the seeing)" : "";
            return new(name, rms, lag1, "random", $"{name} {rms:0.00}{u}{context}, errors random (seeing-like)");
        }

        var ra = Axis("RA", steady.Select(p => p.Ra).ToList(), seeingRa, minMoveRa, dec: false);
        var dec = Axis("Dec", steady.Select(p => p.Dec).ToList(), seeingDec, minMoveDec, dec: true);
        var total = Math.Sqrt(ra.Rms * ra.Rms + dec.Rms * dec.Rms);
        var blur = units == "arcsec" ? Math.Round(2.355 * total, 2) : (double?)null;

        string summary;
        bool Done(AxisDiagnosis a) => a.Verdict is "seeing-limited" or "fine";
        if (ra.Verdict == "seeing-limited" && dec.Verdict == "seeing-limited")
            summary = "Seeing-limited: the guiding is as good as the sky allows";
        else if (Done(ra) && Done(dec))
            summary = "Holding the star within the min move (or the seeing): nothing to gain from the settings";
        else if (ra.Verdict is "chasing" or "lagging")
            summary = ra.Text;
        else if (dec.Verdict is "chasing" or "lagging")
            summary = dec.Text;
        else if (seeingRa is null)
            summary = "Errors look random (seeing-like); run the guiding assistant to measure the seeing";
        else
            summary = $"{ra.Text}; {dec.Text}";
        return new GuideDiagnosis(summary, [ra, dec], blur);
    }

    /// <summary>Lag-1 autocorrelation (about the mean): −1 alternating, 0 random, +1 slowly changing.</summary>
    public static double Lag1(IReadOnlyList<double> v)
    {
        if (v.Count < 3) return 0;
        var mean = v.Average();
        double num = 0, den = 0;
        for (var i = 0; i < v.Count; i++)
        {
            den += (v[i] - mean) * (v[i] - mean);
            if (i > 0) num += (v[i] - mean) * (v[i - 1] - mean);
        }
        return den > 0 ? num / den : 0;
    }
}
