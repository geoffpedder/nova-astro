using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Simulation;

namespace Nova.Server.Services;

/// <param name="Method">"ThreePoint": three solves anywhere in the sky, turning RA between them (N.I.N.A.'s TPPA).
/// "Pole": the camera on the pole with Polaris in view, two solves turning RA about 90° between them (as SharpCap does).</param>
/// <param name="StepDegrees">Three-point: how far RA turns between the points.</param>
/// <param name="Direction">"East" or "West": which way RA turns.</param>
/// <param name="Refraction">Aim at the refracted pole (where it appears) instead of the true pole.</param>
/// <param name="Manual">For mounts without GoTo or a drivable RA axis: you turn RA by hand.</param>
/// <param name="StartAutomatically">GoTo the start first (the mount roughly polar aligned, e.g. at home); false starts
/// wherever the scope points.</param>
/// <param name="AltitudeOffset">Three-point start: degrees above the pole (as N.I.N.A.'s default, just above it).</param>
/// <param name="AzimuthOffset">Three-point start: degrees east of the pole.</param>
/// <param name="PoleTurnDegrees">Pole method: how far RA turns between the two frames (60–90° is best).</param>
public sealed record PolarAlignmentSettings(
    string Method = "ThreePoint", double StepDegrees = 10, string Direction = "East", double ExposureSeconds = 3,
    bool Refraction = false, bool Manual = false, bool StartAutomatically = true,
    double AltitudeOffset = 2, double AzimuthOffset = 1, double PoleTurnDegrees = 90)
{
    /// <summary>The automatic start applies to GoTo mounts (not when RA is turned by hand).</summary>
    public bool AutoStart => StartAutomatically && !Manual;

    public bool Pole => Method == "Pole";

    public string? Validate() =>
        Method is not ("ThreePoint" or "Pole") ? "Method must be ThreePoint or Pole"
        : StepDegrees is < 2 or > 60 ? "Step must be between 2 and 60 degrees"
        : Direction is not ("West" or "East") ? "Direction must be East or West"
        : ExposureSeconds is < 0 or > 60 ? "Exposure must be between 0 and 60 seconds"
        : AltitudeOffset is < -30 or > 30 ? "Altitude offset must be between −30 and 30 degrees"
        : AzimuthOffset is < -90 or > 90 ? "Azimuth offset must be between −90 and 90 degrees"
        : PoleTurnDegrees is < 30 or > 120 ? "The turn must be between 30 and 120 degrees"
        : null;
}

public sealed record PolarErrorPoint(DateTime Utc, double AltitudeArcmin, double AzimuthArcmin, double TotalArcmin);

/// <summary>What to draw on the latest frame (pole method): pixel positions in that image, which may lie outside it.</summary>
public sealed record PolarOverlay(
    int ImageId, int Width, int Height, double ArcsecPerPixel, double[]? Axis, double[]? Pole, double[]? Polaris, double[]? UpFromAxis);

/// <param name="Phase">Measuring, Adjusting (live, turn the knobs), Done, Failed or Cancelled.</param>
/// <param name="Point">Which point (1–3, or 1–2 on the pole) while measuring.</param>
/// <param name="Points">How many points this method measures.</param>
/// <param name="WaitingForRotation">Manual mode: turn RA, then Continue.</param>
/// <param name="NorthernHemisphere">Which pole: decides which way "left" is on the compass.</param>
public sealed record PolarAlignmentStatus(
    bool Running, string Phase, string Message, int Point, bool WaitingForRotation, bool NorthernHemisphere,
    PolarError? Error, PolarError? InitialError, IReadOnlyList<PolarErrorPoint> History, int Frames, DateTime StartedUtc,
    string Method = "ThreePoint", int Points = 3, PolarOverlay? Overlay = null, int? ImageId = null);

/// <param name="Knobs">The simulator's true polar error and knobs, when the mount is simulated.</param>
public sealed record PolarAlignmentInfo(
    bool Running, PolarAlignmentSettings Settings, PolarAlignmentStatus? Status, SimulatorKnobs? Knobs);

public sealed record SimulatorKnobs(double AltitudeErrorArcmin, double AzimuthErrorArcmin);

/// <summary>
/// Polar alignment, two ways. Three-point (as N.I.N.A.'s TPPA plugin): solve, turn RA, solve, turn RA, solve,
/// anywhere above the horizon (by default starting just above the pole, as N.I.N.A. does). On the pole (as SharpCap):
/// solve with Polaris in view, turn RA about 90°, solve, which finds the pixel the mount turns about. Either way, it
/// then keeps exposing and solving while the user turns the altitude and azimuth knobs, updating the remaining error
/// each frame, until stopped.
/// </summary>
public sealed class PolarAlignmentService
{
    private const int HistoryLength = 200;
    private const double MinimumAltitude = 10;
    private const int SettingsVersion = 2;

    private readonly EquipmentService _equipment;
    private readonly ImagingService _imaging;
    private readonly PlateSolveService _solver;
    private readonly SiteService _sites;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private CancellationTokenSource? _run;
    private TaskCompletionSource? _continue;

    public PolarAlignmentService(EquipmentService equipment, ImagingService imaging, PlateSolveService solver,
        SiteService sites, ActivityLog log, IOptions<NovaOptions> options)
    {
        _equipment = equipment;
        _imaging = imaging;
        _solver = solver;
        _sites = sites;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "polar-alignment.json");
        Settings = Load();
    }

    public PolarAlignmentSettings Settings { get; private set; }
    public PolarAlignmentStatus? Status { get; private set; }
    public bool Running
    {
        get { lock (_sync) return _run is not null; }
    }

    public PolarAlignmentInfo Info => new(Running, Settings, Status,
        _equipment.Mount is IPolarAlignmentKnobs knobs ? new SimulatorKnobs(knobs.AltitudeErrorArcmin, knobs.AzimuthErrorArcmin) : null);

    public string? UpdateSettings(PolarAlignmentSettings settings)
    {
        if (settings.Validate() is { } problem) return problem;
        Settings = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var json = JsonSerializer.SerializeToNode(settings)!.AsObject();
            json["Version"] = SettingsVersion;
            File.WriteAllText(_file, json.ToJsonString());
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
        return null;
    }

    public bool Start(out string? error)
    {
        var settings = Settings;
        if (_equipment.Mount is not { IsConnected: true } mount) { error = "No mount connected"; return false; }
        var auto = settings.AutoStart;
        if (mount.State == MountState.Parked && !auto) { error = "Unpark the mount first"; return false; }
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (!_solver.Solver.Available) { error = _solver.Solver.Problem; return false; }
        if (Site() is not { } site) { error = "Set your site (latitude and longitude) first: polar alignment depends on it"; return false; }
        if (!settings.Pole && !auto && !settings.Manual && !mount.CanMoveRaAxis && CrossesMeridian(mount.Coordinates, settings, site) is { } crossing)
        {
            error = crossing;
            return false;
        }
        if (settings.Pole && !settings.Manual && !mount.CanMoveRaAxis)
        {
            error = "Turning RA 90° on the pole needs a mount whose RA axis can be driven (a GoTo there would flip it): turn it by hand instead (tick By hand)";
            return false;
        }
        if (_imaging.IsBusy) { error = "An exposure is running"; return false; }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_run is not null) { error = "Polar alignment is already running"; return false; }
            _run = cts = new CancellationTokenSource();
        }
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(mount, settings, cts.Token);
            }
            finally
            {
                lock (_sync) _run = null;
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>Manual mode: RA has been turned, take the next point.</summary>
    public bool Continue() => _continue?.TrySetResult() ?? false;

    /// <summary>Ends the run: while adjusting this is the normal way to finish.</summary>
    public void Stop()
    {
        lock (_sync) _run?.Cancel();
    }

    /// <summary>Simulator only: turn the simulated mount's knobs.</summary>
    public string? TurnSimulatorKnobs(double altitudeArcmin, double azimuthArcmin)
    {
        if (_equipment.Mount is not IPolarAlignmentKnobs knobs) return "Only the simulated mount has virtual knobs";
        knobs.Turn(altitudeArcmin, azimuthArcmin);
        return null;
    }

    /// <summary>The site the user chose first: a driver may show a wrong (or 0°, 0°) location until it's sent again.</summary>
    private GeoLocation? Site() =>
        _sites.ActiveSite?.Location
        ?? (_equipment.Mount?.Location is { } m && (m.Latitude != 0 || m.Longitude != 0) ? m : null);

    /// <param name="MountRaHours">Where the mount said it pointed in RA (JNow) when the frame was taken.</param>
    private sealed record Shot(PolarSample Sample, PoleFrame? Frame, int ImageId, double? MountRaHours);

    private async Task RunAsync(IMount mount, PolarAlignmentSettings settings, CancellationToken ct)
    {
        await _sites.EnsureMountLocationAsync(ct);
        var site = Site()!;
        var points = settings.Pole ? 2 : 3;
        var history = new List<PolarErrorPoint>();
        var frames = 0;
        Status = new PolarAlignmentStatus(true, "Measuring", "Starting", 1, false, site.Latitude >= 0,
            null, null, [], 0, DateTime.UtcNow, settings.Method, points);
        _log.Info(settings.Pole
            ? $"Polar alignment on the pole: 2 frames, turning RA {settings.PoleTurnDegrees:0}° {settings.Direction.ToLowerInvariant()}" + (settings.Manual ? " by hand" : "")
            : $"Polar alignment: 3 points {settings.StepDegrees:0.#}° apart, turning {settings.Direction.ToLowerInvariant()}" + (settings.Manual ? " by hand" : ""));

        void Report(string message, int? point = null, bool waiting = false) =>
            Status = Status! with { Message = message, Point = point ?? Status.Point, WaitingForRotation = waiting, Frames = frames };

        async Task<Shot?> ShootAsync(string what, int attempts = 1)
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                Report(attempt == 1 ? $"{what}: exposing" : $"{what}: solve failed, trying again ({attempt}/{attempts}) with a wider search");
                var image = await _imaging.CaptureAsync(settings.ExposureSeconds, save: false, ct, keep: false);
                Status = Status! with { ImageId = image.Id }; // the tab shows this frame, not an image opened meanwhile
                Report($"{what}: solving");
                var solve = await _solver.SolveLatestAsync(ct, searchRadius: attempt == 1 ? null : attempt == 2 ? 60 : 180);
                frames++;
                if (!solve.Solved) continue;
                var middle = image.CapturedUtc.ToUniversalTime().AddSeconds(image.DurationSeconds / 2);
                var wcs = _solver.WcsOf(image.Id);
                return new Shot(new PolarSample(new EquatorialCoordinates(solve.RaHours!.Value, solve.DecDegrees!.Value), middle),
                    wcs is null ? null : new PoleFrame(wcs, image.Width, image.Height, middle), image.Id,
                    mount.IsConnected ? mount.Coordinates.RaHours : null);
            }
            return null;
        }

        void Record(PolarError error, bool initial = false, PolarOverlay? overlay = null)
        {
            history.Add(new PolarErrorPoint(DateTime.UtcNow, error.AltitudeArcmin, error.AzimuthArcmin, error.TotalArcmin));
            if (history.Count > HistoryLength) history.RemoveAt(0);
            Status = Status! with
            {
                Phase = "Adjusting", Error = error, InitialError = initial ? error : Status.InitialError,
                History = history.ToList(), Overlay = overlay ?? Status.Overlay,
            };
        }

        try
        {
            if (settings.AutoStart)
            {
                if (mount.State == MountState.Parked)
                {
                    Report("Unparking", 1);
                    await mount.UnparkAsync(ct);
                }
                var start = settings.Pole ? PoleStart(DateTime.UtcNow, site) : StartPosition(site, settings, DateTime.UtcNow);
                var (altitude, azimuth) = Horizon.AltAz(Horizon.ToVector(start, DateTime.UtcNow, site));
                if (altitude < MinimumAltitude)
                    throw new InvalidOperationException($"The start position would be only {altitude:0}° up: change the start offsets");
                _sites.EnsureAwayFromSun(Precession.JNowToJ2000(start, DateTime.UtcNow), "The polar alignment start position");
                Report(settings.Pole ? "Slewing to the pole" : $"Slewing to the start, {altitude:0}° up in the {Compass(azimuth)}", 1);
                _log.Info($"Polar alignment: starting at {start} (JNow), {altitude:0}° up in the {Compass(azimuth)}");
                await _sites.SlewSafelyAsync(mount, start, ct); // and settles
            }

            var useAxis = mount.CanMoveRaAxis;
            var shots = new List<Shot>();
            for (var point = 1; point <= points; point++)
            {
                if (point > 1)
                {
                    var degrees = settings.Pole ? settings.PoleTurnDegrees : settings.StepDegrees;
                    if (settings.Manual) await WaitForManualTurnAsync(mount, settings, degrees, point, Report, ct);
                    else useAxis = await TurnRaAsync(mount, settings, degrees, point, useAxis, site, Report, ct);
                }

                var shot = await ShootAsync($"Point {point} of {points}", attempts: 3);
                if (shot is null) throw new InvalidOperationException($"Couldn't plate solve point {point}: {_solver.Last?.Error}");
                shots.Add(shot);
                Report($"Point {point} solved", point);
            }

            if (settings.Pole)
                await AdjustOnThePoleAsync(shots[0], shots[1], settings, site, ShootAsync, Report, Record, ct);
            else
                await AdjustThreePointAsync(shots, settings, site, ShootAsync, Report, Record, ct);
        }
        catch (OperationCanceledException)
        {
            var finished = Status!.Phase == "Adjusting";
            Status = Status with
            {
                Running = false, WaitingForRotation = false, Phase = finished ? "Done" : "Cancelled",
                Message = finished && Status.Error is { } e ? $"Finished with {Dms(e.TotalArcmin)} error" : "Cancelled",
            };
            if (finished && Status.Error is { } done)
                _log.Info($"Polar alignment finished: {Dms(done.TotalArcmin)} (altitude {Dms(done.AltitudeArcmin, signed: true)}, " +
                          $"azimuth {Dms(done.AzimuthArcmin, signed: true)}). The mount moved, so sync or plate solve before going to a target.");
            else
                _log.Warn("Polar alignment cancelled");
        }
        catch (Exception ex)
        {
            Status = Status! with { Running = false, WaitingForRotation = false, Phase = "Failed", Message = ex.Message };
            _log.Warn($"Polar alignment failed: {ex.Message}");
        }
        finally
        {
            _continue = null;
        }
    }

    private async Task AdjustThreePointAsync(List<Shot> shots, PolarAlignmentSettings settings, GeoLocation site,
        Func<string, int, Task<Shot?>> shoot, Action<string, int?, bool> report, Action<PolarError, bool, PolarOverlay?> record, CancellationToken ct)
    {
        var alignment = new PolarAlignment(site, settings.Refraction);
        var initial = alignment.Measure(shots[0].Sample, shots[1].Sample, shots[2].Sample);
        record(initial, true, null);
        _log.Info($"Polar error {Dms(initial.TotalArcmin)}: altitude {Describe(initial, site).Altitude}, azimuth {Describe(initial, site).Azimuth}");
        report("Turn the altitude and azimuth knobs; the error updates with every frame. Press Done when it's small enough.", null, false);

        while (true)
        {
            var shot = await shoot("Adjusting", 1);
            if (shot is null)
            {
                report($"Couldn't solve that frame ({_solver.Last?.Error}); trying again", null, false);
                continue;
            }
            var error = alignment.Update(shot.Sample);
            record(error, false, null);
            report(error.TotalArcmin < 1 ? "Within 1′: well aligned. Press Done when you're happy." : "Keep adjusting", null, false);
        }
    }

    private async Task AdjustOnThePoleAsync(Shot first, Shot second, PolarAlignmentSettings settings, GeoLocation site,
        Func<string, int, Task<Shot?>> shoot, Action<string, int?, bool> report, Action<PolarError, bool, PolarOverlay?> record, CancellationToken ct)
    {
        if (first.Frame is null || second.Frame is null)
            throw new InvalidOperationException("The plate solver didn't give a full solution for the frames: the pole method needs one");
        var alignment = new PoleAlignment(site, settings.Refraction);
        PolarError initial;
        try
        {
            initial = alignment.Measure(first.Frame, second.Frame);
        }
        catch (ArgumentException) when (first.MountRaHours is { } a && second.MountRaHours is { } b
                                        && Math.Abs(((b - a) % 24 + 36) % 24 - 12) * 15 is var turned && turned > 20
                                        && alignment.TurnDegrees < turned / 4)
        {
            // The mount turned, but the view didn't turn with it.
            throw new InvalidOperationException(
                $"The mount turned RA {Math.Abs(((b - a) % 24 + 36) % 24 - 12) * 15:0}°, but the stars in the frame only turned {alignment.TurnDegrees:0.#}°: " +
                "the camera's view isn't turning with the mount. INDI's CCD Simulator can't simulate this method (it keeps its image " +
                "square to the sky; use three-point with it). On real equipment, check the camera is fixed to the telescope.");
        }
        _log.Info($"Polar error {Dms(initial.TotalArcmin)} (RA turned {alignment.TurnDegrees:0}°): altitude {Describe(initial, site).Altitude}, azimuth {Describe(initial, site).Azimuth}");
        record(initial, true, Overlay(second, alignment.Marks));
        report("Turn the altitude and azimuth knobs to bring the pole onto the axis mark; the error updates with every frame. Press Done when it's small enough.", null, false);

        while (true)
        {
            var shot = await shoot("Adjusting", 1);
            if (shot?.Frame is not { } frame)
            {
                report($"Couldn't solve that frame ({_solver.Last?.Error}); trying again", null, false);
                continue;
            }
            var error = alignment.Update(frame);
            record(error, false, Overlay(shot, alignment.Marks));
            report(error.TotalArcmin < 1 ? "Within 1′: well aligned. Press Done when you're happy." : "Keep adjusting", null, false);
        }
    }

    private PolarOverlay? Overlay(Shot shot, PoleMarks? marks)
    {
        if (shot.Frame is not { } frame || marks is null) return null;
        static double[]? P((double X, double Y)? p) => p is { } v ? [Math.Round(v.X, 1), Math.Round(v.Y, 1)] : null;
        var scale = (_solver.SolveOf(shot.ImageId)?.ImageScale) ?? 1;
        return new PolarOverlay(shot.ImageId, frame.Width, frame.Height, scale, P(marks.Axis), P(marks.Pole), P(marks.Polaris), P(marks.UpFromAxis));
    }

    /// <summary>Turns RA between points: driving the axis where the mount can (no flip), else a GoTo.</summary>
    private async Task<bool> TurnRaAsync(IMount mount, PolarAlignmentSettings settings, double degrees, int point, bool useAxis,
        GeoLocation site, Action<string, int?, bool> report, CancellationToken ct)
    {
        var west = settings.Direction == "West";
        var current = mount.Coordinates;
        var target = current with { RaHours = (current.RaHours + degrees / 15 * (west ? -1 : 1) + 24) % 24 };
        var altitude = Horizon.AltAz(Horizon.ToVector(target, DateTime.UtcNow, site)).Altitude;
        if (altitude < MinimumAltitude)
            throw new InvalidOperationException(
                $"Turning {settings.Direction.ToLowerInvariant()} would point {altitude:0}° above the horizon: try the other direction, or start higher");
        _sites.EnsureAwayFromSun(Precession.JNowToJ2000(target, DateTime.UtcNow), "The next polar alignment point");
        report($"Turning RA {degrees:0}° {settings.Direction.ToLowerInvariant()}", point, false);

        if (useAxis)
        {
            try
            {
                // Drive the RA axis (as N.I.N.A.'s plugin does) rather than GoTo: no flip and no pointing model, so it
                // works anywhere, across the meridian and on the pole too.
                if (!_sites.PathIsClear(mount, target)) throw new InvalidOperationException("the turn would pass close to the Sun");
                await mount.MoveRaAxisAsync(degrees * (west ? 1 : -1), ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct); // let the mount settle
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (settings.Pole) throw new InvalidOperationException($"Couldn't drive the RA axis ({ex.Message}): turn it by hand instead (tick By hand)");
                _log.Warn($"Driving RA didn't work ({ex.Message}); turning with GoTo instead");
            }
        }
        var side = mount.SideOfPier;
        await _sites.SlewSafelyAsync(mount, target, ct);
        if (side is not null && mount.SideOfPier != side)
            throw new InvalidOperationException(
                "The mount flipped to the other side of the pier while turning RA, which spoils the measurement. " +
                "Turn the other way, or start further from the meridian.");
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        return false;
    }

    /// <summary>
    /// Three-point start (JNow), as N.I.N.A. does it: the pole's altitude plus <see cref="PolarAlignmentSettings.AltitudeOffset"/>,
    /// <see cref="PolarAlignmentSettings.AzimuthOffset"/> east of it. Close to the pole the field never goes near the
    /// zenith or due east/west (where the knob readings blur), and turning RA there barely moves the scope.
    /// </summary>
    public static EquatorialCoordinates StartPosition(GeoLocation site, PolarAlignmentSettings settings, DateTime utc)
    {
        var north = site.Latitude >= 0;
        var altitude = Math.Clamp(Math.Abs(site.Latitude) + settings.AltitudeOffset, 5, 85);
        var azimuth = ((north ? 0 : 180) + settings.AzimuthOffset + 360) % 360;
        return Horizon.FromVector(Horizon.FromAltAz(altitude, azimuth), utc, site);
    }

    /// <summary>
    /// On the pole (JNow): a hair from it towards Polaris (a GoTo to exactly 90° has no RA), so the pole, and the axis
    /// near it, are in the middle of the frame, with Polaris 0.66° away (in view with most fields).
    /// </summary>
    public static EquatorialCoordinates PoleStart(DateTime utc, GeoLocation site)
    {
        if (site.Latitude < 0) return new EquatorialCoordinates(Horizon.LocalSiderealDegrees(utc, site.Longitude) / 15, -89.9);
        var polaris = Precession.J2000ToJNow(PoleAlignment.Polaris, utc);
        return polaris with { DecDegrees = 89.9 };
    }

    private static string Compass(double azimuth) =>
        new[] { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" }[(int)Math.Round(azimuth / 45) % 8];

    /// <summary>
    /// Manual mode: wait while the user turns RA. With a mount connection, show how far it has turned and carry
    /// on by itself once it's turned far enough and stopped (as N.I.N.A. does); Continue also works at any time.
    /// </summary>
    private async Task WaitForManualTurnAsync(IMount mount, PolarAlignmentSettings settings, double degrees, int point,
        Action<string, int?, bool> report, CancellationToken ct)
    {
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _continue = resume;
        var start = mount.IsConnected ? mount.Coordinates.RaHours : (double?)null;
        double? lastRa = null;
        var stillSince = DateTime.UtcNow;
        try
        {
            while (!resume.Task.IsCompleted)
            {
                var text = $"Turn RA {settings.Direction.ToLowerInvariant()} by about {degrees:0}° (RA only), then press Continue";
                if (start is { } s && mount.IsConnected)
                {
                    var ra = mount.Coordinates.RaHours;
                    var turned = Math.Abs(((ra - s) % 24 + 36) % 24 - 12) * 15;
                    if (lastRa is null || Math.Abs(ra - lastRa.Value) * 15 > 0.02) stillSince = DateTime.UtcNow;
                    lastRa = ra;
                    if (turned >= degrees - 1)
                    {
                        if (DateTime.UtcNow - stillSince > TimeSpan.FromSeconds(3)) break; // far enough, and stopped
                        text = $"Turned {turned:0.0}°: far enough. Stop turning, and lock the RA clutch";
                    }
                    else
                    {
                        text = $"Turn RA {settings.Direction.ToLowerInvariant()}: {turned:0.0}° of {degrees:0}° so far (or press Continue)";
                    }
                }
                report(text, point, true);
                await Task.WhenAny(resume.Task, Task.Delay(500, ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _continue = null;
        }
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // let it settle before the next frame
    }

    /// <summary>
    /// GoTo turns that cross the meridian make a German mount flip sides, which isn't a turn about the RA axis
    /// and ruins the measurement. Says so (and what to do) if the two turns from here would cross it.
    /// </summary>
    public static string? CrossesMeridian(EquatorialCoordinates jnow, PolarAlignmentSettings settings, GeoLocation site)
    {
        var start = Horizon.HourAngle(jnow, DateTime.UtcNow, site.Longitude);
        var end = start + 2 * settings.StepDegrees / 15 * (settings.Direction == "West" ? 1 : -1);
        if (start < 0 == end < 0 || Math.Abs(end) > 11) return null;
        var hours = Math.Abs(start);
        return $"Turning {settings.Direction.ToLowerInvariant()} twice by {settings.StepDegrees:0}° from here ({hours:0.0} h " +
               $"{(start < 0 ? "east" : "west")} of the meridian) would cross the meridian, and the mount would flip sides, which " +
               $"spoils the measurement. Point {2 * settings.StepDegrees / 15 + 0.5:0.#} h or more {(start < 0 ? "east" : "west")} of the " +
               $"meridian, or turn {(settings.Direction == "West" ? "east" : "west")}.";
    }

    /// <summary>An angle given in arcminutes, as degrees, minutes and seconds: 0° 01′ 23″ (as N.I.N.A. shows it).</summary>
    public static string Dms(double arcmin, bool signed = false)
    {
        var total = (int)Math.Round(Math.Abs(arcmin) * 60);
        var text = $"{total / 3600}° {total / 60 % 60:00}′ {total % 60:00}″";
        return signed ? (arcmin < 0 ? "−" : "+") + text : text;
    }

    /// <summary>Plain-language directions: which way to turn each knob.</summary>
    public static (string Altitude, string Azimuth) Describe(PolarError error, GeoLocation site)
    {
        var alt = $"{Dms(error.AltitudeArcmin)} too {(error.AltitudeArcmin > 0 ? "high" : "low")}";
        // Right of the pole as you face it is east in the north, west in the south.
        var right = error.AzimuthArcmin > 0;
        var side = site.Latitude >= 0 ? (right ? "east" : "west") : (right ? "west" : "east");
        return (alt, $"{Dms(error.AzimuthArcmin)} too far {side}");
    }

    /// <summary>
    /// Settings from before version 2 (three-point started near Dec 50°, turning 15° west) keep only what still means
    /// the same: the exposure, refraction and turning by hand. Everything else takes the new defaults.
    /// </summary>
    private PolarAlignmentSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new();
            var text = File.ReadAllText(_file);
            var json = JsonNode.Parse(text)?.AsObject();
            if (json?["Version"]?.GetValue<int>() is SettingsVersion)
                return JsonSerializer.Deserialize<PolarAlignmentSettings>(text) ?? new();
            var defaults = new PolarAlignmentSettings();
            return defaults with
            {
                ExposureSeconds = json?["ExposureSeconds"]?.GetValue<double>() ?? defaults.ExposureSeconds,
                Refraction = json?["Refraction"]?.GetValue<bool>() ?? defaults.Refraction,
                Manual = json?["Manual"]?.GetValue<bool>() ?? defaults.Manual,
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new PolarAlignmentSettings();
    }
}
