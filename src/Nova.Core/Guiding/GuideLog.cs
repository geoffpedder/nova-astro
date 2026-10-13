using System.Globalization;
using Nova.Core.Equipment;

namespace Nova.Core.Guiding;

/// <summary>One guide frame for the log (pixels, PHD2's columns).</summary>
/// <param name="Dx">The star's offset from the lock position in the image.</param>
/// <param name="RaRaw">The error along RA and Dec, before the algorithms.</param>
/// <param name="RaGuide">How far the corrections were meant to move it (0 without a pulse).</param>
public sealed record GuideLogStep(
    double Dx, double Dy, double RaRaw, double DecRaw, double RaGuide, double DecGuide,
    GuidePulse? Ra, GuidePulse? Dec, double StarMass, double Snr);

/// <summary>
/// A guide log in PHD2's format (PHD2_GuideLog, log version 2.5), so the tools made for PHD2's logs can read NOVA's:
/// a "Guiding Begins" section with the settings, a row per guide frame (time, offsets, the error on each axis and
/// the pulses sent), INFO lines for dithers and settling, and calibration sections with every step. Text is
/// appended a line at a time (a USB drive pulled out loses at most the line being written).
/// </summary>
public sealed class GuideLog(Func<string?> file)
{
    private const string Columns =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    private DateTime _start;
    private int _frame;
    private bool _guiding;
    private bool _calibrating;

    /// <summary>Writing failed (the folder can't be written): the reason, once per problem.</summary>
    public event Action<string>? Problem;

    public bool Guiding => _guiding;

    public void BeginGuiding(DateTime utc, IEnumerable<string> header)
    {
        if (_guiding) EndGuiding(utc);
        (_guiding, _start, _frame) = (true, utc, 0);
        Write(["", $"Guiding Begins at {Local(utc)}", .. header, Columns]);
    }

    public void Step(DateTime utc, GuideLogStep s)
    {
        if (!_guiding) return;
        _frame++;
        Write([string.Join(',',
            _frame.ToString(CultureInfo.InvariantCulture), Seconds(utc), "\"Mount\"",
            F(s.Dx), F(s.Dy), F(s.RaRaw), F(s.DecRaw), F(s.RaGuide), F(s.DecGuide),
            Ms(s.Ra), Letter(s.Ra), Ms(s.Dec), Letter(s.Dec), "", "",
            s.StarMass.ToString("0", CultureInfo.InvariantCulture), s.Snr.ToString("0.00", CultureInfo.InvariantCulture), "0")]);
    }

    /// <summary>A frame without the star: PHD2's DROP row.</summary>
    public void StarLost(DateTime utc, string reason)
    {
        if (!_guiding) return;
        _frame++;
        Write([$"{_frame},{Seconds(utc)},\"DROP\",,,,,,,,,,,,,0,0.00,1,\"{reason.Replace("\"", "'")}\""]);
    }

    public void Info(string text)
    {
        if (_guiding || _calibrating) Write([$"INFO: {text}"]);
    }

    public void EndGuiding(DateTime utc)
    {
        if (!_guiding) return;
        _guiding = false;
        Write([$"Guiding Ends at {Local(utc)}"]);
    }

    public void BeginCalibration(DateTime utc, IEnumerable<string> header)
    {
        _calibrating = true;
        Write(["", $"Calibration Begins at {Local(utc)}", .. header, "Direction,Step,dx,dy,x,y,Dist"]);
    }

    /// <summary>
    /// The calibration's steps (from the calibrator's path, pixels from where the West steps began) and its result or
    /// why it failed. dx/dy and Dist are from where each direction's steps began, as in PHD2's log.
    /// </summary>
    public void EndCalibration(DateTime utc, IReadOnlyList<CalibrationPoint> points, GuideCalibration? result, string? error,
        double? arcsecPerPixel)
    {
        if (!_calibrating) return;
        _calibrating = false;
        var lines = new List<string>();
        foreach (var phase in new[] { "West", "East", "North", "South" })
        {
            var steps = points.Where(p => p.Phase == phase).ToList();
            if (steps.Count == 0) continue;
            var (x0, y0) = (steps[0].X, steps[0].Y);
            for (var i = 0; i < steps.Count; i++)
            {
                var (dx, dy) = (steps[i].X - x0, steps[i].Y - y0);
                lines.Add($"{phase},{i},{F(dx)},{F(dy)},{F(steps[i].X)},{F(steps[i].Y)},{F(Math.Sqrt(dx * dx + dy * dy))}");
            }
            if (result is not null && phase == "West")
                lines.Add($"West calibration complete. Angle = {Deg(result.RaAngle):0.0} deg, Rate = {result.RaRate:0.000} px/sec, Parity = +");
            if (result is not null && phase == "North")
                lines.Add($"North calibration complete. Angle = {Deg(result.DecAngle):0.0} deg, Rate = {result.DecRate:0.000} px/sec, Parity = +");
        }
        if (result is not null)
        {
            if (arcsecPerPixel is { } s)
                lines.Add(FormattableString.Invariant($"Calibration guide speeds: RA = {result.RaRate * s:0.0} a-s/s, Dec = {result.DecRate * s:0.0} a-s/s"));
            lines.Add(FormattableString.Invariant($"Calibration complete, mount = Mount. {result.OrthogonalityError:0.0} deg from orthogonal."));
        }
        else
        {
            lines.Add($"Calibration failed: {error ?? "unknown"}");
        }
        lines.Add($"Calibration Ends at {Local(utc)}");
        Write(lines);
    }

    /// <summary>The first line of a new file, as PHD2 starts its logs.</summary>
    public static string FileHeader(DateTime utc) => $"PHD2 version 2.6.13 (NOVA guider), Log version 2.5. Log enabled at {Local(utc)}";

    private string? _lastProblem;

    private void Write(IReadOnlyList<string> lines)
    {
        var path = file();
        if (path is null) return;
        try
        {
            var fresh = !File.Exists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = (fresh ? FileHeader(DateTime.UtcNow) + Environment.NewLine : "") + string.Join(Environment.NewLine, lines) + Environment.NewLine;
            File.AppendAllText(path, text);
            _lastProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_lastProblem == ex.Message) return;
            _lastProblem = ex.Message;
            Problem?.Invoke($"Can't write the guide log {path}: {ex.Message}");
        }
    }

    private string Seconds(DateTime utc) => (utc - _start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    private static string Ms(GuidePulse? p) => p is null ? "0" : p.Milliseconds.ToString(CultureInfo.InvariantCulture);
    private static string Letter(GuidePulse? p) => p?.Direction switch
    {
        GuideDirection.East => "E",
        GuideDirection.West => "W",
        GuideDirection.North => "N",
        GuideDirection.South => "S",
        _ => "",
    };
    private static double Deg(double radians) => ((radians * 180 / Math.PI) % 360 + 540) % 360 - 180;
    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The observing night a moment belongs to (local noon to noon), as NOVA names its image folders.</summary>
    public static string Night(DateTime utc) => utc.ToLocalTime().AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
