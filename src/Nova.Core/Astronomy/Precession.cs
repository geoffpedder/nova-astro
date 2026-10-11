namespace Nova.Core.Astronomy;

/// <summary>
/// Converts between J2000 (catalogues, plate solves, FITS WCS) and JNow (the equator and equinox of date,
/// which INDI mounts use in EQUATORIAL_EOD_COORD). Precession only: IAU 1976 rotation (Meeus, Astronomical
/// Algorithms, ch. 21). Nutation and aberration (~20″) are left out — far below what centring needs.
/// </summary>
public static class Precession
{
    private const double J2000 = 2451545.0;
    private const double ArcsecToRad = Math.PI / (180 * 3600);

    public static EquatorialCoordinates J2000ToJNow(EquatorialCoordinates j2000, DateTime utc)
    {
        var (zeta, z, theta) = Angles(utc);
        var ra = j2000.RaHours * 15 * Math.PI / 180;
        var dec = j2000.DecDegrees * Math.PI / 180;

        var a = Math.Cos(dec) * Math.Sin(ra + zeta);
        var b = Math.Cos(theta) * Math.Cos(dec) * Math.Cos(ra + zeta) - Math.Sin(theta) * Math.Sin(dec);
        var c = Math.Sin(theta) * Math.Cos(dec) * Math.Cos(ra + zeta) + Math.Cos(theta) * Math.Sin(dec);
        return FromRadians(Math.Atan2(a, b) + z, Math.Asin(Math.Clamp(c, -1, 1)));
    }

    public static EquatorialCoordinates JNowToJ2000(EquatorialCoordinates jnow, DateTime utc)
    {
        // The exact inverse rotation of J2000ToJNow.
        var (zeta, z, theta) = Angles(utc);
        var ra = jnow.RaHours * 15 * Math.PI / 180 - z;
        var dec = jnow.DecDegrees * Math.PI / 180;

        var a = Math.Cos(dec) * Math.Sin(ra);
        var b = Math.Cos(theta) * Math.Cos(dec) * Math.Cos(ra) + Math.Sin(theta) * Math.Sin(dec);
        var c = -Math.Sin(theta) * Math.Cos(dec) * Math.Cos(ra) + Math.Cos(theta) * Math.Sin(dec);
        return FromRadians(Math.Atan2(a, b) - zeta, Math.Asin(Math.Clamp(c, -1, 1)));
    }

    /// <summary>Angle between two positions on the sky, in degrees.</summary>
    public static double Separation(EquatorialCoordinates a, EquatorialCoordinates b)
    {
        var ra1 = a.RaHours * 15 * Math.PI / 180;
        var ra2 = b.RaHours * 15 * Math.PI / 180;
        var d1 = a.DecDegrees * Math.PI / 180;
        var d2 = b.DecDegrees * Math.PI / 180;
        // Haversine: accurate for the tiny angles that matter when centring.
        var h = Math.Pow(Math.Sin((d2 - d1) / 2), 2) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin((ra2 - ra1) / 2), 2);
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h))) * 180 / Math.PI;
    }

    private static (double Zeta, double Z, double Theta) Angles(DateTime utc)
    {
        var t = (JulianDate(utc) - J2000) / 36525; // centuries since J2000
        var zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
        var z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
        var theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;
        return (zeta, z, theta);
    }

    public static double JulianDate(DateTime utc) =>
        utc.ToUniversalTime().ToOADate() + 2415018.5; // OLE automation dates count days from 1899-12-30

    private static EquatorialCoordinates FromRadians(double ra, double dec)
    {
        var hours = ra * 180 / Math.PI / 15 % 24;
        if (hours < 0) hours += 24;
        return new EquatorialCoordinates(hours, dec * 180 / Math.PI);
    }
}
