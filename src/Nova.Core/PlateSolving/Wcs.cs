using Nova.Core.Astronomy;

namespace Nova.Core.PlateSolving;

/// <summary>
/// A plate solution's mapping between sky and image (FITS WCS, gnomonic/TAN projection), as written by
/// ASTAP: reference pixel CRPIX (1-based FITS pixels), its sky position CRVAL (J2000, degrees) and the CD
/// matrix (degrees per pixel). NOVA keeps FITS rows in file order, so array pixel (i, j) is FITS pixel
/// (i + 1, j + 1) and the methods here work in 0-based array coordinates.
/// </summary>
public sealed record Wcs(double CrPix1, double CrPix2, double CrVal1, double CrVal2,
    double Cd11, double Cd12, double Cd21, double Cd22)
{
    private const double Rad = Math.PI / 180;

    /// <summary>
    /// A plain north-up, east-left mapping centred on <paramref name="center"/>: what a camera without any
    /// rotation or mirroring sees (and what NOVA's simulator draws).
    /// </summary>
    public static Wcs Simple(EquatorialCoordinates center, double arcsecPerPixel, int width, int height) =>
        Rotated(center, arcsecPerPixel, width, height, 0);

    /// <summary>
    /// The same, turned so the image's top (row 0) points at position angle <paramref name="positionAngle"/>
    /// (degrees from north through east): a camera turned on its rotator.
    /// </summary>
    public static Wcs Rotated(EquatorialCoordinates center, double arcsecPerPixel, int width, int height, double positionAngle)
    {
        var s = arcsecPerPixel / 3600;
        var t = positionAngle * Rad;
        var (cos, sin) = (Math.Cos(t), Math.Sin(t));
        return new Wcs((width + 1) / 2.0, (height + 1) / 2.0, center.RaHours * 15, center.DecDegrees,
            -s * cos, -s * sin, s * sin, -s * cos);
    }

    /// <summary>
    /// Where a J2000 position falls in the image (0-based pixel coordinates, may lie outside the image);
    /// null for points 90° or more from the image centre, which the projection can't show.
    /// </summary>
    public (double X, double Y)? ToPixel(EquatorialCoordinates position)
    {
        var ra = position.RaHours * 15 * Rad;
        var dec = position.DecDegrees * Rad;
        var ra0 = CrVal1 * Rad;
        var dec0 = CrVal2 * Rad;
        var cosC = Math.Sin(dec0) * Math.Sin(dec) + Math.Cos(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0);
        if (cosC <= 1e-6) return null;
        var xi = Math.Cos(dec) * Math.Sin(ra - ra0) / cosC / Rad;
        var eta = (Math.Cos(dec0) * Math.Sin(dec) - Math.Sin(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0)) / cosC / Rad;

        var det = Cd11 * Cd22 - Cd12 * Cd21;
        if (det == 0) return null;
        var u = (Cd22 * xi - Cd12 * eta) / det;
        var v = (-Cd21 * xi + Cd11 * eta) / det;
        return (CrPix1 + u - 1, CrPix2 + v - 1);
    }

    /// <summary>The J2000 sky position of a 0-based pixel coordinate.</summary>
    public EquatorialCoordinates ToSky(double x, double y)
    {
        var u = x + 1 - CrPix1;
        var v = y + 1 - CrPix2;
        var xi = (Cd11 * u + Cd12 * v) * Rad;
        var eta = (Cd21 * u + Cd22 * v) * Rad;
        var ra0 = CrVal1 * Rad;
        var dec0 = CrVal2 * Rad;
        var denominator = Math.Cos(dec0) - eta * Math.Sin(dec0);
        var ra = ra0 + Math.Atan2(xi, denominator);
        var dec = Math.Atan2(Math.Sin(dec0) + eta * Math.Cos(dec0), Math.Sqrt(xi * xi + denominator * denominator));
        var raHours = ra / Rad / 15 % 24;
        return new EquatorialCoordinates(raHours < 0 ? raHours + 24 : raHours, dec / Rad);
    }
}
