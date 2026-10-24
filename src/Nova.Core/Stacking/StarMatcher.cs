namespace Nova.Core.Stacking;

/// <summary>A star's position and brightness, in the pixels of the image being aligned.</summary>
public readonly record struct StarPoint(double X, double Y, double Flux);

/// <summary>
/// A similarity transform (shift, rotation and scale) from one image's pixels to another's:
/// x' = A·x − B·y + Tx, y' = B·x + A·y + Ty.
/// </summary>
public readonly record struct Similarity(double A, double B, double Tx, double Ty)
{
    public static readonly Similarity Identity = new(1, 0, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + Tx, B * x + A * y + Ty);

    public double Scale => Math.Sqrt(A * A + B * B);

    /// <summary>Rotation in degrees.</summary>
    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;

    public Similarity Inverse()
    {
        var d = A * A + B * B;
        var (a, b) = (A / d, -B / d);
        return new Similarity(a, b, -(a * Tx - b * Ty), -(b * Tx + a * Ty));
    }
}

/// <param name="Transform">From the frame's pixels to the reference's, or null when no match was found.</param>
/// <param name="Matched">How many of the frame's stars landed on a reference star.</param>
public sealed record StarMatch(Similarity? Transform, int Matched, double RmsPixels);

/// <summary>
/// Finds how a frame is shifted and turned relative to a reference, from their stars alone (no plate solve): the
/// usual triangle method. Triangles made from the brightest stars are compared by their shape (two ratios of their
/// sides, unchanged by shifting, turning or scaling); each pair of triangles alike in shape and size votes for its
/// three pairs of stars; the best-supported star pairs give a first fit, outliers are dropped, and the fit is then
/// checked and refined against all the stars that line up.
/// </summary>
public static class StarMatcher
{
    /// <summary>Stars used to build triangles (the brightest).</summary>
    private const int TriangleStars = 20;

    /// <summary>Stars used to check and refine the fit.</summary>
    private const int CheckStars = 80;

    /// <summary>How alike two triangles' shapes must be (difference in each side ratio).</summary>
    private const double ShapeTolerance = 0.008;

    private readonly record struct Triangle(int A, int B, int C, double Ratio1, double Ratio2, double Longest);

    public static StarMatch Match(IReadOnlyList<StarPoint> reference, IReadOnlyList<StarPoint> frame, double tolerancePixels = 2.5)
    {
        var refStars = Brightest(reference, CheckStars);
        var frameStars = Brightest(frame, CheckStars);
        if (refStars.Count < 3 || frameStars.Count < 3) return new StarMatch(null, 0, 0);

        var refTriangles = Triangles(refStars.Take(TriangleStars).ToList());
        var frameTriangles = Triangles(frameStars.Take(TriangleStars).ToList());
        if (refTriangles.Count == 0 || frameTriangles.Count == 0) return new StarMatch(null, 0, 0);

        // Votes: frame star i ↔ reference star j.
        var nf = Math.Min(TriangleStars, frameStars.Count);
        var nr = Math.Min(TriangleStars, refStars.Count);
        var votes = new int[nf, nr];
        var sorted = refTriangles.OrderBy(t => t.Ratio1).ToArray();
        var keys = sorted.Select(t => t.Ratio1).ToArray();
        foreach (var f in frameTriangles)
        {
            var start = Array.BinarySearch(keys, f.Ratio1 - ShapeTolerance);
            if (start < 0) start = ~start;
            for (var k = start; k < sorted.Length && sorted[k].Ratio1 <= f.Ratio1 + ShapeTolerance; k++)
            {
                var r = sorted[k];
                if (Math.Abs(r.Ratio2 - f.Ratio2) > ShapeTolerance) continue;
                var size = f.Longest / r.Longest;
                if (size is < 0.8 or > 1.25) continue; // same telescope and camera: about the same size
                votes[f.A, r.A]++;
                votes[f.B, r.B]++;
                votes[f.C, r.C]++;
            }
        }

        // Each frame star's best-voted reference star, kept when that's mutual and well supported.
        var pairs = new List<(StarPoint Frame, StarPoint Ref, int Votes)>();
        for (var i = 0; i < nf; i++)
        {
            var (best, bestVotes) = (-1, 0);
            for (var j = 0; j < nr; j++)
                if (votes[i, j] > bestVotes) (best, bestVotes) = (j, votes[i, j]);
            if (best < 0 || bestVotes < 2) continue;
            var mutual = true;
            for (var i2 = 0; i2 < nf && mutual; i2++)
                if (i2 != i && votes[i2, best] > bestVotes) mutual = false;
            if (mutual) pairs.Add((frameStars[i], refStars[best], bestVotes));
        }
        if (pairs.Count < 3) return new StarMatch(null, 0, 0);

        // First fit from the voted pairs, dropping the worst until they agree.
        var fit = Fit(pairs.Select(p => (p.Frame, p.Ref)).ToList());
        for (var round = 0; round < 5 && pairs.Count > 3; round++)
        {
            var residuals = pairs.Select(p => Residual(fit, p.Frame, p.Ref)).ToArray();
            var worst = residuals.Max();
            if (worst <= tolerancePixels) break;
            pairs.RemoveAt(Array.IndexOf(residuals, worst));
            fit = Fit(pairs.Select(p => (p.Frame, p.Ref)).ToList());
        }

        // Check against every star: which frame stars land on a reference star? Refine on those.
        for (var round = 0; round < 2; round++)
        {
            var lined = LineUp(fit, frameStars, refStars, tolerancePixels);
            if (lined.Count < 3) return new StarMatch(null, lined.Count, 0);
            fit = Fit(lined);
        }
        var final = LineUp(fit, frameStars, refStars, tolerancePixels);
        if (final.Count < 3) return new StarMatch(null, final.Count, 0);
        var rms = Math.Sqrt(final.Average(p => Math.Pow(Residual(fit, p.Frame, p.Ref), 2)));
        return new StarMatch(fit, final.Count, rms);
    }

    private static List<StarPoint> Brightest(IReadOnlyList<StarPoint> stars, int count) =>
        stars.OrderByDescending(s => s.Flux).Take(count).ToList();

    /// <summary>
    /// Every triangle of the stars, its corners labelled by the side they face (A faces the longest side, C the
    /// shortest), so matching triangles pair up their corners. Too small or too thin ones are left out.
    /// </summary>
    private static List<Triangle> Triangles(IReadOnlyList<StarPoint> stars)
    {
        var list = new List<Triangle>();
        for (var i = 0; i < stars.Count; i++)
        for (var j = i + 1; j < stars.Count; j++)
        for (var k = j + 1; k < stars.Count; k++)
        {
            // Side opposite each corner.
            var sides = new[] { (Corner: i, Length: Distance(stars[j], stars[k])), (Corner: j, Length: Distance(stars[i], stars[k])), (Corner: k, Length: Distance(stars[i], stars[j])) };
            Array.Sort(sides, (x, y) => y.Length.CompareTo(x.Length));
            var longest = sides[0].Length;
            if (longest < 15) continue; // too small: centroid errors swamp its shape
            var r1 = sides[1].Length / longest;
            var r2 = sides[2].Length / longest;
            if (r2 < 0.1) continue; // too thin: its corners are ambiguous
            list.Add(new Triangle(sides[0].Corner, sides[1].Corner, sides[2].Corner, r1, r2, longest));
        }
        return list;
    }

    private static List<(StarPoint Frame, StarPoint Ref)> LineUp(Similarity fit, List<StarPoint> frame, List<StarPoint> reference, double tolerance)
    {
        var used = new HashSet<int>();
        var pairs = new List<(StarPoint, StarPoint)>();
        foreach (var f in frame)
        {
            var (x, y) = fit.Apply(f.X, f.Y);
            var (best, bestDistance) = (-1, tolerance);
            for (var j = 0; j < reference.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(reference[j].X - x, 2) + Math.Pow(reference[j].Y - y, 2));
                if (d < bestDistance && !used.Contains(j)) (best, bestDistance) = (j, d);
            }
            if (best < 0) continue;
            used.Add(best);
            pairs.Add((f, reference[best]));
        }
        return pairs;
    }

    /// <summary>The least-squares similarity taking the frame points onto the reference points.</summary>
    public static Similarity Fit(IReadOnlyList<(StarPoint Frame, StarPoint Ref)> pairs)
    {
        var n = pairs.Count;
        double fx = 0, fy = 0, rx = 0, ry = 0;
        foreach (var (f, r) in pairs) { fx += f.X; fy += f.Y; rx += r.X; ry += r.Y; }
        (fx, fy, rx, ry) = (fx / n, fy / n, rx / n, ry / n);
        double sxx = 0, sab = 0, sba = 0;
        foreach (var (f, r) in pairs)
        {
            var (ax, ay, bx, by) = (f.X - fx, f.Y - fy, r.X - rx, r.Y - ry);
            sxx += ax * ax + ay * ay;
            sab += ax * bx + ay * by;
            sba += ax * by - ay * bx;
        }
        if (sxx <= 0) return Similarity.Identity with { Tx = rx - fx, Ty = ry - fy };
        var (a, b) = (sab / sxx, sba / sxx);
        return new Similarity(a, b, rx - (a * fx - b * fy), ry - (b * fx + a * fy));
    }

    private static double Residual(Similarity fit, StarPoint frame, StarPoint reference)
    {
        var (x, y) = fit.Apply(frame.X, frame.Y);
        return Math.Sqrt(Math.Pow(reference.X - x, 2) + Math.Pow(reference.Y - y, 2));
    }

    private static double Distance(StarPoint a, StarPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
