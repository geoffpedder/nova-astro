using System.Text.RegularExpressions;

namespace Nova.Core.Guiding;

/// <summary>A built-in starting point for the guide settings: exposure, gain and the algorithms (not the equipment).</summary>
public sealed record GuidePreset(string Name, string Description, double ExposureSeconds, double? Gain, GuideAlgorithmSettings Algorithms);

/// <summary>Dither and settle defaults for a mount: how far to dither (guide pixels) and when guiding counts as settled.</summary>
public sealed record GuideDefaults(string Mount, double DitherPixels, int DitherEveryFrames, double SettlePixels, double SettleSeconds, double SettleTimeout);

/// <summary>
/// Built-in guide settings, and defaults by mount. ZWO's AM mounts (AM3, AM5) have strain-wave drives: almost no RA
/// backlash but a fast periodic error, so shorter guide exposures follow it better; their guide pixel is usually
/// several imaging pixels, so dithers can be small and settling tight.
/// </summary>
public static partial class GuidePresets
{
    public static readonly GuidePreset Standard = new("NOVA defaults",
        "2 s exposures, PHD2's usual algorithms (RA Hysteresis 70%, Dec Resist Switch)", 2, null, new GuideAlgorithmSettings());

    public static readonly GuidePreset ZwoAm = new("ZWO AM mount",
        "Strain-wave drive (AM3, AM5): 1.5 s exposures to follow its fast periodic error, gain 60", 1.5, 60, new GuideAlgorithmSettings());

    public static readonly GuidePreset PoorSeeing = new("Poor seeing",
        "3 s exposures and gentler corrections that don't chase the seeing", 3, null,
        new GuideAlgorithmSettings(RaHysteresis: 0.1, RaAggressiveness: 0.5, RaMinMove: 0.25, DecAggressiveness: 0.8, DecMinMove: 0.3));

    public static IReadOnlyList<GuidePreset> All { get; } = [Standard, ZwoAm, PoorSeeing];

    [GeneratedRegex(@"\bZWO\s*AM\s*\d|\bAM[357]\b", RegexOptions.IgnoreCase)]
    private static partial Regex ZwoAmName();

    /// <summary>Whether a mount (by its driver's name) is one of ZWO's AM strain-wave mounts.</summary>
    public static bool IsZwoAm(string? mountName) => mountName is not null && ZwoAmName().IsMatch(mountName);

    /// <summary>Dither and settle defaults for this mount.</summary>
    public static GuideDefaults DefaultsFor(string? mountName) => IsZwoAm(mountName)
        ? new GuideDefaults("ZWO AM", DitherPixels: 2.5, DitherEveryFrames: 3, SettlePixels: 0.7, SettleSeconds: 8, SettleTimeout: 60)
        : new GuideDefaults("", DitherPixels: 5, DitherEveryFrames: 3, SettlePixels: 1.5, SettleSeconds: 10, SettleTimeout: 60);
}
