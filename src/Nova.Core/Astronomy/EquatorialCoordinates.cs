using System.Globalization;

namespace Nova.Core.Astronomy;

/// <summary>
/// Right ascension (hours, 0..24) and declination (degrees, -90..+90).
/// The epoch is not tracked yet: INDI mounts report JNow via EQUATORIAL_EOD_COORD.
/// TODO: add J2000 &lt;-&gt; JNow conversion (precession/nutation) before plate solving lands.
/// </summary>
public readonly record struct EquatorialCoordinates(double RaHours, double DecDegrees)
{
    public string RaText => Sexagesimal.Format(RaHours, 'h');
    public string DecText => Sexagesimal.Format(DecDegrees, 'd', signed: true);

    public override string ToString() => $"RA {RaText}  Dec {DecText}";

    /// <summary>Parses RA in hours and Dec in degrees, each as decimal or sexagesimal text.</summary>
    public static EquatorialCoordinates Parse(string ra, string dec)
    {
        var raHours = Sexagesimal.Parse(ra);
        var decDegrees = Sexagesimal.Parse(dec);
        if (raHours < 0 || raHours >= 24) throw new FormatException($"RA out of range (0..24h): {ra}");
        if (decDegrees < -90 || decDegrees > 90) throw new FormatException($"Dec out of range (-90..+90°): {dec}");
        return new EquatorialCoordinates(raHours, decDegrees);
    }
}

public static class Sexagesimal
{
    private static readonly char[] Separators = [':', ' ', 'h', 'H', 'd', 'D', '°', 'm', 'M', '\'', 's', 'S', '"'];

    /// <summary>
    /// Parses "12.5", "12:30:00", "12 30 00", "12h30m00s", "-05°30'15\"" into a decimal value.
    /// </summary>
    public static double Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length == 0) throw new FormatException("Empty value");

        var negative = s.StartsWith('-');
        if (negative || s.StartsWith('+')) s = s[1..];

        var parts = s.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 3) throw new FormatException($"Cannot parse '{text}'");

        double value = 0, scale = 1;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                throw new FormatException($"Cannot parse '{text}'");
            value += v / scale;
            scale *= 60;
        }
        return negative ? -value : value;
    }

    /// <summary>Formats as "12h30m15.2s" (unit 'h') or "+05°30'15\"" (unit 'd').</summary>
    public static string Format(double value, char unit, bool signed = false)
    {
        var sign = value < 0 ? "-" : signed ? "+" : "";
        var abs = Math.Abs(value);
        // Round to the display precision first so 59.95s never prints as "60.0s".
        var totalTenthsOrSeconds = unit == 'h' ? Math.Round(abs * 36000) : Math.Round(abs * 3600);
        if (unit == 'h')
        {
            var h = (long)(totalTenthsOrSeconds / 36000);
            var m = (long)(totalTenthsOrSeconds / 600 % 60);
            var sec = totalTenthsOrSeconds % 600 / 10.0;
            return $"{sign}{h:00}h{m:00}m{sec.ToString("00.0", CultureInfo.InvariantCulture)}s";
        }
        else
        {
            var d = (long)(totalTenthsOrSeconds / 3600);
            var m = (long)(totalTenthsOrSeconds / 60 % 60);
            var sec = (long)(totalTenthsOrSeconds % 60);
            return $"{sign}{d:00}°{m:00}'{sec:00}\"";
        }
    }
}
