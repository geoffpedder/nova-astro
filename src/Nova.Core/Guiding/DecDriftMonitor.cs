namespace Nova.Core.Guiding;

/// <summary>A steady drift in Dec that guiding keeps having to correct, and the polar error it implies.</summary>
/// <param name="ArcsecPerMinute">How fast the star drifts in Dec.</param>
/// <param name="Direction">Which way guiding keeps pushing: "North" or "South".</param>
/// <param name="OneWay">The share of Dec corrections that went that way (0–1).</param>
/// <param name="Minutes">How much guiding the estimate is based on.</param>
/// <param name="PolarErrorArcmin">
/// The smallest polar error that drifts this fast. Drift depends on where the scope points, so the real error can be
/// larger, never smaller.
/// </param>
/// <param name="Significant">Steady and fast enough to be worth fixing the polar alignment for.</param>
public sealed record DecDrift(
    double ArcsecPerMinute, string Direction, double OneWay, double Minutes, double PolarErrorArcmin, bool Significant);

/// <summary>
/// Watches PHD2's Dec corrections for a steady one-way drift, the signature of poor polar alignment. While guiding
/// holds the star still, the corrections add up to how far it would have drifted. A polar error of ε drifts a star in
/// Dec by up to ε × the sidereal rate: 0.26″ a minute for each arcminute of error.
/// </summary>
public sealed class DecDriftMonitor
{
    /// <summary>Dec drift (″/min) for each arcminute of polar error, at most: 15.04″/s × 60 s × (1′ in radians).</summary>
    public const double ArcsecPerMinutePerArcmin = 15.041 * 60 * Math.PI / 180 / 60;

    private const double WindowSeconds = 15 * 60;
    private const double MinimumSeconds = 5 * 60;
    private const double LongestStep = 10; // a gap longer than this (star lost, paused) isn't counted as guiding
    private readonly Queue<(double Time, double Seconds, double Pixels, int Sign)> _steps = new();
    private double? _last;

    /// <summary>Starts again (guiding started, on a new target perhaps, where the drift is different).</summary>
    public void Reset()
    {
        _steps.Clear();
        _last = null;
    }

    /// <summary>
    /// One guide step: when it happened (seconds), the Dec correction PHD2 decided on (pixels, either sign) and which
    /// way it pulsed ("North", "South", or null if it didn't). Steps that don't count (settling, a lost star) only
    /// mark the time.
    /// </summary>
    public void Add(double time, double decCorrectionPixels, string? direction, bool counts = true)
    {
        var seconds = _last is { } last ? time - last : 0;
        _last = time;
        if (!counts || seconds <= 0 || seconds > LongestStep) return;
        var sign = direction switch
        {
            "North" or "N" => 1,
            "South" or "S" => -1,
            _ => 0,
        };
        var pixels = sign == 0 ? 0 : Math.Abs(decCorrectionPixels) * sign;
        _steps.Enqueue((time, seconds, pixels, sign));
        while (_steps.Count > 0 && _steps.Peek().Time < time - WindowSeconds) _steps.Dequeue();
    }

    /// <summary>The drift so far, or null with too little guiding to tell.</summary>
    public DecDrift? Estimate(double arcsecPerPixel)
    {
        var seconds = _steps.Sum(s => s.Seconds);
        if (seconds < MinimumSeconds) return null;
        var net = _steps.Sum(s => s.Pixels) * arcsecPerPixel;
        var rate = Math.Abs(net) / seconds * 60;
        var north = _steps.Count(s => s.Sign > 0);
        var south = _steps.Count(s => s.Sign < 0);
        var oneWay = north + south == 0 ? 0 : (double)Math.Max(north, south) / (north + south);
        var polar = rate / ArcsecPerMinutePerArcmin;
        // Steady (corrections nearly all one way) and more than about 15′ of polar error's worth.
        var significant = oneWay >= 0.8 && polar >= 15;
        return new DecDrift(Math.Round(rate, 1), net >= 0 ? "North" : "South", Math.Round(oneWay, 2),
            Math.Round(seconds / 60, 1), Math.Round(polar, 0), significant);
    }

    /// <summary>A polar error for people: "2.1°" or "35′".</summary>
    public static string Describe(double arcmin) => arcmin >= 60 ? $"{arcmin / 60:0.0}°" : $"{arcmin:0}′";
}
