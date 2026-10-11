using Nova.Core.Equipment;
using Nova.Core.PlateSolving;

namespace Nova.Core.Astronomy;

/// <summary>A plate-solved frame: its solution (pixels ↔ J2000) and the moment it was taken (mid-exposure).</summary>
public sealed record PoleFrame(Wcs Wcs, int Width, int Height, DateTime Utc);

/// <summary>Where things are in the latest frame, in its pixels (any may lie outside the frame).</summary>
/// <param name="Axis">Where the mount's RA axis points.</param>
/// <param name="Pole">The celestial pole (refracted, if refraction is on).</param>
/// <param name="Zenith">A pixel direction "up" in the sky (towards the zenith) from the axis: for drawing.</param>
public sealed record PoleMarks((double X, double Y)? Axis, (double X, double Y)? Pole, (double X, double Y)? Polaris,
    (double X, double Y)? UpFromAxis);

/// <summary>
/// Polar alignment with the camera on the pole (as SharpCap does it): two solved frames with the RA axis turned
/// between them (ideally about 90°). The camera turns about the mount's axis, so the one pixel that sees the same
/// ground-fixed direction in both frames is where the axis points, and it keeps seeing it: the camera is fixed to
/// the mount. While the altitude and azimuth knobs are turned, each new solve says where that pixel now points,
/// which is the axis, compared with the pole. The axis is found once, from the first two frames.
/// </summary>
public sealed class PoleAlignment
{
    private readonly GeoLocation _site;
    private readonly bool _refraction;
    private Vec3 _axisInCamera;  // the axis as a direction in the camera's own frame (fixed: the camera is on the mount)

    /// <summary>Polaris, J2000 (Hipparcos).</summary>
    public static readonly EquatorialCoordinates Polaris = new(2.530301, 89.264109);

    public PoleAlignment(GeoLocation site, bool refraction = false)
    {
        _site = site;
        _refraction = refraction;
    }

    public PolarError? Error { get; private set; }
    public PoleMarks? Marks { get; private set; }

    /// <summary>How far RA turned between the two frames, as measured (degrees).</summary>
    public double TurnDegrees { get; private set; }

    /// <summary>Finds the axis from two frames with only the RA axis turned between them.</summary>
    public PolarError Measure(PoleFrame first, PoleFrame second)
    {
        var m1 = Orientation(first);
        var m2 = Orientation(second);
        // In camera coordinates, the axis is the direction both orientations send to the same ground direction:
        // the axis of the rotation m2ᵀ·m1.
        var q = Multiply(Transpose(m2), m1);
        var axis = new Vec3(q[2, 1] - q[1, 2], q[0, 2] - q[2, 0], q[1, 0] - q[0, 1]);
        var sine = axis.Length / 2;
        var cosine = (q[0, 0] + q[1, 1] + q[2, 2] - 1) / 2;
        TurnDegrees = Math.Atan2(sine, cosine) * 180 / Math.PI;
        if (TurnDegrees < 5)
            throw new ArgumentException($"RA only turned {TurnDegrees:0.#}° between the two frames: turn it further (60–90° is best) and try again");
        axis = axis.Normalized;
        var ground = Apply(m1, axis);
        if (ground.Dot(Horizon.VisiblePole(_site)) < 0) axis = -1 * axis;
        _axisInCamera = axis;
        if (Apply(m1, axis).AngleTo(Horizon.VisiblePole(_site)) > 10)
            throw new ArgumentException($"The measured axis is {Apply(m1, axis).AngleTo(Horizon.VisiblePole(_site)):0}° from the pole: check the site's latitude and longitude");
        return Update(second);
    }

    /// <summary>The error from a new frame, taken with RA not turned since the second frame.</summary>
    public PolarError Update(PoleFrame frame)
    {
        var m = Orientation(frame);
        var axis = Apply(m, _axisInCamera);
        var pole = Horizon.VisiblePole(_site);
        if (_refraction) pole = Horizon.Refracted(pole);
        var field = Ground(frame, frame.Width / 2.0, frame.Height / 2.0);

        var (poleAlt, poleAz) = Horizon.AltAz(pole);
        var (axisAlt, axisAz) = Horizon.AltAz(axis);
        var dAz = ((axisAz - poleAz + 540) % 360) - 180;
        Error = new PolarError(
            Math.Round((axisAlt - poleAlt) * 60, 2),
            Math.Round(dAz * Math.Cos(poleAlt * Math.PI / 180) * 60, 2),
            Math.Round(axis.AngleTo(pole) * 60, 2),
            axisAlt, axisAz,
            axis.AngleTo(pole) > 2
                ? $"The axis is {axis.AngleTo(pole):0.#}° off: bring it within a degree or two first, then measure again for an accurate result."
                : null);

        // For drawing: the axis, the pole and Polaris in this frame, and which way is up from the axis.
        var up = Horizon.FromAltAz(Math.Min(axisAlt + 0.2, 89.9), axisAz);
        Marks = new PoleMarks(Pixel(frame, axis), Pixel(frame, pole), frame.Wcs.ToPixel(Polaris), Pixel(frame, up));
        return Error;
    }

    /// <summary>Where a ground-fixed direction falls in a frame (null if it's behind the camera).</summary>
    private (double X, double Y)? Pixel(PoleFrame frame, Vec3 ground)
    {
        var direction = _refraction ? Unrefracted(ground) : ground;
        var jnow = Horizon.FromVector(direction, frame.Utc, _site);
        return frame.Wcs.ToPixel(Precession.JNowToJ2000(jnow, frame.Utc));
    }

    /// <summary>The ground-fixed direction a pixel looks at (refracted, if refraction is on: where it appears).</summary>
    private Vec3 Ground(PoleFrame frame, double x, double y)
    {
        var v = Horizon.ToVector(Precession.J2000ToJNow(frame.Wcs.ToSky(x, y), frame.Utc), frame.Utc, _site);
        return _refraction ? Horizon.Refracted(v) : v;
    }

    private static Vec3 Unrefracted(Vec3 apparent)
    {
        // Refraction only lifts: undo it with a couple of fixed-point steps.
        var guess = apparent;
        for (var i = 0; i < 3; i++) guess = guess + (apparent - Horizon.Refracted(guess));
        return guess.Normalized;
    }

    /// <summary>
    /// The camera's orientation as columns (right, down, forward) in ground coordinates, from three pixel rays.
    /// Built the same way for every frame, so a mirrored image just mirrors all of them alike.
    /// </summary>
    private double[,] Orientation(PoleFrame frame)
    {
        var (cx, cy) = (frame.Width / 2.0, frame.Height / 2.0);
        var step = Math.Min(frame.Width, frame.Height) / 4.0;
        var forward = Ground(frame, cx, cy);
        var right = Ground(frame, cx + step, cy) - forward;
        right = (right - right.Dot(forward) * forward).Normalized;
        var down = Ground(frame, cx, cy + step) - forward;
        down = (down - down.Dot(forward) * forward - down.Dot(right) * right).Normalized;
        return new[,]
        {
            { right.X, down.X, forward.X },
            { right.Y, down.Y, forward.Y },
            { right.Z, down.Z, forward.Z },
        };
    }

    private static Vec3 Apply(double[,] m, Vec3 v) => new(
        m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z,
        m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z,
        m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z);

    private static double[,] Transpose(double[,] m)
    {
        var t = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            t[i, j] = m[j, i];
        return t;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        for (var k = 0; k < 3; k++)
            r[i, j] += a[i, k] * b[k, j];
        return r;
    }
}
