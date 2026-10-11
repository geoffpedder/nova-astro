using Nova.Core.Equipment;

namespace Nova.Core.Astronomy;

/// <summary>
/// One night at a site (UTC): sunset to sunrise, with the end and start of nautical (Sun 12° down) and
/// astronomical (18° down) twilight. A time is null when the Sun doesn't get that low — short summer nights
/// far from the equator have no astronomical darkness at all.
/// </summary>
/// <param name="DarkStart">When it's properly dark: astronomical dusk, else nautical, else sunset.</param>
public sealed record NightTimes(
    DateTime Sunset, DateTime Sunrise, DateTime? NauticalDusk, DateTime? NauticalDawn, DateTime? AstronomicalDusk,
    DateTime? AstronomicalDawn, double MoonIllumination, bool MoonWaxing)
{
    public DateTime DarkStart => AstronomicalDusk ?? NauticalDusk ?? Sunset;
    public DateTime DarkEnd => AstronomicalDawn ?? NauticalDawn ?? Sunrise;
}

public static class Night
{
    public const double SunsetAltitude = -0.833; // upper limb on the horizon, with refraction

    public static double SunAltitude(DateTime utc, GeoLocation site) =>
        Horizon.AltAz(Horizon.ToVector(SolarSystem.Sun(utc), utc, site)).Altitude;

    public static double MoonAltitude(DateTime utc, GeoLocation site) =>
        Horizon.AltAz(Horizon.ToVector(SolarSystem.Moon(utc), utc, site)).Altitude;

    /// <summary>
    /// The night in progress at <paramref name="utc"/>, or the next one if it's daytime. Null where the Sun doesn't
    /// set (polar summer) or rise (polar winter) within a day.
    /// </summary>
    public static NightTimes? Around(DateTime utc, GeoLocation site)
    {
        var step = TimeSpan.FromMinutes(10);
        DateTime sunset;
        if (SunAltitude(utc, site) > SunsetAltitude)
        {
            if (Crossing(utc, utc.AddHours(26), step, site, SunsetAltitude, falling: true) is not { } next) return null;
            sunset = next;
        }
        else
        {
            // It's night: find when it began.
            if (Crossing(utc.AddHours(-26), utc, step, site, SunsetAltitude, falling: true, last: true) is not { } began) return null;
            sunset = began;
        }
        if (Crossing(sunset.AddMinutes(1), sunset.AddHours(26), step, site, SunsetAltitude, falling: false) is not { } sunrise) return null;

        DateTime? Dusk(double altitude) => Crossing(sunset, sunrise, step, site, altitude, falling: true);
        DateTime? Dawn(double altitude) => Crossing(sunset, sunrise, step, site, altitude, falling: false, last: true);
        var middle = sunset + (sunrise - sunset) / 2;
        return new NightTimes(sunset, sunrise, Dusk(-12), Dawn(-12), Dusk(-18), Dawn(-18),
            Math.Round(SolarSystem.MoonIllumination(middle), 3), SolarSystem.MoonWaxing(middle));
    }

    /// <summary>When the Sun crosses <paramref name="altitude"/> (first or last time in the window), to a few seconds.</summary>
    private static DateTime? Crossing(DateTime from, DateTime to, TimeSpan step, GeoLocation site, double altitude, bool falling, bool last = false)
    {
        DateTime? found = null;
        var previous = SunAltitude(from, site) - altitude;
        for (var t = from + step; t <= to + step; t += step)
        {
            var at = t > to ? to : t;
            var now = SunAltitude(at, site) - altitude;
            if (falling ? previous > 0 && now <= 0 : previous < 0 && now >= 0)
            {
                // Bisect within the step.
                var (lo, hi) = (at - step, at);
                for (var i = 0; i < 12; i++)
                {
                    var mid = lo + (hi - lo) / 2;
                    var value = SunAltitude(mid, site) - altitude;
                    if (falling ? value > 0 : value < 0) lo = mid;
                    else hi = mid;
                }
                found = lo + (hi - lo) / 2;
                if (!last) return found;
            }
            previous = now;
            if (at == to) break;
        }
        return found;
    }
}
