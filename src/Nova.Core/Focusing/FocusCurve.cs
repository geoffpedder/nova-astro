namespace Nova.Core.Focusing;

/// <summary>One autofocus measurement: the median star HFR (pixels) at a focuser position.</summary>
public sealed record FocusPoint(int Position, double Hfr, int Stars);

/// <summary>
/// The fitted focus curve HFR(x) = √(A + B·(x − C)²): a hyperbola, the shape star size really follows
/// either side of focus. C is the best focus position, √A the HFR there.
/// </summary>
public sealed record FocusFit(double A, double B, double C, double RSquared)
{
    public int BestPosition => (int)Math.Round(C);
    public double BestHfr => Math.Sqrt(Math.Max(A, 0));
    public double HfrAt(double position) => Math.Sqrt(Math.Max(A + B * (position - C) * (position - C), 0));
}

public static class FocusCurve
{
    /// <summary>
    /// Fits the hyperbola. Squaring it gives HFR² = A + B(x − C)², a parabola in x, so an ordinary
    /// least-squares parabola through the squared HFRs does the job — no iteration, no starting guess.
    /// Seeing makes HFR errors roughly proportional to HFR, so squaring blows up the far, blurry points'
    /// errors; weighting by 1/HFR² keeps them from dragging the minimum around.
    /// Null when the points don't form a V (too few, or curving the wrong way).
    /// </summary>
    public static FocusFit? Fit(IReadOnlyList<FocusPoint> points)
    {
        if (points.Count < 3) return null;

        // Centre and scale positions so the normal equations stay well conditioned with 5-digit positions.
        var mean = points.Average(p => p.Position);
        var scale = Math.Max(1, Math.Sqrt(points.Average(p => (p.Position - mean) * (p.Position - mean))));
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var p in points)
        {
            var u = (p.Position - mean) / scale;
            var y = p.Hfr * p.Hfr;
            var w = 1 / Math.Max(y, 1e-6);
            s0 += w; s1 += w * u; s2 += w * u * u; s3 += w * u * u * u; s4 += w * u * u * u * u;
            t0 += w * y; t1 += w * u * y; t2 += w * u * u * y;
        }
        // Solve [s0 s1 s2; s1 s2 s3; s2 s3 s4]·[p0 p1 p2] = [t0 t1 t2] by Cramer's rule.
        var det = Det(s0, s1, s2, s1, s2, s3, s2, s3, s4);
        if (Math.Abs(det) < 1e-12) return null;
        var p0 = Det(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
        var p1 = Det(s0, t0, s2, s1, t1, s3, s2, t2, s4) / det;
        var p2 = Det(s0, s1, t0, s1, s2, t1, s2, s3, t2) / det;
        if (p2 <= 0) return null; // ∩-shaped: no minimum

        var uBest = -p1 / (2 * p2);
        var fit = new FocusFit(
            A: p0 - p1 * p1 / (4 * p2),
            B: p2 / (scale * scale),
            C: mean + uBest * scale,
            RSquared: 0);

        var hfrMean = points.Average(p => p.Hfr);
        var total = points.Sum(p => (p.Hfr - hfrMean) * (p.Hfr - hfrMean));
        var residual = points.Sum(p => (p.Hfr - fit.HfrAt(p.Position)) * (p.Hfr - fit.HfrAt(p.Position)));
        return fit with { RSquared = total > 0 ? Math.Round(1 - residual / total, 4) : 0 };
    }

    private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
}
