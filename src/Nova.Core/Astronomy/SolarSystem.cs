namespace Nova.Core.Astronomy;

/// <summary>
/// Low-precision Sun and Moon (geocentric, equinox of date): good to about 0.01° for the Sun and a few tenths
/// of a degree for the Moon — plenty for twilight times, moonlight and keeping clear of the Moon.
/// Sun from the Astronomical Almanac's low-precision formulae; Moon from Meeus' truncated series.
/// </summary>
public static class SolarSystem
{
    private const double Rad = Math.PI / 180;

    private static double Days(DateTime utc) => Precession.JulianDate(utc) - 2451545.0;

    private static double Obliquity(double days) => 23.439 - 0.0000004 * days;

    private static EquatorialCoordinates FromEcliptic(double longitude, double latitude, double obliquity)
    {
        var (l, b, e) = (longitude * Rad, latitude * Rad, obliquity * Rad);
        var ra = Math.Atan2(Math.Sin(l) * Math.Cos(e) - Math.Tan(b) * Math.Sin(e), Math.Cos(l));
        var dec = Math.Asin(Math.Sin(b) * Math.Cos(e) + Math.Cos(b) * Math.Sin(e) * Math.Sin(l));
        var hours = ra / Rad / 15 % 24;
        return new EquatorialCoordinates(hours < 0 ? hours + 24 : hours, dec / Rad);
    }

    private static double SunLongitude(double days)
    {
        var mean = 280.460 + 0.9856474 * days;
        var anomaly = (357.528 + 0.9856003 * days) * Rad;
        return mean + 1.915 * Math.Sin(anomaly) + 0.020 * Math.Sin(2 * anomaly);
    }

    /// <summary>The Sun (JNow).</summary>
    public static EquatorialCoordinates Sun(DateTime utc)
    {
        var days = Days(utc);
        return FromEcliptic(SunLongitude(days), 0, Obliquity(days));
    }

    private static (double Longitude, double Latitude) MoonEcliptic(double days)
    {
        var t = days / 36525;
        double S(double a, double b) => Math.Sin((a + b * t) * Rad);
        var longitude = 218.32 + 481267.881 * t
                        + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36) + 0.66 * S(235.7, 890534.22)
                        + 0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05) - 0.11 * S(186.5, 966404.03);
        var latitude = 5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89)
                       - 0.28 * S(318.3, 6003.15) - 0.17 * S(217.6, -407332.21);
        return (longitude, latitude);
    }

    /// <summary>The Moon (JNow, geocentric: up to a degree from where it appears, which doesn't matter here).</summary>
    public static EquatorialCoordinates Moon(DateTime utc)
    {
        var days = Days(utc);
        var (longitude, latitude) = MoonEcliptic(days);
        return FromEcliptic(longitude, latitude, Obliquity(days));
    }

    /// <summary>The lit fraction of the Moon, 0 (new) to 1 (full).</summary>
    public static double MoonIllumination(DateTime utc)
    {
        var days = Days(utc);
        var (longitude, latitude) = MoonEcliptic(days);
        var elongation = Math.Acos(Math.Cos((longitude - SunLongitude(days)) * Rad) * Math.Cos(latitude * Rad));
        return (1 - Math.Cos(elongation)) / 2;
    }

    /// <summary>Whether the Moon is waxing (growing towards full).</summary>
    public static bool MoonWaxing(DateTime utc)
    {
        var days = Days(utc);
        var difference = ((MoonEcliptic(days).Longitude - SunLongitude(days)) % 360 + 360) % 360;
        return difference < 180;
    }
}
