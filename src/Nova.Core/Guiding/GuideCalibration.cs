using Nova.Core.Equipment;

namespace Nova.Core.Guiding;

/// <summary>
/// How the guide star moves in the guide camera for each axis: a West pulse moves it along <see cref="RaAngle"/>
/// at <see cref="RaRate"/>, a North pulse along <see cref="DecAngle"/> at <see cref="DecRate"/> (pixels per second
/// of pulse; angles in radians from the image's +x axis, y down). Measured at <see cref="DecDegrees"/> on
/// <see cref="PierSide"/>; <see cref="For"/> adapts it to where the scope points now.
/// </summary>
public sealed record GuideCalibration(
    double RaAngle, double RaRate, double DecAngle, double DecRate, double DecDegrees, PierSide? PierSide, DateTime Utc)
{
    /// <summary>How far from square the axes are (degrees): a few is normal, over ~10 means a poor calibration.</summary>
    public double OrthogonalityError
    {
        get
        {
            var between = Math.Abs(((RaAngle - DecAngle) * 180 / Math.PI % 360 + 540) % 360 - 180);
            return Math.Abs(90 - between);
        }
    }

    /// <summary>
    /// This calibration where the scope points now: RA moves scale with cos(Dec) (a guide pulse turns the RA axis by
    /// a fixed angle, which covers less sky towards the pole), and after a meridian flip the camera has turned half
    /// way round, so both directions turn 180° in the image (Dec reversed as well when <paramref name="reverseDecAfterFlip"/>,
    /// for mounts whose Dec pulses change sense on the other side).
    /// </summary>
    public GuideCalibration For(double decDegrees, PierSide? pierSide, bool reverseDecAfterFlip = false)
    {
        var scale = Math.Cos(decDegrees * Math.PI / 180) / Math.Max(0.05, Math.Cos(DecDegrees * Math.PI / 180));
        var raRate = RaRate * Math.Clamp(scale, 0.05, 20);
        var flipped = PierSide is not null && pierSide is not null && pierSide != PierSide;
        var raAngle = flipped ? RaAngle + Math.PI : RaAngle;
        var decAngle = flipped ? DecAngle + Math.PI + (reverseDecAfterFlip ? Math.PI : 0) : DecAngle;
        return this with { RaAngle = raAngle, RaRate = raRate, DecAngle = decAngle, DecDegrees = decDegrees, PierSide = pierSide };
    }

    /// <summary>
    /// Splits a star offset (pixels, from where it should be) into how far it is along the West-pulse direction and
    /// along the North-pulse direction (pixels). Solves for both at once, so axes that aren't quite square still
    /// come apart correctly.
    /// </summary>
    public (double Ra, double Dec) ToAxes(double dx, double dy)
    {
        var (wx, wy) = (Math.Cos(RaAngle), Math.Sin(RaAngle));
        var (nx, ny) = (Math.Cos(DecAngle), Math.Sin(DecAngle));
        var det = wx * ny - wy * nx;
        if (Math.Abs(det) < 1e-6) return (dx * wx + dy * wy, dx * nx + dy * ny); // degenerate: plain projections
        return ((dx * ny - dy * nx) / det, (wx * dy - wy * dx) / det);
    }
}

/// <summary>Turns one axis's error (pixels along that axis's pulse direction) into a correction (pixels to undo).</summary>
public interface IGuideAlgorithm
{
    double Correction(double errorPixels);
    void Reset();
}

/// <summary>
/// PHD2's Hysteresis (its usual RA algorithm): blends in part of the last correction to smooth the response, scales
/// by the aggressiveness, and ignores errors under the minimum move.
/// </summary>
public sealed class HysteresisAlgorithm(double hysteresis = 0.1, double aggressiveness = 0.7, double minMove = 0.15) : IGuideAlgorithm
{
    private double _last;

    public double Hysteresis { get; set; } = hysteresis;
    public double Aggressiveness { get; set; } = aggressiveness;
    public double MinMove { get; set; } = minMove;

    public double Correction(double error)
    {
        var result = ((1 - Hysteresis) * error + Hysteresis * _last) * Aggressiveness;
        if (Math.Abs(error) < MinMove) result = 0;
        _last = result;
        return result;
    }

    public void Reset() => _last = 0;
}

/// <summary>
/// PHD2's Resist Switch (its usual Dec algorithm): corrects in one direction only and is slow to reverse, since Dec
/// mostly drifts one way (polar alignment) and reversing costs backlash. It switches when the error has stayed on
/// the other side for a few frames, or at once for a large jump when fast switching is on.
/// </summary>
public sealed class ResistSwitchAlgorithm(double aggressiveness = 1.0, double minMove = 0.2, bool fastSwitch = true) : IGuideAlgorithm
{
    private const int FramesToSwitch = 3;
    private readonly Queue<double> _recent = new();
    private int _side; // +1, −1, or 0 while undecided

    public double Aggressiveness { get; set; } = aggressiveness;
    public double MinMove { get; set; } = minMove;
    public bool FastSwitch { get; set; } = fastSwitch;

    public double Correction(double error)
    {
        _recent.Enqueue(error);
        while (_recent.Count > FramesToSwitch) _recent.Dequeue();
        if (Math.Abs(error) < MinMove) return 0;

        var sign = Math.Sign(error);
        if (sign != _side)
        {
            var steady = _recent.Count == FramesToSwitch && _recent.All(e => Math.Sign(e) == sign && Math.Abs(e) >= MinMove);
            var jump = FastSwitch && Math.Abs(error) > 3 * MinMove;
            if (_side == 0 || steady || jump) _side = sign;
            else return 0;
        }
        return error * Aggressiveness;
    }

    public void Reset()
    {
        _recent.Clear();
        _side = 0;
    }
}

/// <summary>
/// Whether guiding has settled after starting or a dither: the error has stayed under <c>Pixels</c> for
/// <c>Seconds</c>, before <c>Timeout</c> runs out (PHD2's settle semantics).
/// </summary>
public sealed class SettleTracker(SettleSettings settings, DateTime startUtc)
{
    private DateTime? _since;

    /// <summary>Feeds one frame's error; true once settled, false while not yet; throws when the timeout passes.</summary>
    public bool Add(DateTime utc, double? distancePixels)
    {
        if (distancePixels is { } d && d <= settings.Pixels) _since ??= utc;
        else _since = null;
        if (_since is { } since && (utc - since).TotalSeconds >= settings.Seconds) return true;
        if ((utc - startUtc).TotalSeconds > settings.Timeout)
            throw new TimeoutException($"Guiding didn't settle within {settings.Pixels:0.#} px for {settings.Seconds:0} s in {settings.Timeout:0} s");
        return false;
    }
}
