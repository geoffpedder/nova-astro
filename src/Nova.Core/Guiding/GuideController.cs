using Nova.Core.Equipment;

namespace Nova.Core.Guiding;

/// <summary>Which way Dec may be corrected: both, one way only (when polar alignment drifts one way), or not at all.</summary>
public enum DecGuideMode { Auto, North, South, Off }

/// <summary>RA's algorithm: Hysteresis (reacts to each error), or Predictive PEC (also corrects the mount's learned periodic error ahead of time).</summary>
public enum RaAlgorithm { Hysteresis, PredictivePec }

/// <summary>Dec's algorithm: Resist Switch (slow to reverse; PHD2's usual), or Lowpass2 (follows a fitted line through the recent errors).</summary>
public enum DecAlgorithm { ResistSwitch, Lowpass2 }

/// <summary>The guiding algorithms' settings, with PHD2-like defaults suited to a strain-wave mount (ZWO AM3/AM5).</summary>
/// <param name="PecPeriodSeconds">Predictive PEC: the periodic error's period if known (seconds); 0 finds it.</param>
/// <param name="PecPredictionGain">Predictive PEC: how much of the predicted motion to correct ahead (0–1).</param>
/// <param name="PecReactiveAggressiveness">Predictive PEC: the reactive part's aggressiveness, gentler as the prediction does most of the work.</param>
/// <param name="DecBacklashCompensation">When a Dec correction reverses, add a pulse to take up the backlash measured at calibration (adjusted as it guides).</param>
/// <param name="DecAlgorithm">Dec's algorithm; both use <c>DecAggressiveness</c> and <c>DecMinMove</c> (FastSwitch is Resist Switch's).</param>
public sealed record GuideAlgorithmSettings(
    double RaHysteresis = 0.1,
    double RaAggressiveness = 0.7,
    double RaMinMove = 0.15,
    double DecAggressiveness = 1.0,
    double DecMinMove = 0.2,
    bool DecFastSwitch = true,
    DecGuideMode DecMode = DecGuideMode.Auto,
    int MaxRaPulseMs = 2500,
    int MaxDecPulseMs = 2500,
    RaAlgorithm RaAlgorithm = RaAlgorithm.Hysteresis,
    double PecPeriodSeconds = 0,
    double PecPredictionGain = 0.8,
    double PecReactiveAggressiveness = 0.5,
    bool DecBacklashCompensation = true,
    DecAlgorithm DecAlgorithm = DecAlgorithm.ResistSwitch);

/// <summary>A guide correction to send.</summary>
public sealed record GuidePulse(GuideDirection Direction, int Milliseconds);

/// <summary>One frame's guiding result: the error on each axis (pixels) and the pulses to send (null = none).</summary>
/// <param name="DecBacklashMs">The part of the Dec pulse added to take up backlash on a reversal (ms), or 0.</param>
public sealed record GuideCorrection(double RaPixels, double DecPixels, GuidePulse? Ra, GuidePulse? Dec, int DecBacklashMs = 0);

/// <summary>
/// Dec backlash compensation, as PHD2 does it: when a Dec correction goes the other way from the last one, the gears
/// have slack to take up before the star moves, so that much pulse is added. It starts from the backlash measured at
/// calibration and adjusts itself: if the star still needs pushing the same way the frame after, the next reversal gets
/// more; if it overshot, less (never more than twice what was measured, nor more than the longest Dec pulse).
/// </summary>
public sealed class DecBacklashCompensator
{
    /// <summary>Less than this measured at calibration (pixels) is noise, not backlash.</summary>
    public const double MinimumPixels = 0.5;

    private DateTime? _seededFrom;
    private int _lastDirection; // +1 north, -1 south, 0 none yet
    private int _checkDirection; // a compensated reversal to judge on the next frame

    /// <summary>The backlash measured at calibration, as pulse time (ms; 0 if too small to matter).</summary>
    public double MeasuredMs { get; private set; }
    /// <summary>The pulse added on a reversal now (ms), adjusted from <see cref="MeasuredMs"/> as it guides.</summary>
    public double PulseMs { get; private set; }
    /// <summary>Reversals compensated since the calibration.</summary>
    public int Applied { get; private set; }
    public int Increases { get; private set; }
    public int Decreases { get; private set; }

    /// <summary>Starts from a calibration's measurement (once per calibration).</summary>
    public void Seed(GuideCalibration calibration)
    {
        if (_seededFrom == calibration.Utc) return;
        _seededFrom = calibration.Utc;
        MeasuredMs = calibration.DecBacklashPixels >= MinimumPixels ? calibration.DecBacklashMs : 0;
        PulseMs = MeasuredMs;
        (Applied, Increases, Decreases, _checkDirection) = (0, 0, 0, 0);
    }

    /// <summary>
    /// Adds the compensation to a Dec pulse that reverses the last one (when <paramref name="enabled"/>), and learns from
    /// how the last compensated reversal turned out. Returns the milliseconds added.
    /// </summary>
    /// <param name="errorPixels">This frame's Dec error.</param>
    /// <param name="threshold">An error under this after a compensated reversal is close enough to leave it as it is.</param>
    public int Apply(ref GuidePulse? pulse, double errorPixels, bool enabled, double threshold, int maxMs)
    {
        var direction = pulse is null ? 0 : pulse.Direction == GuideDirection.North ? 1 : -1;
        if (_checkDirection != 0)
        {
            if (direction != 0 && Math.Abs(errorPixels) >= threshold)
            {
                var ceiling = Math.Min(2 * MeasuredMs, maxMs);
                if (direction == _checkDirection)
                {
                    PulseMs = Math.Min(PulseMs * 1.1 + 10, ceiling); // still short: the slack was more
                    Increases++;
                }
                else
                {
                    PulseMs *= 0.8; // overshot: less
                    Decreases++;
                }
            }
            _checkDirection = 0;
        }
        if (direction == 0) return 0;

        var added = 0;
        if (enabled && _lastDirection != 0 && direction != _lastDirection && PulseMs >= 10)
        {
            added = (int)Math.Round(PulseMs);
            pulse = pulse! with { Milliseconds = pulse.Milliseconds + added };
            _checkDirection = direction;
            Applied++;
        }
        _lastDirection = direction;
        return added;
    }
}

/// <summary>
/// Turns a star offset into guide pulses: split into RA and Dec with the calibration, each through its algorithm
/// (Hysteresis or Predictive PEC for RA, Resist Switch or Lowpass2 for Dec), then pulse length = correction ÷ that axis's rate, capped.
/// </summary>
public sealed class GuideController
{
    private readonly HysteresisAlgorithm _ra = new();
    private readonly HysteresisAlgorithm _raReactive = new(); // Predictive PEC's reactive part
    private readonly ResistSwitchAlgorithm _dec = new();
    private readonly Lowpass2Algorithm _decLowpass = new();

    /// <summary>Predictive PEC's model of the mount's periodic error (learning whenever Predictive PEC is chosen).</summary>
    public PeriodicErrorPredictor Pec { get; } = new();

    /// <summary>Dec backlash compensation: the pulse added when Dec reverses (kept over Reset: the gears don't forget).</summary>
    public DecBacklashCompensator Backlash { get; } = new();

    public GuideController(GuideAlgorithmSettings? settings = null) => Settings = settings ?? new GuideAlgorithmSettings();

    public GuideAlgorithmSettings Settings
    {
        get;
        set
        {
            field = value;
            (_ra.Hysteresis, _ra.Aggressiveness, _ra.MinMove) = (value.RaHysteresis, value.RaAggressiveness, value.RaMinMove);
            (_raReactive.Hysteresis, _raReactive.Aggressiveness, _raReactive.MinMove) = (value.RaHysteresis, value.PecReactiveAggressiveness, value.RaMinMove);
            Pec.PeriodOverride = value.PecPeriodSeconds > 0 ? value.PecPeriodSeconds : null;
            (_dec.Aggressiveness, _dec.MinMove, _dec.FastSwitch) = (value.DecAggressiveness, value.DecMinMove, value.DecFastSwitch);
            (_decLowpass.Aggressiveness, _decLowpass.MinMove) = (value.DecAggressiveness, value.DecMinMove);
        }
    }

    /// <summary>Forgets the algorithms' history (starting to guide, after a dither or a lost star). The PEC model stays.</summary>
    public void Reset()
    {
        _ra.Reset();
        _raReactive.Reset();
        _dec.Reset();
        _decLowpass.Reset();
    }

    /// <summary>The lock moved along RA on purpose (a dither): the PEC model's record of the mount's motion carries on smoothly.</summary>
    public void LockMoved(double raPixels) => Pec.Applied(raPixels);

    /// <param name="dx">The star's offset from home in the image (pixels).</param>
    /// <param name="time">The frame's time (seconds, any steady clock): Predictive PEC needs it.</param>
    /// <param name="steady">Not settling: the frame can teach the PEC model.</param>
    public GuideCorrection Correct(double dx, double dy, GuideCalibration calibration, double? time = null, bool steady = true)
    {
        var (raError, decError) = calibration.ToAxes(dx, dy);

        // Star displaced the way a West pulse moves it → pulse East to bring it back (and North → South).
        // Predictive PEC: the predicted wander until the next frame, corrected now, plus a gentler reactive part;
        // plain Hysteresis while it's still learning.
        double raCorrection;
        double? predicted = null;
        if (Settings.RaAlgorithm == RaAlgorithm.PredictivePec && time is { } t)
        {
            Pec.Add(t, raError, steady);
            predicted = Pec.Interval is { } dt ? Pec.Predict(t, dt) : null;
        }
        if (predicted is { } p)
        {
            _ra.Reset(); // its blend shouldn't carry stale corrections back when PEC stops predicting
            raCorrection = Settings.PecPredictionGain * p + _raReactive.Correction(raError);
        }
        else
        {
            _raReactive.Reset();
            raCorrection = _ra.Correction(raError);
        }
        var ra = ToPulse(raCorrection, calibration.RaRate, Settings.MaxRaPulseMs, GuideDirection.East, GuideDirection.West);
        if (Settings.RaAlgorithm == RaAlgorithm.PredictivePec && ra is not null)
            Pec.Applied(ra.Milliseconds / 1000.0 * calibration.RaRate * (ra.Direction == GuideDirection.East ? 1 : -1));

        GuidePulse? dec = null;
        if (Settings.DecMode != DecGuideMode.Off)
        {
            var decCorrection = Settings.DecAlgorithm == DecAlgorithm.Lowpass2 ? _decLowpass.Correction(decError) : _dec.Correction(decError);
            dec = ToPulse(decCorrection, calibration.DecRate, Settings.MaxDecPulseMs, GuideDirection.South, GuideDirection.North);
            if (dec is not null &&
                ((Settings.DecMode == DecGuideMode.North && dec.Direction != GuideDirection.North) ||
                 (Settings.DecMode == DecGuideMode.South && dec.Direction != GuideDirection.South)))
                dec = null;
        }
        Backlash.Seed(calibration);
        var added = Backlash.Apply(ref dec, decError, Settings.DecBacklashCompensation, Math.Max(2 * Settings.DecMinMove, 0.3), Settings.MaxDecPulseMs);
        return new GuideCorrection(raError, decError, ra, dec, added);
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
