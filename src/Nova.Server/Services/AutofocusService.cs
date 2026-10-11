using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;
using Nova.Core.Focusing;

namespace Nova.Server.Services;

/// <param name="StepSize">Focuser steps between measurements. Wide enough that the outer points are clearly blurred.</param>
/// <param name="StepsPerSide">Measurements either side of the start position (2 × this + 1 in all).</param>
/// <param name="ExposureSeconds">Per measurement; long enough for plenty of stars.</param>
/// <param name="Backlash">
/// Overshoot for outward moves: the focuser goes this far past the target and comes back in, so every
/// position is reached moving inward and gear slack doesn't shift the curve. 0 = off.
/// </param>
public sealed record AutofocusSettings(int StepSize = 100, int StepsPerSide = 4, double ExposureSeconds = 3, int Backlash = 0)
{
    public string? Validate() =>
        StepSize < 1 ? "Step size must be at least 1"
        : StepsPerSide is < 2 or > 10 ? "Steps per side must be between 2 and 10"
        : ExposureSeconds is < 0 or > 120 ? "Exposure must be between 0 and 120 seconds"
        : Backlash is < 0 or > 100_000 ? "Backlash must be between 0 and 100000"
        : null;
}

/// <param name="Fit">The fitted curve, once there are enough points; drawn by the UI.</param>
/// <param name="StartHfr">Measured at the start position during the sweep: the result has to beat it.</param>
/// <param name="FinalHfr">Measured on a frame taken at the chosen position.</param>
public sealed record AutofocusStatus(
    bool Running, string Message, bool? Succeeded, int StartPosition, IReadOnlyList<FocusPoint> Points,
    FocusFit? Fit, int? BestPosition, double? FinalHfr, double? Temperature, string? Filter, DateTime StartedUtc,
    double? StartHfr = null, int Pipelined = 0);

/// <summary>
/// V-curve autofocus: steps the focuser through positions around the current one, measures the median star
/// HFR at each, fits a hyperbola and moves to its minimum. If the best point is at the edge of the sweep,
/// it keeps going in that direction (up to another sweep's worth) before fitting. A frame at the chosen
/// position must not be worse than the start (as NINA checks too). If the run fails — no usable curve (e.g. focus
/// is outside the range) or a result worse than the start — the focuser goes to the sharpest position it measured:
/// never worse than the start, and usually closer to focus. On cancel or an error it returns to the start.
/// </summary>
public sealed class AutofocusService
{
    private const double MinRSquared = 0.7;
    private const int MinStarsPerPoint = 3;
    /// <summary>Pause after the camera says the exposure ended before moving, in case a driver says so a touch early.</summary>
    private static readonly TimeSpan MoveMargin = TimeSpan.FromMilliseconds(100);
    /// <summary>Seeing alone moves HFR a few percent frame to frame; only worse than this counts.</summary>
    private const double WorseTolerance = 0.05;

    /// <summary>
    /// Whether the final frame is acceptable against the starting HFR. No stars in the final frame is a
    /// failure; no stars at the start means there's nothing to compare against, so anything measured wins.
    /// </summary>
    public static bool IsImprovement(double? startHfr, double? finalHfr) =>
        finalHfr is { } final && (startHfr is not { } start || final <= start * (1 + WorseTolerance));

    private readonly EquipmentService _equipment;
    private readonly ImagingService _imaging;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private CancellationTokenSource? _run;

    public AutofocusService(EquipmentService equipment, ImagingService imaging, ActivityLog log, IOptions<NovaOptions> options)
    {
        _equipment = equipment;
        _imaging = imaging;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "autofocus.json");
        Settings = Load();
    }

    public AutofocusSettings Settings { get; private set; }
    public AutofocusStatus? Status { get; private set; }
    public bool Running
    {
        get { lock (_sync) return _run is not null; }
    }

    public string? UpdateSettings(AutofocusSettings settings)
    {
        if (settings.Validate() is { } problem) return problem;
        Settings = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
        return null;
    }

    public bool Start(out string? error)
    {
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (_equipment.Focuser is not { IsConnected: true } focuser) { error = "No focuser connected"; return false; }
        if (!focuser.CanMoveAbsolute) { error = "Autofocus needs a focuser that reports its position"; return false; }
        if (_imaging.IsBusy) { error = "An exposure is running"; return false; }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_run is not null) { error = "Autofocus is already running"; return false; }
            _run = cts = new CancellationTokenSource();
        }
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(focuser, Settings, cts.Token);
            }
            finally
            {
                lock (_sync) _run = null;
                cts.Dispose();
            }
        });
        return true;
    }

    public void Cancel()
    {
        lock (_sync) _run?.Cancel();
    }

    /// <summary>Runs autofocus and waits for it (for automation and tests). Returns the final status.</summary>
    public async Task<AutofocusStatus> RunAsync(CancellationToken ct)
    {
        if (!Start(out var error)) throw new InvalidOperationException(error);
        await using var _ = ct.Register(Cancel);
        while (Running) await Task.Delay(50, CancellationToken.None);
        return Status!;
    }

    private async Task RunAsync(IFocuser focuser, AutofocusSettings settings, CancellationToken ct)
    {
        var start = focuser.Position;
        var filter = _equipment.FilterWheel is { IsConnected: true } wheel && wheel.Position >= 1 && wheel.Position <= wheel.Names.Count
            ? wheel.Names[wheel.Position - 1]
            : null;
        var points = new List<FocusPoint>(); // filled by background measurements: lock it
        var measuring = new List<Task>();
        var pipelined = 0;
        Task? moving = null;                  // a move started while the last frame was downloading
        Status = new AutofocusStatus(true, "Starting", null, start, [], null, null, null, focuser.Temperature, filter, DateTime.UtcNow);
        _log.Info($"Autofocus from {start}: {2 * settings.StepsPerSide + 1} points {settings.StepSize} steps apart, " +
                  $"{settings.ExposureSeconds:0.##}s each{(filter is null ? "" : $", filter {filter}")}");

        void Report(string message)
        {
            List<FocusPoint> snapshot;
            lock (points) snapshot = points.OrderBy(p => p.Position).ToList();
            Status = Status! with { Message = message, Points = snapshot, Fit = FitOrNull(snapshot), Pipelined = pipelined };
        }

        // Exposes at a position the focuser is already at. With a next position, the focuser heads there as soon
        // as the exposure ends — during the download — and the stars are measured in the background meanwhile.
        async Task ExposeAtAsync(int position, int? next)
        {
            Report($"Exposing at {position}");
            Action? onEnd = next is { } target
                ? () =>
                {
                    moving = Task.Run(async () =>
                    {
                        await Task.Delay(MoveMargin, ct);
                        await MoveAsync(focuser, target, settings.Backlash, ct);
                    }, ct);
                    Interlocked.Increment(ref pipelined);
                }
                : null;
            var frame = await _imaging.ExposeForMeasurementAsync(settings.ExposureSeconds, ct, onEnd);
            measuring.Add(frame.Stars.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { MedianHfr: { } hfr, Count: >= MinStarsPerPoint } stars)
                {
                    lock (points) points.Add(new FocusPoint(position, hfr, stars.Count));
                }
                else
                {
                    _log.Warn(t.IsFaulted
                        ? $"Autofocus: couldn't measure the frame at {position} ({t.Exception?.InnerException?.Message}), skipping that point"
                        : $"Autofocus: too few stars at {position}, skipping that point");
                }
                Report($"Measured {position}");
            }, TaskScheduler.Default));
        }

        // One point at a time, waiting for its measurement: for points that depend on the results so far.
        async Task MeasureAsync(int position)
        {
            Report($"Moving to {position}");
            await MoveAsync(focuser, position, settings.Backlash, ct);
            await ExposeAtAsync(position, next: null);
            await Task.WhenAll(measuring);
        }

        try
        {
            // The sweep, from outside in (so moves between points are all inward). Its positions are known in
            // advance, so it's pipelined: frame N downloads and is measured while the focuser moves to N+1.
            var sweep = Enumerable.Range(0, 2 * settings.StepsPerSide + 1)
                .Select(i => start + (settings.StepsPerSide - i) * settings.StepSize)
                .Where(position => position >= focuser.MinPosition && position <= focuser.MaxPosition)
                .ToList();
            for (var k = 0; k < sweep.Count; k++)
            {
                if (k == 0)
                {
                    Report($"Moving to {sweep[0]}");
                    await MoveAsync(focuser, sweep[0], settings.Backlash, ct);
                }
                else if (moving is { } move)
                {
                    Report($"Moving to {sweep[k]}");
                    await move; // never expose while the focuser moves
                    moving = null;
                }
                else
                {
                    await MoveAsync(focuser, sweep[k], settings.Backlash, ct);
                }
                await ExposeAtAsync(sweep[k], k + 1 < sweep.Count ? sweep[k + 1] : null);
            }
            Report("Measuring the last frames");
            await Task.WhenAll(measuring);

            // Focus near an edge of the sweep: keep measuring that way until both arms of the V have a few
            // points, otherwise the fit has little to go on for that side.
            var perArm = Math.Max(3, settings.StepsPerSide / 2 + 1);
            for (var extra = 0; extra < settings.StepsPerSide && points.Count > 0; extra++)
            {
                var best = points.MinBy(p => p.Hfr)!.Position;
                int next;
                if (points.Count(p => p.Position > best) < perArm) next = points.Max(p => p.Position) + settings.StepSize;
                else if (points.Count(p => p.Position < best) < perArm) next = points.Min(p => p.Position) - settings.StepSize;
                else break;
                if (next < focuser.MinPosition || next > focuser.MaxPosition) break;
                _log.Info($"Autofocus: best so far ({best}) is near the edge; extending to {next}");
                await MeasureAsync(next);
            }

            var fit = FocusCurve.Fit(points);
            var low = points.Count > 0 ? points.Min(p => p.Position) - settings.StepSize : 0;
            var high = points.Count > 0 ? points.Max(p => p.Position) + settings.StepSize : 0;
            var problem =
                points.Count < 5 ? $"Only {points.Count} usable points: not enough stars? Try a longer exposure"
                : fit is null ? "The measurements don't form a V curve: try a bigger step size"
                : fit.RSquared < MinRSquared ? $"Poor curve fit (R² {fit.RSquared:0.00}): seeing or clouds? Try a longer exposure"
                : fit.BestPosition < low || fit.BestPosition > high ? $"Best focus ({fit.BestPosition}) is outside the measured range: move closer to focus and run again"
                : fit.BestPosition < focuser.MinPosition || fit.BestPosition > focuser.MaxPosition ? "Best focus is beyond the focuser's travel"
                : null;
            if (problem is not null)
            {
                // Still help: go to the sharpest position measured (the start is one of the points, so this is
                // never worse), which also gives the next run a better place to start from.
                if (points.MinBy(p => p.Hfr) is { } sharpest && sharpest.Position != start)
                {
                    await MoveAsync(focuser, sharpest.Position, settings.Backlash, CancellationToken.None);
                    Status = Status with
                    {
                        Running = false, Succeeded = false, BestPosition = sharpest.Position, FinalHfr = sharpest.Hfr,
                        StartHfr = points.FirstOrDefault(p => p.Position == start)?.Hfr,
                        Message = $"{problem}. Moved to the sharpest position measured, {sharpest.Position} (HFR {sharpest.Hfr:0.00} px)",
                    };
                    _log.Warn($"Autofocus failed: {problem}. Focuser moved to the sharpest measured position, {sharpest.Position} " +
                              $"(HFR {sharpest.Hfr:0.00} px)");
                    return;
                }
                await ReturnToStartAsync(focuser, start, settings.Backlash);
                Status = Status with { Running = false, Succeeded = false, Message = problem };
                _log.Warn($"Autofocus failed: {problem}. Focuser back at {start}");
                return;
            }

            Report($"Moving to best focus {fit!.BestPosition}");
            await MoveAsync(focuser, fit.BestPosition, settings.Backlash, ct);
            Report("Checking");
            var check = await _imaging.CaptureAsync(settings.ExposureSeconds, save: false, ct, keep: false);
            var final = check.Stars.MedianHfr;
            var startHfr = points.FirstOrDefault(p => p.Position == start)?.Hfr;
            Status = Status with { StartHfr = startHfr };
            if (!IsImprovement(startHfr, final))
            {
                var why = final is { } f1
                    ? $"HFR {f1:0.00} px at {fit.BestPosition} is worse than {startHfr:0.00} px at the start"
                    : $"no stars measured at {fit.BestPosition}";
                // Like any failed run: end at the sharpest position measured (the start, if nothing beat it).
                var sharpest = points.MinBy(p => p.Hfr)!;
                await MoveAsync(focuser, sharpest.Position, settings.Backlash, CancellationToken.None);
                var where = sharpest.Position == start
                    ? $"focuser back at {start}"
                    : $"moved to the sharpest position measured, {sharpest.Position} (HFR {sharpest.Hfr:0.00} px)";
                Status = Status with
                {
                    Running = false, Succeeded = false, BestPosition = sharpest.Position, FinalHfr = sharpest.Hfr,
                    Message = $"Not better than before ({why}): {where}",
                };
                _log.Warn($"Autofocus result rejected: {why}; {where}");
                return;
            }
            Status = Status with
            {
                Running = false, Succeeded = true, BestPosition = fit.BestPosition, FinalHfr = final,
                Message = $"Focused at {fit.BestPosition}" + (final is { } f ? $", HFR {f:0.00} px" : ""),
            };
            _log.Info($"Autofocus done: {fit.BestPosition} (was {start}), HFR {final:0.00} px (was {startHfr:0.00}) " +
                      $"(curve minimum {fit.BestHfr:0.00}, R² {fit.RSquared:0.000})");
        }
        catch (OperationCanceledException)
        {
            await SettleAsync(moving);
            await ReturnToStartAsync(focuser, start, settings.Backlash);
            Status = Status! with { Running = false, Succeeded = false, Message = "Cancelled" };
            _log.Warn($"Autofocus cancelled. Focuser back at {start}");
        }
        catch (Exception ex)
        {
            await SettleAsync(moving);
            await ReturnToStartAsync(focuser, start, settings.Backlash);
            Status = Status! with { Running = false, Succeeded = false, Message = ex.Message };
            _log.Error("Autofocus failed", ex);
        }
    }

    /// <summary>Waits for a move started early to end (it may have been cancelled), so moves never overlap.</summary>
    private static async Task SettleAsync(Task? move)
    {
        if (move is null) return;
        try
        {
            await move;
        }
        catch
        {
            // cancelled or failed: either way it has stopped
        }
    }

    private static FocusFit? FitOrNull(List<FocusPoint> points) => points.Count >= 3 ? FocusCurve.Fit(points) : null;

    /// <summary>Moves so the last motion is inward: outward moves overshoot by the backlash and come back.</summary>
    private static async Task MoveAsync(IFocuser focuser, int position, int backlash, CancellationToken ct)
    {
        if (backlash > 0 && position > focuser.Position)
            await focuser.MoveToAsync(Math.Min(position + backlash, focuser.MaxPosition), ct);
        await focuser.MoveToAsync(position, ct);
    }

    private async Task ReturnToStartAsync(IFocuser focuser, int start, int backlash)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await MoveAsync(focuser, start, backlash, timeout.Token);
        }
        catch (Exception ex)
        {
            _log.Warn($"Couldn't return the focuser to {start}: {ex.Message}");
        }
    }

    private AutofocusSettings Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<AutofocusSettings>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new AutofocusSettings();
    }
}
