using Nova.Core.Equipment;

namespace Nova.Core.Guiding;

/// <summary>What the calibrator wants next: a pulse, then a frame to see where the star went.</summary>
public sealed record CalibrationStep(GuideDirection Direction, int Milliseconds, string Phase);

/// <summary>
/// Calibration as PHD2 does it, as a state machine fed one star position per frame: clear Dec backlash (north until
/// the star moves), step west until the star has moved <c>targetPixels</c>, the same number east to come back, then
/// north and south the same way. The west and north moves give each axis's angle and rate.
/// </summary>
public sealed class GuideCalibrator
{
    private enum Phase { Backlash, West, East, North, South, Done, Failed }

    private const int MaxBacklashSteps = 30;
    private const double BacklashMovedPixels = 3;

    private readonly int _stepMs;
    private readonly double _target;
    private readonly int _maxSteps;
    private readonly double _decDegrees;
    private readonly PierSide? _pierSide;
    private readonly bool _skipDec;
    private Phase _phase = Phase.Backlash;
    private int _steps, _westSteps, _northSteps;
    private double _startX, _startY;
    private bool _started;
    private (double Dx, double Dy) _west, _north;

    /// <param name="stepMs">Each pulse (ms): see <see cref="StepMilliseconds"/>.</param>
    /// <param name="targetPixels">How far each axis should move the star (PHD2 uses 25).</param>
    /// <param name="skipDec">Calibrate RA only (Dec guiding off): Dec gets a square-on placeholder.</param>
    public GuideCalibrator(int stepMs, double decDegrees, PierSide? pierSide, double targetPixels = 25, int maxSteps = 60,
        bool skipDec = false)
    {
        _stepMs = Math.Max(10, stepMs);
        _target = targetPixels;
        _maxSteps = maxSteps;
        _decDegrees = decDegrees;
        _pierSide = pierSide;
        _skipDec = skipDec;
        if (skipDec) _phase = Phase.West;
    }

    public bool IsDone => _phase == Phase.Done;
    public bool IsFailed => _phase == Phase.Failed;
    public string? Error { get; private set; }
    public GuideCalibration? Result { get; private set; }
    /// <summary>A note on a calibration that worked but looks doubtful (axes far from square, very different rates).</summary>
    public string? Warning { get; private set; }
    public string Status => _phase switch
    {
        Phase.Backlash => $"Clearing Dec backlash ({_steps})",
        Phase.West => $"West step {_steps}",
        Phase.East => $"East step {_steps} of {_westSteps}",
        Phase.North => $"North step {_steps}",
        Phase.South => $"South step {_steps} of {_northSteps}",
        Phase.Done => "Calibrated",
        _ => "Calibration failed",
    };

    /// <summary>
    /// The pulse length that moves the star <paramref name="targetPixels"/> in about <paramref name="steps"/> steps:
    /// RA covers guideRate × 15.04 × cos(Dec) ″ per second.
    /// </summary>
    public static int StepMilliseconds(double arcsecPerPixel, double guideRateSidereal, double decDegrees,
        double targetPixels = 25, int steps = 12)
    {
        var arcsecPerSecond = Math.Max(0.05, guideRateSidereal) * 15.041 * Math.Max(0.2, Math.Cos(decDegrees * Math.PI / 180));
        var ms = targetPixels * arcsecPerPixel / arcsecPerSecond / steps * 1000;
        return (int)Math.Clamp(Math.Round(ms / 50) * 50, 100, 5000);
    }

    /// <summary>Feeds where the star is now; returns the next pulse, or null when calibration has finished or failed.</summary>
    public CalibrationStep? Next(double x, double y)
    {
        if (!_started)
        {
            (_startX, _startY, _started) = (x, y, true);
            return Pulse(_phase == Phase.Backlash ? GuideDirection.North : GuideDirection.West);
        }

        var (dx, dy) = (x - _startX, y - _startY);
        var moved = Math.Sqrt(dx * dx + dy * dy);
        switch (_phase)
        {
            case Phase.Backlash:
                if (moved >= BacklashMovedPixels || _steps >= MaxBacklashSteps)
                {
                    // Moved (or as cleared as it gets): RA starts from here.
                    Begin(Phase.West, x, y);
                    return Pulse(GuideDirection.West);
                }
                return Pulse(GuideDirection.North);

            case Phase.West:
                if (moved >= _target || _steps >= _maxSteps)
                {
                    if (moved < Math.Min(_target / 3, 5))
                        return Fail($"The star moved only {moved:0.#} px west in {_steps} steps: check the guide output and the guide rate");
                    (_west, _westSteps) = ((dx, dy), _steps);
                    Begin(Phase.East, x, y);
                    return Pulse(GuideDirection.East);
                }
                return Pulse(GuideDirection.West);

            case Phase.East:
                if (_steps >= _westSteps)
                {
                    if (_skipDec) return Finish();
                    Begin(Phase.North, x, y);
                    return Pulse(GuideDirection.North);
                }
                return Pulse(GuideDirection.East);

            case Phase.North:
                if (moved >= _target || _steps >= _maxSteps)
                {
                    if (moved < Math.Min(_target / 3, 5))
                        return Fail($"The star moved only {moved:0.#} px north in {_steps} steps: check Dec backlash or turn Dec guiding off");
                    (_north, _northSteps) = ((dx, dy), _steps);
                    Begin(Phase.South, x, y);
                    return Pulse(GuideDirection.South);
                }
                return Pulse(GuideDirection.North);

            case Phase.South:
                if (_steps >= _northSteps) return Finish();
                return Pulse(GuideDirection.South);

            default:
                return null;
        }
    }

    /// <summary>The star was lost mid-calibration.</summary>
    public void Lost() => Fail("The guide star was lost during calibration");

    private CalibrationStep Pulse(GuideDirection direction)
    {
        _steps++;
        return new CalibrationStep(direction, _stepMs, _phase.ToString());
    }

    private void Begin(Phase phase, double x, double y) => (_phase, _steps, _startX, _startY) = (phase, 0, x, y);

    private CalibrationStep? Fail(string error)
    {
        (_phase, Error) = (Phase.Failed, error);
        return null;
    }

    private CalibrationStep? Finish()
    {
        var seconds = _stepMs / 1000.0;
        var raAngle = Math.Atan2(_west.Dy, _west.Dx);
        var raRate = Math.Sqrt(_west.Dx * _west.Dx + _west.Dy * _west.Dy) / (_westSteps * seconds);
        double decAngle, decRate;
        if (_skipDec)
        {
            // Square on to RA, turned the usual way (north is 90° anticlockwise of west seen on the sky; with y down,
            // −90° in image angle; a mirrored image would be the other way, but Dec isn't guided).
            (decAngle, decRate) = (raAngle - Math.PI / 2, raRate);
        }
        else
        {
            decAngle = Math.Atan2(_north.Dy, _north.Dx);
            decRate = Math.Sqrt(_north.Dx * _north.Dx + _north.Dy * _north.Dy) / (_northSteps * seconds);
        }
        Result = new GuideCalibration(raAngle, raRate, decAngle, decRate, _decDegrees, _pierSide, DateTime.UtcNow);
        _phase = Phase.Done;
        if (!_skipDec && Result.OrthogonalityError > 10)
            Warning = $"The RA and Dec moves are {Result.OrthogonalityError:0}° from square (should be under 10°): " +
                "Dec backlash or polar alignment may have spoiled it; calibrate again near the meridian and Dec 0";
        return null;
    }
}
