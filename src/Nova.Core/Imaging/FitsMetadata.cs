using System.Globalization;
using Nova.Core.Astronomy;

namespace Nova.Core.Imaging;

/// <summary>Reads what common capture programs (INDI, NINA, Ekos, SharpCap…) record about a frame.</summary>
public static class FitsMetadata
{
    /// <summary>
    /// Arcseconds per pixel of this image (binning included): an explicit scale keyword if present,
    /// otherwise from the focal length and pixel size (XPIXSZ already includes binning, by convention).
    /// </summary>
    public static double? ImageScale(RawImage image)
    {
        foreach (var key in new[] { "PIXSCALE", "SCALE", "SECPIX1", "SECPIX" })
            if (Number(image, key) is > 0 and var scale) return scale;

        if (Number(image, "FOCALLEN") is > 0 and var focal && Number(image, "XPIXSZ") is > 0 and var pixel)
            return 206.265 * pixel / focal;
        return null;
    }

    /// <summary>
    /// Where the telescope was pointing according to the header: RA/DEC in degrees, or OBJCTRA/OBJCTDEC as
    /// sexagesimal ("05 35 17.3", "-05 23 28"). Good enough as a plate-solving hint; null if absent.
    /// </summary>
    public static EquatorialCoordinates? Coordinates(RawImage image)
    {
        if (Number(image, "RA") is { } raDeg && Number(image, "DEC") is { } decDeg && raDeg is >= 0 and < 360 && decDeg is >= -90 and <= 90)
            return new EquatorialCoordinates(raDeg / 15, decDeg);

        if (image.Headers.TryGetValue("OBJCTRA", out var ra) && image.Headers.TryGetValue("OBJCTDEC", out var dec))
        {
            try
            {
                return EquatorialCoordinates.Parse(ra, dec);
            }
            catch (FormatException)
            {
                // unreadable: no hint
            }
        }
        return null;
    }

    /// <summary>When the exposure started (DATE-OBS), if recorded.</summary>
    public static DateTime? Started(RawImage image) =>
        image.Headers.TryGetValue("DATE-OBS", out var text)
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    public static double? Number(RawImage image, string key) =>
        image.Headers.TryGetValue(key, out var text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
