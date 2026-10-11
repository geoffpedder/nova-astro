using System.Globalization;
using System.Text.RegularExpressions;
using Nova.Core.Equipment;

namespace Nova.Core.Imaging;

/// <summary>
/// What a saved frame's file name is built from. An unknown filter is written as "None", an unknown
/// frame type or temperature as an empty part (so the parts always sit in the same places).
/// </summary>
/// <param name="Temperature">Sensor temperature in °C.</param>
/// <param name="FrameNumber">The frame's number within its sequence (1, 2, …); null for single exposures.</param>
public sealed record FrameDescription(
    DateTime LocalTime, double ExposureSeconds, FrameType? FrameType, string? Filter, double? Temperature,
    int? FrameNumber = null);

/// <summary>
/// File names for saved frames, NINA style: <c>2026-08-14_01-19-05_Light_Ha_-10.00_300.00s_0024.fits</c> — local
/// date and time, frame type, filter, sensor temperature, exposure, and for sequence frames the frame's number in
/// the sequence. Sortable by time, and easy to group by type, filter, temperature and exposure (for matching darks).
/// </summary>
public static partial class FileNaming
{
    /// <summary>Used in place of the filter when there's no filter wheel or the slot has no name.</summary>
    public const string NoFilter = "None";

    public static string FileName(FrameDescription frame)
    {
        var parts = new List<string>
        {
            frame.LocalTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            frame.FrameType?.ToString() ?? "",
            Clean(frame.Filter) ?? NoFilter,
            frame.Temperature is { } t ? Number(t) : "",
            Number(frame.ExposureSeconds) + "s",
        };
        if (frame.FrameNumber is { } n) parts.Add(n.ToString("D4", CultureInfo.InvariantCulture));
        return string.Join("_", parts) + ".fits";
    }

    /// <summary>Two decimals, and never "-0.00".</summary>
    private static string Number(double value)
    {
        var rounded = Math.Round(value, 2);
        return (rounded == 0 ? 0 : rounded).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>The path to save to, adding _2, _3… rather than overwriting an existing file.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem}_{n}{extension}");
        return path;
    }

    /// <summary>
    /// Makes a value safe for file names on any OS: letters (including e.g. α), digits, '.', '+' and '-' are kept, anything
    /// else (spaces, slashes, and '_' which separates the parts) becomes '-'. Null if nothing is left.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = UnsafeCharacters().Replace(value.Trim(), "-").Trim('-', '.');
        cleaned = RepeatedDashes().Replace(cleaned, "-");
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Frame type from a FITS IMAGETYP value ("Light Frame", "Dark Frame", "Flat Field", "Bias Frame").</summary>
    public static FrameType? FrameTypeFromHeader(string? imageType) => imageType?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        var t when t.StartsWith("light") => FrameType.Light,
        var t when t.StartsWith("dark") => FrameType.Dark,
        var t when t.StartsWith("flat") => FrameType.Flat,
        var t when t.StartsWith("bias") || t.StartsWith("offset") || t.StartsWith("zero") => FrameType.Bias,
        _ => null,
    };

    [GeneratedRegex(@"[^\p{L}\p{N}.+\-]")]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashes();
}
