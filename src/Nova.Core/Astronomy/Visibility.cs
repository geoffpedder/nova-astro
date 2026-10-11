using Nova.Core.Equipment;

namespace Nova.Core.Astronomy;

/// <summary>How a target fares during the dark part of a night.</summary>
/// <param name="HoursAbove">Hours of darkness with the target above the minimum altitude.</param>
/// <param name="BestUtc">When it's highest during darkness (its transit, if that falls in the dark).</param>
/// <param name="MoonSeparation">Degrees from the Moon in the middle of the night.</param>
public sealed record Visibility(double MaxAltitude, DateTime BestUtc, double HoursAbove, double MoonSeparation);

public static class TargetVisibility
{
    private const double Rad = Math.PI / 180;

    /// <summary>
    /// Altitude (degrees) of a J2000 position, ignoring precession, nutation and refraction: within half a degree,
    /// and fast enough to rate thousands of objects across a night.
    /// </summary>
    public static double QuickAltitude(EquatorialCoordinates j2000, double localSiderealDegrees, double latitude)
    {
        var hourAngle = (localSiderealDegrees - j2000.RaHours * 15) * Rad;
        var (dec, lat) = (j2000.DecDegrees * Rad, latitude * Rad);
        return Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hourAngle)) / Rad;
    }

    /// <summary>Sample times across the dark part of the night, every <paramref name="step"/>.</summary>
    public static (DateTime Utc, double Lst)[] Samples(NightTimes night, GeoLocation site, TimeSpan step)
    {
        var list = new List<(DateTime, double)>();
        for (var t = night.DarkStart; t <= night.DarkEnd; t += step)
            list.Add((t, Horizon.LocalSiderealDegrees(t, site.Longitude)));
        return [.. list];
    }

    public static Visibility For(EquatorialCoordinates j2000, (DateTime Utc, double Lst)[] samples, GeoLocation site,
        double minAltitude, EquatorialCoordinates moonJ2000, TimeSpan step)
    {
        var best = double.MinValue;
        var bestUtc = samples.Length > 0 ? samples[0].Utc : default;
        var above = 0;
        foreach (var (utc, lst) in samples)
        {
            var altitude = QuickAltitude(j2000, lst, site.Latitude);
            if (altitude > best) (best, bestUtc) = (altitude, utc);
            if (altitude >= minAltitude) above++;
        }
        return new Visibility(Math.Round(best, 1), bestUtc, Math.Round(above * step.TotalHours, 2),
            Math.Round(Precession.Separation(j2000, moonJ2000), 1));
    }
}
