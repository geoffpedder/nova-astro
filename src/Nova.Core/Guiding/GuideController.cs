using Nova.Core.Equipment;

namespace Nova.Core.Guiding;

/// <summary>Which way Dec may be corrected: both, one way only (when polar alignment drifts one way), or not at all.</summary>
public enum DecGuideMode { Auto, North, South, Off }

/// <summary>The guiding algorithms' settings, with PHD2-like defaults suited to a strain-wave mount (ZWO AM3/AM5).</summary>
public sealed record GuideAlgorithmSettings(
    double RaHysteresis = 0.1,
    double RaAggressiveness = 0.7,
    double RaMinMove = 0.15,
    double DecAggressiveness = 1.0,
    double DecMinMove = 0.2,
    bool DecFastSwitch = true,
    DecGuideMode DecMode = DecGuideMode.Auto,
    int MaxRaPulseMs = 2500,
    int MaxDecPulseMs = 2500);

/// <summary>A guide correction to send.</summary>
public sealed record GuidePulse(GuideDirection Direction, int Milliseconds);

/// <summary>One frame's guiding result: the error on each axis (pixels) and the pulses to send (null = none).</summary>
public sealed record GuideCorrection(double RaPixels, double DecPixels, GuidePulse? Ra, GuidePulse? Dec);

/// <summary>
/// Turns a star offset into guide pulses: split into RA and Dec with the calibration, each through its algorithm
/// (Hysteresis for RA, Resist Switch for Dec), then pulse length = correction ÷ that axis's rate, capped.
/// </summary>
public sealed class GuideController
{
    private readonly HysteresisAlgorithm _ra = new();
    private readonly ResistSwitchAlgorithm _dec = new();

    public GuideController(GuideAlgorithmSettings? settings = null) => Settings = settings ?? new GuideAlgorithmSettings();

    public GuideAlgorithmSettings Settings
    {
        get;
        set
        {
            field = value;
            (_ra.Hysteresis, _ra.Aggressiveness, _ra.MinMove) = (value.RaHysteresis, value.RaAggressiveness, value.RaMinMove);
            (_dec.Aggressiveness, _dec.MinMove, _dec.FastSwitch) = (value.DecAggressiveness, value.DecMinMove, value.DecFastSwitch);
        }
    }

    /// <summary>Forgets the algorithms' history (starting to guide, after a dither or a lost star).</summary>
    public void Reset()
    {
        _ra.Reset();
        _dec.Reset();
    }

    /// <param name="dx">The star's offset from home in the image (pixels).</param>
    public GuideCorrection Correct(double dx, double dy, GuideCalibration calibration)
    {
        var (raError, decError) = calibration.ToAxes(dx, dy);

        // Star displaced the way a West pulse moves it → pulse East to bring it back (and North → South).
        var raCorrection = _ra.Correction(raError);
        var ra = ToPulse(raCorrection, calibration.RaRate, Settings.MaxRaPulseMs, GuideDirection.East, GuideDirection.West);

        GuidePulse? dec = null;
        if (Settings.DecMode != DecGuideMode.Off)
        {
            var decCorrection = _dec.Correction(decError);
            dec = ToPulse(decCorrection, calibration.DecRate, Settings.MaxDecPulseMs, GuideDirection.South, GuideDirection.North);
            if (dec is not null &&
                ((Settings.DecMode == DecGuideMode.North && dec.Direction != GuideDirection.North) ||
                 (Settings.DecMode == DecGuideMode.South && dec.Direction != GuideDirection.South)))
                dec = null;
        }
        return new GuideCorrection(raError, decError, ra, dec);
    }

    private static GuidePulse? ToPulse(double correction, double ratePixelsPerSecond, int maxMs, GuideDirection positive, GuideDirection negative)
    {
        if (correction == 0 || ratePixelsPerSecond <= 0) return null;
        var ms = (int)Math.Round(Math.Abs(correction) / ratePixelsPerSecond * 1000);
        if (ms < 1) return null;
        return new GuidePulse(correction > 0 ? positive : negative, Math.Min(ms, maxMs));
    }

    /// <summary>
    /// A random dither offset (pixels, image axes) up to <paramref name="pixels"/> on each guide axis; RA only moves
    /// along the RA direction (for mounts with Dec backlash).
    /// </summary>
    public static (double Dx, double Dy) DitherOffset(Random random, double pixels, bool raOnly, GuideCalibration calibration)
    {
        var ra = (random.NextDouble() * 2 - 1) * pixels;
        var dec = raOnly ? 0 : (random.NextDouble() * 2 - 1) * pixels;
        return (ra * Math.Cos(calibration.RaAngle) + dec * Math.Cos(calibration.DecAngle),
                ra * Math.Sin(calibration.RaAngle) + dec * Math.Sin(calibration.DecAngle));
    }
}
