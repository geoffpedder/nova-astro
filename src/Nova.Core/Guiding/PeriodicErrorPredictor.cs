namespace Nova.Core.Guiding;

/// <summary>A period found in a series and how much of its variance (0–1, after the trend) a sine at it explains.</summary>
public sealed record PeriodPeak(double Seconds, double Power);

/// <summary>
/// Finds periodic signals in unevenly sampled data (guide frames come at irregular times): for each trial period a
/// least-squares sine fit after removing the linear trend, Lomb-Scargle style. Returns the strongest period and a
/// second one when it carries at least a quarter of the first's power and isn't just its harmonic.
/// </summary>
public static class Periodogram
{
    public static IReadOnlyList<PeriodPeak> Peaks(IReadOnlyList<double> t, IReadOnlyList<double> y, double minPeriod, double maxPeriod, int max = 2)
    {
        var n = t.Count;
        if (n < 20 || maxPeriod <= minPeriod) return [];
        var residual = Detrend(t, y);
        var total = residual.Sum(v => v * v);
        if (total <= 0) return [];

        // Log-spaced trial periods, then each peak refined on a finer grid.
        const int steps = 400;
        var periods = Enumerable.Range(0, steps).Select(i => minPeriod * Math.Pow(maxPeriod / minPeriod, i / (steps - 1.0))).ToArray();
        var power = periods.Select(p => SinePower(t, residual, p) / total).ToArray();

        var peaks = new List<PeriodPeak>();
        for (var i = 1; i < steps - 1; i++)
            if (power[i] > power[i - 1] && power[i] >= power[i + 1])
                peaks.Add(Refine(t, residual, total, periods[i - 1], periods[i + 1]));
        peaks = peaks.OrderByDescending(p => p.Power).ToList();
        if (peaks.Count == 0) return [];

        var result = new List<PeriodPeak> { peaks[0] };
        foreach (var p in peaks.Skip(1))
        {
            if (result.Count >= max || p.Power < 0.25 * peaks[0].Power) break;
            // Not a harmonic (or near) of one already taken: those are part of its shape, fitted anyway.
            var harmonic = result.Any(r => Enumerable.Range(1, 4).Any(h =>
                Math.Abs(p.Seconds - r.Seconds / h) / (r.Seconds / h) < 0.06 || Math.Abs(p.Seconds - r.Seconds * h) / (r.Seconds * h) < 0.06));
            if (!harmonic) result.Add(p);
        }
        return result;
    }

    private static PeriodPeak Refine(IReadOnlyList<double> t, double[] residual, double total, double from, double to)
    {
        var best = new PeriodPeak(from, 0);
        for (var i = 0; i <= 40; i++)
        {
            var p = from + (to - from) * i / 40;
            var power = SinePower(t, residual, p) / total;
            if (power > best.Power) best = new PeriodPeak(p, power);
        }
        return best;
    }

    /// <summary>The variance a sine (any phase) at this period explains: a 2×2 least-squares fit.</summary>
    private static double SinePower(IReadOnlyList<double> t, double[] y, double period)
    {
        var w = 2 * Math.PI / period;
        double cc = 0, ss = 0, cs = 0, yc = 0, ys = 0;
        for (var i = 0; i < y.Length; i++)
        {
            var c = Math.Cos(w * t[i]);
            var s = Math.Sin(w * t[i]);
            cc += c * c; ss += s * s; cs += c * s; yc += y[i] * c; ys += y[i] * s;
        }
        var det = cc * ss - cs * cs;
        if (Math.Abs(det) < 1e-12) return 0;
        var a = (yc * ss - ys * cs) / det;
        var b = (ys * cc - yc * cs) / det;
        return a * yc + b * ys;
    }

    /// <summary>The series with its least-squares straight line taken out.</summary>
    public static double[] Detrend(IReadOnlyList<double> t, IReadOnlyList<double> y)
    {
        var n = t.Count;
        var mt = t.Average();
        var my = y.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (t[i] - mt) * (y[i] - my);
            sxx += (t[i] - mt) * (t[i] - mt);
        }
        var slope = sxx > 0 ? sxy / sxx : 0;
        return Enumerable.Range(0, n).Select(i => y[i] - my - slope * (t[i] - mt)).ToArray();
    }
}

public enum PecState { Learning, Predicting }

/// <summary>What the predictor knows: learning or predicting, the period(s), how well the model fits and its size.</summary>
/// <param name="Quality">The share of the mount's (detrended) RA motion the model explains, 0–1.</param>
/// <param name="AmplitudePixels">Half the model's peak-to-peak (pixels).</param>
/// <param name="MinutesNeeded">How much guiding it needs before predicting (two cycles of the longest period), when known.</param>
public sealed record PecStatus(
    PecState State, IReadOnlyList<double> Periods, double Quality, double AmplitudePixels, double Minutes, double? MinutesNeeded);

/// <summary>
/// Predictive periodic error correction. While guiding it rebuilds the mount's own RA motion (each frame's RA error plus
/// every correction sent so far, so the corrections don't hide it), finds its period(s), fits a fundamental and two
/// harmonics for each, and predicts how far the mount will wander before the next frame, so that can be corrected
/// in advance instead of a frame late. Not thread-safe.
/// </summary>
public sealed class PeriodicErrorPredictor
{
    public const double MinPeriod = 20, MaxPeriod = 1500, WindowSeconds = 3600, GapSeconds = 60, MinQuality = 0.3;
    private const int Harmonics = 3;

    private readonly List<(double T, double Gear, bool Steady)> _samples = [];
    private double _applied;             // Σ corrections sent (and dither offsets), pixels along RA
    private double? _lastT;
    private int _sinceSearch;
    private IReadOnlyList<double> _periods = [];
    private double[]? _coefficients;     // [constant, slope, (cos, sin) × harmonics × periods]
    private double _t0;
    private double _quality;

    /// <summary>A fixed period (seconds) instead of searching for it; null searches.</summary>
    public double? PeriodOverride { get; set; }

    /// <summary>It started learning again by itself (a long gap): why, for the guide log.</summary>
    public event Action<string>? Restarted;

    public bool Trusted => _coefficients is not null && _periods.Count > 0 && _quality >= MinQuality &&
                           Span >= 2 * _periods.Max();

    private double Span => _samples.Count < 2 ? 0 : _samples[^1].T - _samples[0].T;

    public void Reset()
    {
        _samples.Clear();
        (_applied, _lastT, _sinceSearch, _periods, _coefficients, _quality) = (0, null, 0, [], null, 0);
    }

    /// <summary>A correction sent (pixels along RA, + undoes a + error), or a dither's shift of the lock along RA.</summary>
    public void Applied(double pixels) => _applied += pixels;

    /// <summary>One guide frame: its time (s) and RA error (pixels). Settling frames keep the sum going but aren't fitted.</summary>
    public void Add(double t, double raErrorPixels, bool steady = true)
    {
        if (_lastT is { } last && t - last > GapSeconds)
        {
            // A long gap (star lost, paused): the gear's phase can't be trusted to carry on.
            Reset();
            Restarted?.Invoke($"PEC: no guide frames for {t - last:0} s, learning again");
        }
        _lastT = t;
        _samples.Add((t, raErrorPixels + _applied, steady));
        while (_samples.Count > 0 && _samples[0].T < t - WindowSeconds) _samples.RemoveAt(0);

        var steadySamples = _samples.Where(s => s.Steady).ToList();
        if (steadySamples.Count < 30) return;
        var times = steadySamples.Select(s => s.T).ToList();
        var gear = steadySamples.Select(s => s.Gear).ToList();

        // Periods: the override, or a search every 20 frames over what the data can show (two cycles at least).
        if (PeriodOverride is { } fixedPeriod) _periods = [fixedPeriod];
        else if (_periods.Count == 0 || ++_sinceSearch >= 20)
        {
            _sinceSearch = 0;
            var interval = MedianInterval(times);
            var max = Math.Min(MaxPeriod, Span / 2);
            var min = Math.Max(MinPeriod, 4 * interval);
            _periods = max > min ? Periodogram.Peaks(times, gear, min, max).Select(p => p.Seconds).ToList() : [];
        }
        if (_periods.Count == 0)
        {
            _coefficients = null;
            return;
        }
        Fit(times, gear);
    }

    /// <summary>
    /// How far (pixels along RA) the mount will move the star from <paramref name="t"/> to <paramref name="t"/> +
    /// <paramref name="dt"/>, or null while it's still learning.
    /// </summary>
    public double? Predict(double t, double dt)
    {
        if (!Trusted || _coefficients is not { } c) return null;
        return Model(t + dt) - Model(t) + c[1] * dt;
    }

    /// <summary>The time between frames lately (seconds), for how far ahead to predict.</summary>
    public double? Interval => _samples.Count < 3 ? null : MedianInterval(_samples.TakeLast(11).Select(s => s.T).ToList());

    public PecStatus Status
    {
        get
        {
            var amplitude = 0.0;
            if (_coefficients is not null && _periods.Count > 0)
            {
                var longest = _periods.Max();
                var values = Enumerable.Range(0, 200).Select(i => Model(_t0 + longest * i / 200)).ToList();
                amplitude = (values.Max() - values.Min()) / 2;
            }
            double? needed = _periods.Count > 0 ? 2 * _periods.Max() / 60 : null;
            return new PecStatus(Trusted ? PecState.Predicting : PecState.Learning, _periods.Select(p => Math.Round(p, 1)).ToList(),
                Math.Round(_quality, 3), Math.Round(amplitude, 3), Math.Round(Span / 60, 1), needed is { } m ? Math.Round(m, 1) : null);
        }
    }

    /// <summary>The periodic part of the model (no constant, no trend) at a time.</summary>
    private double Model(double t)
    {
        if (_coefficients is not { } c) return 0;
        double sum = 0;
        var k = 2;
        foreach (var p in _periods)
            for (var h = 1; h <= Harmonics; h++)
            {
                var w = 2 * Math.PI * h / p;
                sum += c[k] * Math.Cos(w * (t - _t0)) + c[k + 1] * Math.Sin(w * (t - _t0));
                k += 2;
            }
        return sum;
    }

    /// <summary>Least squares: constant + slope + a cosine and sine for each harmonic of each period.</summary>
    private void Fit(IReadOnlyList<double> t, IReadOnlyList<double> y)
    {
        _t0 = t[0];
        var m = 2 + 2 * Harmonics * _periods.Count;
        var ata = new double[m, m];
        var aty = new double[m];
        var row = new double[m];
        for (var i = 0; i < t.Count; i++)
        {
            Row(t[i], row);
            for (var a = 0; a < m; a++)
            {
                aty[a] += row[a] * y[i];
                for (var b = 0; b < m; b++) ata[a, b] += row[a] * row[b];
            }
        }
        for (var a = 0; a < m; a++) ata[a, a] += 1e-9 * (1 + ata[a, a]); // keep it solvable
        var c = Solve(ata, aty);
        if (c is null)
        {
            _coefficients = null;
            return;
        }

        // Quality: how much of the detrended motion the periodic part explains.
        var detrended = Periodogram.Detrend(t, y);
        double total = 0, residual = 0;
        for (var i = 0; i < t.Count; i++)
        {
            Row(t[i], row);
            var fit = 0.0;
            for (var a = 0; a < m; a++) fit += row[a] * c[a];
            residual += (y[i] - fit) * (y[i] - fit);
            total += detrended[i] * detrended[i];
        }
        _coefficients = c;
        _quality = total > 0 ? Math.Max(0, 1 - residual / total) : 0;

        void Row(double time, double[] r)
        {
            r[0] = 1;
            r[1] = time - _t0;
            var k = 2;
            foreach (var p in _periods)
                for (var h = 1; h <= Harmonics; h++)
                {
                    var w = 2 * Math.PI * h / p;
                    r[k] = Math.Cos(w * (time - _t0));
                    r[k + 1] = Math.Sin(w * (time - _t0));
                    k += 2;
                }
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) return null;
            if (pivot != col)
            {
                for (var k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (var k = col; k < n; k++) m[r, k] -= f * m[col, k];
                x[r] -= f * x[col];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            var sum = x[r];
            for (var k = r + 1; k < n; k++) sum -= m[r, k] * x[k];
            x[r] = sum / m[r, r];
        }
        return x;
    }

    private static double MedianInterval(IReadOnlyList<double> t)
    {
        if (t.Count < 2) return 1;
        var d = Enumerable.Range(1, t.Count - 1).Select(i => t[i] - t[i - 1]).Order().ToArray();
        return d[d.Length / 2];
    }
}
