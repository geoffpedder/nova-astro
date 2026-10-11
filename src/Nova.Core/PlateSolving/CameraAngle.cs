using Nova.Core.Astronomy;

namespace Nova.Core.PlateSolving;

/// <summary>
/// The camera's rotation on the sky, as the framing tool means it: the position angle (degrees from north through
/// east) of the top of the frame as NOVA shows it, seen from the image centre. And how far to turn the camera to
/// reach a planned angle.
/// </summary>
public static class CameraAngle
{
    private const double Rad = Math.PI / 180;

    /// <summary>The position angle of the middle of the top edge (row 0) from the centre of a solved image.</summary>
    public static double OfTop(Wcs wcs, int width, int height)
    {
        var (w, h) = (width - 1.0, height - 1.0);
        return PositionAngle(wcs.ToSky(w / 2, h / 2), wcs.ToSky(w / 2, 0));
    }

    /// <summary>The direction of <paramref name="to"/> from <paramref name="from"/>, degrees 0–360 from north through east.</summary>
    public static double PositionAngle(EquatorialCoordinates from, EquatorialCoordinates to)
    {
        var da = (to.RaHours - from.RaHours) * 15 * Rad;
        var d1 = from.DecDegrees * Rad;
        var d2 = to.DecDegrees * Rad;
        var pa = Math.Atan2(Math.Sin(da), Math.Cos(d1) * Math.Tan(d2) - Math.Sin(d1) * Math.Cos(da)) / Rad;
        return (pa % 360 + 360) % 360;
    }

    /// <summary>
    /// The turn (degrees on the sky, positive = towards east, i.e. a larger position angle) from the camera's angle to
    /// the planned one. A frame turned 180° covers the same sky, upside down, so the turn is at most 90° either way;
    /// <c>UpsideDown</c> says the result will be the plan rotated by 180°.
    /// </summary>
    public static (double Degrees, bool UpsideDown) Turn(double planned, double camera)
    {
        var full = Wrap180(planned - camera);
        if (full > 90) return (full - 180, true);
        if (full <= -90) return (full + 180, true);
        return (full, false);
    }

    /// <summary>An angle as −180 (exclusive) to 180.</summary>
    public static double Wrap180(double degrees)
    {
        var a = (degrees % 360 + 360) % 360;
        return a > 180 ? a - 360 : a;
    }
}
