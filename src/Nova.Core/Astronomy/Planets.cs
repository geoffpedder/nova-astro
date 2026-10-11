namespace Nova.Core.Astronomy;

/// <summary>A planet's place and look right now.</summary>
/// <param name="Position">J2000, geocentric.</param>
/// <param name="DistanceAu">From the Earth.</param>
/// <param name="DiameterArcsec">Apparent equatorial diameter.</param>
public sealed record PlanetPosition(string Name, EquatorialCoordinates Position, double Magnitude, double DistanceAu, double DiameterArcsec);

/// <summary>
/// The planets (and Pluto) from JPL's Keplerian elements for approximate positions (E. M. Standish, valid
/// 1800–2050, public domain): within a few arcminutes — enough for the sky chart and a GoTo before centring.
/// Heliocentric ecliptic positions of the planet and of the Earth–Moon barycentre, differenced, then turned to
/// J2000 equatorial. Magnitudes from each planet's absolute magnitude, distances and a simple phase term.
/// </summary>
public static class Planets
{
    private const double Rad = Math.PI / 180;
    private const double KmPerAu = 149_597_870.7;
    private const double Obliquity = 23.43928 * Rad; // J2000

    // a (AU), e, I, L, long. perihelion, long. node (degrees) at J2000, then their rates per Julian century.
    private sealed record Elements(
        string Name, double A, double E, double I, double L, double Peri, double Node,
        double DA, double DE, double DI, double DL, double DPeri, double DNode,
        double AbsoluteMagnitude, double PhaseCoefficient, double RadiusKm);

    private static readonly Elements[] Table =
    [
        new("Mercury", 0.38709927, 0.20563593, 7.00497902, 252.25032350, 77.45779628, 48.33076593,
            0.00000037, 0.00001906, -0.00594749, 149472.67411175, 0.16047689, -0.12534081, -0.42, 0.038, 2439.7),
        new("Venus", 0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718, 76.67984255,
            0.00000390, -0.00004107, -0.00078890, 58517.81538729, 0.00268329, -0.27769418, -4.40, 0.013, 6051.8),
        new("Mars", 1.52371034, 0.09339410, 1.84969142, -4.55343205, -23.94362959, 49.55953891,
            0.00001847, 0.00007882, -0.00813131, 19140.30268499, 0.44441088, -0.29257343, -1.52, 0.016, 3389.5),
        new("Jupiter", 5.20288700, 0.04838624, 1.30439695, 34.39644051, 14.72847983, 100.47390909,
            -0.00011607, -0.00013253, -0.00183714, 3034.74612775, 0.21252668, 0.20469106, -9.40, 0.005, 69911),
        new("Saturn", 9.53667594, 0.05386179, 2.48599187, 49.95424423, 92.59887831, 113.66242448,
            -0.00125060, -0.00050991, 0.00193609, 1222.49362201, -0.41897216, -0.28867794, -8.88, 0.044, 58232),
        new("Uranus", 19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630, 74.01692503,
            -0.00196176, -0.00004397, -0.00242939, 428.48202785, 0.40805281, 0.04240589, -7.19, 0.002, 25362),
        new("Neptune", 30.06992276, 0.00859048, 1.77004347, -55.12002969, 44.96476227, 131.78422574,
            0.00026291, 0.00005105, 0.00035372, 218.45945325, -0.32241464, -0.00508664, -6.87, 0, 24622),
        new("Pluto", 39.48211675, 0.24882730, 17.14001206, 238.92903833, 224.06891629, 110.30393684,
            -0.00031596, 0.00005170, 0.00004818, 145.20780515, -0.04062942, -0.01183482, -1.0, 0.041, 1188.3),
    ];

    private static readonly Elements EarthMoon = new("Earth", 1.00000261, 0.01671123, -0.00001531, 100.46457166, 102.93768193, 0,
        0.00000562, -0.00004392, -0.01294668, 35999.37244981, 0.32327364, 0, 0, 0, 0);

    public static IReadOnlyList<string> Names { get; } = Table.Select(t => t.Name).ToArray();

    /// <summary>Every planet (and Pluto) at a moment.</summary>
    public static IReadOnlyList<PlanetPosition> At(DateTime utc)
    {
        var t = (Precession.JulianDate(utc) - 2451545.0) / 36525;
        var earth = Heliocentric(EarthMoon, t);
        return Table.Select(p => Geocentric(p, t, earth)).ToList();
    }

    public static PlanetPosition? Find(string name, DateTime utc) =>
        At(utc).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static PlanetPosition Geocentric(Elements p, double t, (double X, double Y, double Z) earth)
    {
        var helio = Heliocentric(p, t);
        var (x, y, z) = (helio.X - earth.X, helio.Y - earth.Y, helio.Z - earth.Z);
        var distance = Math.Sqrt(x * x + y * y + z * z);
        // Ecliptic to equatorial (J2000).
        var ye = y * Math.Cos(Obliquity) - z * Math.Sin(Obliquity);
        var ze = y * Math.Sin(Obliquity) + z * Math.Cos(Obliquity);
        var ra = Math.Atan2(ye, x) / Rad / 15;
        var dec = Math.Asin(ze / distance) / Rad;

        var sunDistance = Math.Sqrt(helio.X * helio.X + helio.Y * helio.Y + helio.Z * helio.Z);
        var earthDistance = Math.Sqrt(earth.X * earth.X + earth.Y * earth.Y + earth.Z * earth.Z);
        // Phase angle (Sun–planet–Earth).
        var cosPhase = (sunDistance * sunDistance + distance * distance - earthDistance * earthDistance) / (2 * sunDistance * distance);
        var phase = Math.Acos(Math.Clamp(cosPhase, -1, 1)) / Rad;
        var magnitude = 5 * Math.Log10(sunDistance * distance) + PhaseMagnitude(p, phase);
        var diameter = 2 * p.RadiusKm / (distance * KmPerAu) / Rad * 3600;
        return new PlanetPosition(p.Name, new EquatorialCoordinates(ra < 0 ? ra + 24 : ra, dec),
            Math.Round(magnitude, 2), Math.Round(distance, 4), Math.Round(diameter, 2));
    }

    /// <summary>
    /// Brightness at unit distances for a phase angle (degrees). Mercury, Venus and Mars use Mallama &amp; Hilton
    /// (2018), which get their crescents right; the outer planets barely show a phase, so a small linear term.
    /// </summary>
    private static double PhaseMagnitude(Elements p, double a) => p.Name switch
    {
        "Mercury" => -0.613 + 6.3280e-2 * a - 1.6336e-3 * a * a + 3.3644e-5 * Math.Pow(a, 3) - 3.4265e-7 * Math.Pow(a, 4)
                     + 1.6893e-9 * Math.Pow(a, 5) - 3.0334e-12 * Math.Pow(a, 6),
        "Venus" when a <= 163.7 => -4.384 - 1.044e-3 * a + 3.687e-4 * a * a - 2.814e-6 * Math.Pow(a, 3) + 8.938e-9 * Math.Pow(a, 4),
        "Venus" => 236.05828 - 2.81914 * a + 8.39034e-3 * a * a,
        "Mars" => -1.601 + 2.267e-2 * a - 1.302e-4 * a * a,
        _ => p.AbsoluteMagnitude + p.PhaseCoefficient * a,
    };

    private static (double X, double Y, double Z) Heliocentric(Elements p, double t)
    {
        var a = p.A + p.DA * t;
        var e = p.E + p.DE * t;
        var i = (p.I + p.DI * t) * Rad;
        var l = p.L + p.DL * t;
        var peri = p.Peri + p.DPeri * t;
        var node = (p.Node + p.DNode * t) * Rad;
        var w = peri * Rad - node; // argument of perihelion
        var m = ((l - peri) % 360 + 540) % 360 - 180; // mean anomaly, −180..180
        // Kepler's equation by Newton's method (degrees, as in the JPL notes).
        var ed = e / Rad;
        var ecc = m + ed * Math.Sin(m * Rad);
        for (var k = 0; k < 10; k++)
        {
            var dm = m - (ecc - ed * Math.Sin(ecc * Rad));
            var de = dm / (1 - e * Math.Cos(ecc * Rad));
            ecc += de;
            if (Math.Abs(de) < 1e-7) break;
        }
        var xp = a * (Math.Cos(ecc * Rad) - e);
        var yp = a * Math.Sqrt(1 - e * e) * Math.Sin(ecc * Rad);
        var (cw, sw, cn, sn, ci, si) = (Math.Cos(w), Math.Sin(w), Math.Cos(node), Math.Sin(node), Math.Cos(i), Math.Sin(i));
        return (
            (cw * cn - sw * sn * ci) * xp + (-sw * cn - cw * sn * ci) * yp,
            (cw * sn + sw * cn * ci) * xp + (-sw * sn + cw * cn * ci) * yp,
            sw * si * xp + cw * si * yp);
    }
}
