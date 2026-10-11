using Nova.Core.Equipment;

namespace Nova.Core.Astronomy;

/// <summary>A point a slew goes through, by hour angle (so it can be turned into RA when that leg starts).</summary>
/// <param name="HourAngle">Hours, −12..12 (negative: east of the meridian).</param>
public readonly record struct RoutePoint(double HourAngle, double DecDegrees);

/// <summary>
/// Plans GoTos that keep clear of the Sun. A German mount turns both axes at once, so a GoTo between two safe
/// points can sweep through the Sun on the way; and when it changes pier side the tube swings over the pole.
/// The direct path is checked both ways it might go; if either passes within the limit, the slew goes round:
/// straight up (same hour angle) to near the celestial pole, across there, and straight down to the target.
/// The Sun is never within 51° of Dec ±75°, so the middle leg is always safe.
/// </summary>
public static class SunSafeRoute
{
    /// <summary>The Dec (towards the visible pole) the detour crosses at.</summary>
    public const double DetourDec = 75;
    private const double StepDegrees = 1;

    /// <summary>
    /// The legs to slew (the last is the target), or null if no route keeps clear (the start or the target is
    /// on the far side of the Sun from the pole, straight up or down).
    /// </summary>
    public static IReadOnlyList<RoutePoint>? Plan(RoutePoint from, RoutePoint to, RoutePoint sun, double limitDegrees, double latitude)
    {
        if (DirectIsSafe(from, to, sun, limitDegrees)) return [to];
        var pole = latitude >= 0 ? DetourDec : -DetourDec;
        var up = new RoutePoint(from.HourAngle, pole);
        var across = new RoutePoint(to.HourAngle, pole);
        // The vertical legs: only the straight path (same hour angle, so no pier change).
        if (!Clear(Linear(from, up), sun, limitDegrees) || !Clear(Linear(across, to), sun, limitDegrees)) return null;
        return [up, across, to];
    }

    /// <summary>Whether a straight GoTo is safe whichever way the mount takes it.</summary>
    public static bool DirectIsSafe(RoutePoint from, RoutePoint to, RoutePoint sun, double limitDegrees) =>
        Clear(Linear(from, to), sun, limitDegrees) && Clear(OverThePole(from, to), sun, limitDegrees);

    /// <summary>Both axes turning together: hour angle and Dec change in step (the shorter way round in hour angle).</summary>
    public static IEnumerable<RoutePoint> Linear(RoutePoint from, RoutePoint to)
    {
        var dHa = ((to.HourAngle - from.HourAngle) % 24 + 36) % 24 - 12;
        var dDec = to.DecDegrees - from.DecDegrees;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dHa * 15), Math.Abs(dDec)) / StepDegrees));
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            yield return new RoutePoint(Wrap(from.HourAngle + dHa * t), from.DecDegrees + dDec * t);
        }
    }

    /// <summary>A pier change: the tube swings up over the pole and down the other side.</summary>
    public static IEnumerable<RoutePoint> OverThePole(RoutePoint from, RoutePoint to)
    {
        var pole = from.DecDegrees + to.DecDegrees >= 0 ? 90 : -90;
        foreach (var p in Linear(from, new RoutePoint(from.HourAngle, pole))) yield return p;
        foreach (var p in Linear(new RoutePoint(to.HourAngle, pole), to)) yield return p;
    }

    private static bool Clear(IEnumerable<RoutePoint> path, RoutePoint sun, double limitDegrees) =>
        path.All(p => Separation(p, sun) >= limitDegrees);

    public static double Separation(RoutePoint a, RoutePoint b) =>
        Precession.Separation(new EquatorialCoordinates(Wrap24(-a.HourAngle), a.DecDegrees), new EquatorialCoordinates(Wrap24(-b.HourAngle), b.DecDegrees));

    private static double Wrap(double hours) => ((hours + 12) % 24 + 24) % 24 - 12;
    private static double Wrap24(double hours) => (hours % 24 + 24) % 24;

    /// <summary>A JNow position as a route point at this time and place.</summary>
    public static RoutePoint At(EquatorialCoordinates jnow, DateTime utc, GeoLocation site) =>
        new(Horizon.HourAngle(jnow, utc, site.Longitude), jnow.DecDegrees);

    /// <summary>A route point as a JNow position now (its RA moves on with the sky).</summary>
    public static EquatorialCoordinates ToJNow(RoutePoint p, DateTime utc, GeoLocation site) =>
        new(Wrap24(Horizon.LocalSiderealDegrees(utc, site.Longitude) / 15 - p.HourAngle), p.DecDegrees);
}
