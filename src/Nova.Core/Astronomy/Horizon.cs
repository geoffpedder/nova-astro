using Nova.Core.Equipment;

namespace Nova.Core.Astronomy;

/// <summary>A direction or vector in the local horizon frame: X north, Y east, Z up.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(double k, Vec3 a) => new(k * a.X, k * a.Y, k * a.Z);
    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(Dot(this));
    public Vec3 Normalized => (1 / Length) * this;

    /// <summary>Angle to another direction, degrees.</summary>
    public double AngleTo(Vec3 b) => Math.Atan2(Cross(b).Length, Dot(b)) * 180 / Math.PI;

    /// <summary>Rotation by <paramref name="radians"/> about <paramref name="axis"/> (right-handed, Rodrigues).</summary>
    public Vec3 Rotate(Vec3 axis, double radians)
    {
        var k = axis.Normalized;
        var cos = Math.Cos(radians);
        return cos * this + Math.Sin(radians) * k.Cross(this) + (k.Dot(this) * (1 - cos)) * k;
    }
}

/// <summary>
/// The local horizon frame, fixed to the ground: where an equatorial (JNow) position is in the sky at a
/// given time and place, and back. Used where the Earth's rotation matters, e.g. polar alignment.
/// </summary>
public static class Horizon
{
    private const double Rad = Math.PI / 180;

    /// <summary>The Earth's rotation rate relative to the stars, radians per second.</summary>
    public const double SiderealRate = 2 * Math.PI / 86_164.0905;

    /// <summary>Local mean sidereal time in degrees (IAU 1982 GMST, Meeus 12.4) for a longitude east.</summary>
    public static double LocalSiderealDegrees(DateTime utc, double longitudeEast)
    {
        var d = Precession.JulianDate(utc) - 2451545.0;
        var t = d / 36525;
        var gmst = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38_710_000;
        return ((gmst + longitudeEast) % 360 + 360) % 360;
    }

    /// <summary>
    /// Hour angle of a JNow position in hours, −12 to +12: negative east of the meridian (rising), positive
    /// after it has crossed.
    /// </summary>
    public static double HourAngle(EquatorialCoordinates jnow, DateTime utc, double longitudeEast)
    {
        var hours = (LocalSiderealDegrees(utc, longitudeEast) / 15 - jnow.RaHours) % 24;
        if (hours < -12) hours += 24;
        if (hours >= 12) hours -= 24;
        return hours;
    }

    /// <summary>Altitude in degrees of a J2000 position at a time and place (no refraction).</summary>
    public static double Altitude(EquatorialCoordinates j2000, DateTime utc, GeoLocation site) =>
        AltAz(ToVector(Precession.J2000ToJNow(j2000, utc), utc, site)).Altitude;

    /// <summary>Clock time for the sky to turn by a number of hours of hour angle.</summary>
    public static TimeSpan SiderealToClock(double hours) => TimeSpan.FromHours(hours * 0.99726957);

    /// <summary>Unit vector towards a JNow position at a time and place (no refraction).</summary>
    public static Vec3 ToVector(EquatorialCoordinates jnow, DateTime utc, GeoLocation site)
    {
        var lat = site.Latitude * Rad;
        var dec = jnow.DecDegrees * Rad;
        var hourAngle = (LocalSiderealDegrees(utc, site.Longitude) - jnow.RaHours * 15) * Rad;
        return new Vec3(
            Math.Cos(lat) * Math.Sin(dec) - Math.Sin(lat) * Math.Cos(dec) * Math.Cos(hourAngle),
            -Math.Cos(dec) * Math.Sin(hourAngle),
            Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hourAngle));
    }

    /// <summary>The JNow position a horizon-frame direction points at, at a time and place.</summary>
    public static EquatorialCoordinates FromVector(Vec3 v, DateTime utc, GeoLocation site)
    {
        v = v.Normalized;
        var lat = site.Latitude * Rad;
        var dec = Math.Asin(Math.Clamp(Math.Sin(lat) * v.Z + Math.Cos(lat) * v.X, -1, 1));
        var hourAngle = Math.Atan2(-v.Y, Math.Cos(lat) * v.Z - Math.Sin(lat) * v.X) / Rad;
        var ra = ((LocalSiderealDegrees(utc, site.Longitude) - hourAngle) % 360 + 360) % 360;
        return new EquatorialCoordinates(ra / 15, dec / Rad);
    }

    public static (double Altitude, double Azimuth) AltAz(Vec3 v)
    {
        v = v.Normalized;
        var az = Math.Atan2(v.Y, v.X) / Rad;
        return (Math.Asin(Math.Clamp(v.Z, -1, 1)) / Rad, (az + 360) % 360);
    }

    /// <summary>Altitude and azimuth (from north through east) in degrees to a unit vector.</summary>
    public static Vec3 FromAltAz(double altitude, double azimuth) => new(
        Math.Cos(altitude * Rad) * Math.Cos(azimuth * Rad),
        Math.Cos(altitude * Rad) * Math.Sin(azimuth * Rad),
        Math.Sin(altitude * Rad));

    /// <summary>The celestial pole above the horizon (north for latitude ≥ 0, else south), as a direction.</summary>
    public static Vec3 VisiblePole(GeoLocation site) =>
        site.Latitude >= 0 ? FromAltAz(site.Latitude, 0) : FromAltAz(-site.Latitude, 180);

    /// <summary>The north celestial pole as a direction (below the horizon in the south).</summary>
    public static Vec3 NorthPole(GeoLocation site) => FromAltAz(site.Latitude, 0);

    /// <summary>
    /// Atmospheric refraction in arcminutes for a true (airless) altitude in degrees (Sæmundsson, standard
    /// conditions): how much higher an object appears.
    /// </summary>
    public static double RefractionArcmin(double trueAltitude) =>
        trueAltitude < -1 ? 0 : 1.02 / Math.Tan((trueAltitude + 10.3 / (trueAltitude + 5.11)) * Rad);

    /// <summary>A direction lifted by refraction: where it appears rather than where it is.</summary>
    public static Vec3 Refracted(Vec3 v)
    {
        var (alt, az) = AltAz(v);
        return FromAltAz(alt + RefractionArcmin(alt) / 60, az);
    }

    /// <summary>
    /// The rotation a polar-alignment knob turn makes: tilt the mount's axis up by <paramref name="altitudeRadians"/>
    /// (about the horizontal line across its azimuth <paramref name="axisAzimuth"/>), then turn it east by
    /// <paramref name="azimuthRadians"/> (about the zenith). Applies to everything on the mount.
    /// </summary>
    public static Vec3 KnobRotation(Vec3 v, double axisAzimuth, double altitudeRadians, double azimuthRadians)
    {
        var tiltAxis = new Vec3(Math.Sin(axisAzimuth * Rad), -Math.Cos(axisAzimuth * Rad), 0);
        return v.Rotate(tiltAxis, altitudeRadians).Rotate(new Vec3(0, 0, 1), azimuthRadians);
    }
}
