using Nova.Core.Equipment;

namespace Nova.Core.Astronomy;

/// <summary>A plate-solved image centre (J2000) and the moment it was taken (mid-exposure).</summary>
public sealed record PolarSample(EquatorialCoordinates J2000, DateTime Utc);

/// <param name="AltitudeArcmin">Positive: the mount's axis points above the pole (lower it).</param>
/// <param name="AzimuthArcmin">
/// Positive: the axis points to the right of the pole as you face it — east of the north pole, west of the
/// south pole — so turn it left. Measured on the sky.
/// </param>
/// <param name="AxisAltitude">Where the axis points, degrees.</param>
/// <param name="Warning">Why the numbers may be unreliable right now (field due east/west or overhead, huge error).</param>
public sealed record PolarError(
    double AltitudeArcmin, double AzimuthArcmin, double TotalArcmin, double AxisAltitude, double AxisAzimuth,
    string? Warning = null);

/// <summary>
/// Three-point polar alignment (as NINA's TPPA plugin does it). Rotating only the RA axis moves the camera's
/// view along a circle around the mount's axis — and tracking does too — so three plate solves, turned into
/// ground-fixed directions, give the circle and with it where the axis points. Compared with the celestial
/// pole, that's the error.
/// <para>
/// While the user turns the altitude and azimuth knobs, the mount (and the camera with it) tilts. Each new
/// solve is compared with where the third point would be by now from tracking alone; the knob rotation that
/// explains the difference, applied to the axis, gives the updated error.
/// </para>
/// </summary>
public sealed class PolarAlignment
{
    /// <summary>
    /// How far apart (degrees) the three solves must be at least. Near the pole, turning RA moves the view only a
    /// little, but plate solves are good to an arcsecond or two, so a few arcminutes of movement is still plenty.
    /// </summary>
    public const double MinimumSpread = 0.05;

    private readonly GeoLocation _site;
    private readonly bool _refraction;
    private Vec3 _axis;        // as measured from the three points
    private Vec3 _reference;   // the third point
    private DateTime _referenceUtc;

    /// <param name="refraction">Aim at the refracted pole (where it appears) instead of the true pole.</param>
    public PolarAlignment(GeoLocation site, bool refraction = false)
    {
        _site = site;
        _refraction = refraction;
    }

    public PolarError? Error { get; private set; }

    /// <summary>
    /// Measures the axis from three solves taken with only the RA axis turning between them.
    /// Throws if they're too close together or in a line, i.e. RA wasn't really rotated.
    /// </summary>
    public PolarError Measure(PolarSample first, PolarSample second, PolarSample third)
    {
        var (a, b, c) = (Direction(first), Direction(second), Direction(third));
        var spread = Math.Max(a.AngleTo(b), Math.Max(b.AngleTo(c), a.AngleTo(c)));
        if (spread < MinimumSpread)
            throw new ArgumentException(
                $"The three positions are only {spread * 3600:0}″ apart: the mount doesn't seem to have turned in RA between them. " +
                "Check that it moves, or (turning by hand) turn RA by the full step before pressing Continue.");

        var normal = (b - a).Cross(c - a);
        if (normal.Length < 1e-9) throw new ArgumentException("The three positions lie on a line: was only the RA axis turned?");
        var axis = normal.Normalized;
        if (axis.Dot(Horizon.VisiblePole(_site)) < 0) axis = -1 * axis;
        if (axis.AngleTo(Horizon.VisiblePole(_site)) > 10)
            throw new ArgumentException($"The measured axis is {axis.AngleTo(Horizon.VisiblePole(_site)):0}° from the pole: check the site's latitude and longitude");

        _axis = axis;
        _reference = c;
        _referenceUtc = third.Utc;
        return Error = ErrorOf(axis, c);
    }

    /// <summary>The error after knob adjustments, from a new solve (RA not turned since the third point).</summary>
    public PolarError Update(PolarSample sample)
    {
        if (Error is null) throw new InvalidOperationException("Measure the three points first");
        var actual = Direction(sample);

        // Tracking alone turns the view about the axis at the sidereal rate (about the north-pointing axis).
        var northward = _axis.Dot(Horizon.NorthPole(_site)) >= 0 ? _axis : -1 * _axis;
        var expected = _reference.Rotate(northward, Horizon.SiderealRate * (sample.Utc - _referenceUtc).TotalSeconds);

        var (_, axisAzimuth) = Horizon.AltAz(_axis);
        var (tilt, turn) = SolveKnobs(expected, actual, axisAzimuth);
        return Error = ErrorOf(Horizon.KnobRotation(_axis, axisAzimuth, tilt, turn), actual);
    }

    private Vec3 Direction(PolarSample sample)
    {
        var v = Horizon.ToVector(Precession.J2000ToJNow(sample.J2000, sample.Utc), sample.Utc, _site);
        return _refraction ? Horizon.Refracted(v) : v;
    }

    private PolarError ErrorOf(Vec3 axis, Vec3 field)
    {
        var pole = Horizon.VisiblePole(_site);
        if (_refraction) pole = Horizon.Refracted(pole);
        var (poleAlt, poleAz) = Horizon.AltAz(pole);
        var (axisAlt, axisAz) = Horizon.AltAz(axis);
        var dAz = ((axisAz - poleAz + 540) % 360) - 180;
        return new PolarError(
            Math.Round((axisAlt - poleAlt) * 60, 2),
            Math.Round(dAz * Math.Cos(poleAlt * Math.PI / 180) * 60, 2),
            Math.Round(axis.AngleTo(pole) * 60, 2),
            axisAlt, axisAz,
            Warning(axis.AngleTo(pole), field));
    }

    /// <summary>
    /// Where the knob readings can't be trusted (as NINA's plugin warns too): with the camera looking near due
    /// east or west, or nearly overhead, the altitude and azimuth knobs move the stars almost the same way, so
    /// the split between them is poorly determined; and a very large error makes the small-turn maths rough.
    /// </summary>
    internal static string? Warning(double errorDegrees, Vec3 field)
    {
        var (alt, az) = Horizon.AltAz(field);
        var fromEastWest = Math.Min(Math.Abs(az - 90), Math.Abs(az - 270));
        if (alt > 75)
            return "The camera is looking nearly straight up: the altitude and azimuth readings can't be told apart well " +
                   "here. For the best results, point lower (30–60° up), nearer the meridian.";
        if (fromEastWest < 10 && alt < 60)
            return $"The camera is looking nearly due {(Math.Abs(az - 90) < 10 ? "east" : "west")}: the altitude and azimuth " +
                   "knobs move the stars almost the same way here, so the readings can jump. Point nearer the meridian for the best results.";
        if (errorDegrees > 2)
            return $"The axis is {errorDegrees:0.#}° off: get it within a degree or two first (with the knobs or a polar scope), " +
                   "then run polar alignment again for an accurate result.";
        return null;
    }

    /// <summary>The altitude tilt and azimuth turn (radians) that carry <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static (double Tilt, double Turn) SolveKnobs(Vec3 from, Vec3 to, double axisAzimuth)
    {
        // Two unknowns, two conditions: zero offset along two directions across the target. Newton's method
        // from (0, 0); knob turns are small, so it converges in a few steps.
        var up = new Vec3(0, 0, 1);
        var across = up.Cross(to);
        across = across.Length < 1e-6 ? new Vec3(0, 1, 0) : across.Normalized;
        var along = to.Cross(across).Normalized;
        (double, double) Residual(double tilt, double turn)
        {
            var moved = Horizon.KnobRotation(from, axisAzimuth, tilt, turn);
            return (moved.Dot(across), moved.Dot(along));
        }

        double t = 0, u = 0;
        const double h = 1e-7;
        for (var i = 0; i < 8; i++)
        {
            var (f1, f2) = Residual(t, u);
            var (a1, a2) = Residual(t + h, u);
            var (b1, b2) = Residual(t, u + h);
            double j11 = (a1 - f1) / h, j21 = (a2 - f2) / h, j12 = (b1 - f1) / h, j22 = (b2 - f2) / h;
            var det = j11 * j22 - j12 * j21;
            if (Math.Abs(det) < 1e-12) break; // pointing where the knobs can't be told apart (zenith, due east/west)
            var dt = (j22 * f1 - j12 * f2) / det;
            var du = (-j21 * f1 + j11 * f2) / det;
            t -= dt;
            u -= du;
            if (Math.Abs(dt) + Math.Abs(du) < 1e-12) break;
        }
        return (t, u);
    }
}
