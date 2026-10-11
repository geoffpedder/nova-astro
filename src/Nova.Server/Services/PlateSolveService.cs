using System.Globalization;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Catalogs;
using Nova.Core.Imaging;
using Nova.Core.PlateSolving;
using Nova.Core.Simulation;

namespace Nova.Server.Services;

/// <param name="RaHours">Solved centre, J2000.</param>
public sealed record SolveInfo(
    int ImageId, bool Solved, double? RaHours, double? DecDegrees, string? RaText, string? DecText,
    double? Rotation, double? ImageScale, bool Flipped, double? FieldWidth, double? FieldHeight,
    string? Error, double Seconds, DateTime FinishedUtc);

/// <param name="X">Full-resolution pixel coordinates (0-based, rows in file order like the star overlay).</param>
public sealed record AnnotatedStar(double X, double Y, string Label, string Detail, double Magnitude);

public sealed record Annotations(int ImageId, int Width, int Height, IReadOnlyList<AnnotatedStar> Stars);

/// <summary>Where the last solved image lies on the sky (J2000): corners top-left, top-right, bottom-right, bottom-left as displayed.</summary>
public sealed record SolvedFrame(int ImageId, IReadOnlyList<EquatorialCoordinates> Corners, EquatorialCoordinates Center);

public sealed record CenteringStatus(bool Running, int Attempt, int MaxAttempts, double? ErrorArcmin, string Message);

/// <summary>Turning a camera without a rotator by hand to the planned framing angle.</summary>
/// <param name="Waiting">Waiting for you to turn the camera (then Check again, or Keep it as it is).</param>
/// <param name="PlannedAngle">Position angle of the top of the frame the plan wants, degrees.</param>
/// <param name="CameraAngle">Where the last solve found it.</param>
/// <param name="TurnDegrees">How far to turn, degrees (sign as on the sky: + towards east).</param>
/// <param name="Direction">"clockwise" or "anticlockwise", seen from behind the camera.</param>
/// <param name="UpsideDown">The quickest turn gives the planned framing rotated 180° (the same sky, upside down).</param>
/// <param name="FromSequence">The sequencer is waiting on this (it carries on by itself after a while).</param>
/// <param name="Rotator">The rotator doing the turning, when there is one (nobody needs to turn anything).</param>
public sealed record RotationStatus(
    bool Running, bool Waiting, double PlannedAngle, double? CameraAngle, double? TurnDegrees, string? Direction,
    bool UpsideDown, int Checks, string Message, bool FromSequence, double Tolerance, string? Rotator = null);

public sealed record SolverStatus(
    string Name, bool Available, string? Problem, bool Solving, SolveInfo? Last, CenteringStatus? Centering,
    RotationStatus? Rotation = null);

/// <summary>
/// Plate solving the latest frame, syncing the mount to the result, and centring on a target
/// (slew → expose → solve → sync → slew again, until within tolerance). Targets and solves are J2000;
/// the mount works in JNow, so positions are converted on the way in and out.
/// </summary>
public sealed class PlateSolveService
{
    /// <summary>Tells your phone when a sequence waits for the camera to be turned (set at startup).</summary>
    public NotificationService? Notifications { get; set; }

    private readonly EquipmentService _equipment;
    private readonly ImagingService _imaging;
    private readonly OpticsService _optics;
    private readonly ActivityLog _log;
    private readonly NovaOptions _options;
    private readonly SemaphoreSlim _solving = new(1, 1);
    private CancellationTokenSource? _centering;

    private readonly SiteService? _sites;

    public PlateSolveService(EquipmentService equipment, ImagingService imaging, OpticsService optics,
        ActivityLog log, IOptions<NovaOptions> options, SiteService? sites = null)
    {
        _sites = sites;
        _equipment = equipment;
        _imaging = imaging;
        _optics = optics;
        _log = log;
        _options = options.Value;
        var useSimulator = _options.Solver.Equals("simulator", StringComparison.OrdinalIgnoreCase)
            || (_options.Solver.Equals("auto", StringComparison.OrdinalIgnoreCase) && equipment.Provider is SimulatorProvider);
        Solver = useSimulator
            ? new SimulatedSolver()
            : new AstapSolver(_options.AstapPath, _options.AstapDatabaseFolder, _options.AstapDatabase);
    }

    public IPlateSolver Solver { get; }
    public SolveInfo? Last { get; private set; }

    /// <summary>A solve worked (the setup guide remembers that one has).</summary>
    public event Action? Solved;
    // Plate solutions of recent images (the latest and the history), by image id.
    private readonly Dictionary<int, (Wcs? Wcs, SolveInfo Info)> _solved = new();
    private const int RememberedSolves = 40;

    /// <summary>An image's plate solve, if it's been solved.</summary>
    public SolveInfo? SolveOf(int id)
    {
        lock (_solved) return _solved.TryGetValue(id, out var s) ? s.Info : null;
    }

    /// <summary>An angle to one decimal, 0–359.9 (359.96° shows as 0.0°, not 360.0°).</summary>
    private static string Degrees(double angle) =>
        ((Math.Round(angle, 1) % 360 + 360) % 360).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An image's plate solution (pixels ↔ J2000), if it's been solved.</summary>
    public Wcs? WcsOf(int id)
    {
        lock (_solved) return _solved.TryGetValue(id, out var s) ? s.Wcs : null;
    }
    public CenteringStatus? Centering { get; private set; }
    public RotationStatus? Rotation { get; private set; }

    public SolverStatus Status => new(Solver.Name, Solver.Available, Solver.Problem, _solving.CurrentCount == 0, Last, Centering, Rotation);

    /// <summary>Solves the latest frame (or one in the history). Uses the mount's position as a hint (or <paramref name="hint"/>).</summary>
    /// <param name="searchRadius">Degrees around the hint to search; null for the configured radius (180 without a hint).</param>
    public async Task<SolveInfo> SolveLatestAsync(CancellationToken ct, EquatorialCoordinates? hint = null, double? searchRadius = null,
        int? imageId = null)
    {
        var image = _imaging.InfoOf(imageId) ?? throw new InvalidOperationException(imageId is null ? "Take an image first" : "That image is no longer in the history");
        var fits = _imaging.FitsOf(image.Id) ?? throw new InvalidOperationException("That image's file has gone");
        var isLatest = image.Id == _imaging.LatestImage?.Id;
        if (!await _solving.WaitAsync(0, ct)) throw new InvalidOperationException("Already solving");
        try
        {
            // A file of its own per solve: two NOVA instances (or tests) solving at once must not swap images.
            var folder = Path.Combine(Path.GetTempPath(), "nova-solve");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"solve-{Guid.NewGuid():N}.fits");
            await File.WriteAllBytesAsync(path, fits, ct);
            try
            {
                // Hint: what the image's own header says (right for opened files, and the mount's position for
                // our frames), else the mount. Field size: our camera's optics preset, but not for opened files —
                // ASTAP reads their focal length and pixel size from the header itself.
                var raw = Fits.Read(fits);
                hint ??= FitsMetadata.Coordinates(raw)
                         ?? (image.OpenedFile is null && isLatest && _equipment.Mount is { IsConnected: true } mount
                             ? Precession.JNowToJ2000(mount.Coordinates, DateTime.UtcNow)
                             : null);
                var request = new PlateSolveRequest(path, hint,
                    searchRadius ?? (hint is null ? 180 : _options.SolveSearchRadius),
                    image.OpenedFile is null ? FieldHeight(fits, image.Height) : null);

                _log.Info($"Plate solving image {image.Id} with {Solver.Name}…");
                var result = await Solver.SolveAsync(request, ct);
                Last = ToInfo(image.Id, result);
                lock (_solved)
                {
                    if (_solved.Count >= RememberedSolves) _solved.Remove(_solved.Keys.Min());
                    _solved[image.Id] = (result.Solution?.Wcs, Last);
                }
                if (result.Solution is { } s)
                {
                    Solved?.Invoke();
                    _log.Info($"Solved image {image.Id} in {result.Duration.TotalSeconds:0.0} s: {s.Center} (J2000), " +
                              $"rotation {s.Rotation:0.0}°, {s.ImageScale:0.00}″/px{(s.Flipped ? ", mirrored" : "")}");
                    if (request.FieldHeightDegrees is > 0 and var expected && s.FieldHeightDegrees > 0
                        && Math.Abs(s.FieldHeightDegrees / expected - 1) > 0.15)
                        _log.Warn($"The image is {s.FieldHeightDegrees:0.00}° high, but the optics settings for this camera say " +
                                  $"{expected:0.00}°: check the focal length and pixel size (the image scale is {s.ImageScale:0.00}″/px)");
                }
                else
                    _log.Warn($"Plate solve failed: {result.Error}");
                return Last;
            }
            finally
            {
                foreach (var file in new[] { path, Path.ChangeExtension(path, ".ini"), Path.ChangeExtension(path, ".wcs"), Path.ChangeExtension(path, ".log") })
                    try { File.Delete(file); } catch (IOException) { /* best effort */ }
            }
        }
        finally
        {
            _solving.Release();
        }
    }

    /// <summary>
    /// Named catalog stars in the latest image, placed with its plate solution: the <paramref name="count"/>
    /// brightest inside the frame. Null when the latest image hasn't been solved.
    /// </summary>
    public Annotations? Annotate(int count = 40, int? imageId = null)
    {
        if (_imaging.InfoOf(imageId) is not { } image || WcsOf(image.Id) is not { } wcs) return null;
        var center = wcs.ToSky((image.Width - 1) / 2.0, (image.Height - 1) / 2.0);
        var corner = wcs.ToSky(0, 0);
        var radius = Math.Max(Precession.Separation(center, corner), Precession.Separation(center, wcs.ToSky(image.Width - 1, 0))) + 0.01;

        var stars = new List<AnnotatedStar>();
        foreach (var star in StarCatalog.Near(center, radius))
        {
            if (wcs.ToPixel(star.Position) is not { } at || at.X < 0 || at.Y < 0 || at.X > image.Width - 1 || at.Y > image.Height - 1)
                continue;
            stars.Add(new AnnotatedStar(Math.Round(at.X, 1), Math.Round(at.Y, 1), star.Label, star.Detail, star.Magnitude));
            if (stars.Count == count) break;
        }
        return new Annotations(image.Id, image.Width, image.Height, stars);
    }

    /// <summary>The latest image's (or a history image's) outline on the sky, if it has been solved.</summary>
    public SolvedFrame? Frame(int? imageId = null)
    {
        if (_imaging.InfoOf(imageId) is not { } image || WcsOf(image.Id) is not { } wcs) return null;
        var (w, h) = (image.Width - 1.0, image.Height - 1.0);
        return new SolvedFrame(image.Id, [wcs.ToSky(0, 0), wcs.ToSky(w, 0), wcs.ToSky(w, h), wcs.ToSky(0, h)], wcs.ToSky(w / 2, h / 2));
    }

    /// <summary>Tells the mount it's pointing where the last solve says.</summary>
    public async Task SyncToLastAsync(CancellationToken ct)
    {
        // Only the latest frame from the camera says where the mount points now.
        if (_imaging.LatestImage is not { OpenedFile: null } latest || SolveOf(latest.Id) is not { Solved: true, RaHours: { } ra, DecDegrees: { } dec })
            throw new InvalidOperationException("Solve the latest frame from the camera first");
        var mount = _equipment.Mount is { IsConnected: true } m ? m : throw new InvalidOperationException("No mount connected");
        var actual = new EquatorialCoordinates(ra, dec);
        await mount.SyncAsync(Precession.J2000ToJNow(actual, DateTime.UtcNow), ct);
        _log.Info($"{mount.Name} synced to {actual} (J2000)");
    }

    public bool StartCentering(EquatorialCoordinates target, out string? error)
    {
        if (_equipment.Mount is not { IsConnected: true }) { error = "No mount connected"; return false; }
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (!Solver.Available) { error = Solver.Problem; return false; }

        CancelCentering();
        var cts = new CancellationTokenSource();
        _centering = cts;
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await CenterAsync(target, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Centering = Centering is { } c ? c with { Running = false, Message = "Cancelled" } : null;
                _log.Warn("Centring cancelled");
            }
            catch (Exception ex)
            {
                Centering = Centering is { } c ? c with { Running = false, Message = ex.Message } : null;
                _log.Error("Centring failed", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _centering, null, cts);
                cts.Dispose();
            }
        });
        return true;
    }

    public void CancelCentering() => _centering?.Cancel();

    /// <summary>Centres on a J2000 target and waits (for sequences); throws if it can't get there.</summary>
    public async Task CenterAndWaitAsync(EquatorialCoordinates target, CancellationToken ct)
    {
        if (!Solver.Available) throw new InvalidOperationException(Solver.Problem ?? "No plate solver");
        CancelCentering();
        try
        {
            await CenterAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            Centering = Centering is { } c ? c with { Running = false, Message = "Cancelled" } : null;
            throw;
        }
        catch (Exception ex)
        {
            Centering = Centering is { } c ? c with { Running = false, Message = ex.Message } : null;
            throw;
        }
    }

    private async Task CenterAsync(EquatorialCoordinates target, CancellationToken ct)
    {
        var max = Math.Max(1, _options.CenterMaxAttempts);
        var tolerance = _options.CenterToleranceArcmin;
        _log.Info($"Centring on {target} (J2000), within {tolerance:0.##}′");

        for (var attempt = 1; attempt <= max; attempt++)
        {
            var mount = _equipment.Mount ?? throw new InvalidOperationException("The mount went away");
            Centering = new CenteringStatus(true, attempt, max, Centering?.ErrorArcmin, "Slewing");
            if (_sites is not null) await _sites.SlewSafelyAsync(mount, Precession.J2000ToJNow(target, DateTime.UtcNow), ct);
            else await mount.SlewAsync(Precession.J2000ToJNow(target, DateTime.UtcNow), ct);

            Centering = Centering with { Message = "Exposing" };
            await _imaging.CaptureAsync(_options.SolveExposureSeconds, save: false, ct);

            Centering = Centering with { Message = "Solving" };
            var solve = await SolveLatestAsync(ct, hint: target);
            if (!solve.Solved) throw new InvalidOperationException($"Plate solve failed: {solve.Error}");

            var actual = new EquatorialCoordinates(solve.RaHours!.Value, solve.DecDegrees!.Value);
            var off = Precession.Separation(actual, target) * 60;
            Centering = Centering with { ErrorArcmin = Math.Round(off, 2) };
            if (off <= tolerance)
            {
                Centering = Centering with { Running = false, Message = $"Centred, {off:0.0}′ off" };
                _log.Info($"Centred on attempt {attempt}: {off:0.00}′ from the target");
                return;
            }

            _log.Info($"Attempt {attempt}: {off:0.0}′ off; syncing and slewing again");
            Centering = Centering with { Message = $"{off:0.0}′ off, syncing" };
            await mount.SyncAsync(Precession.J2000ToJNow(actual, DateTime.UtcNow), ct);
        }

        throw new InvalidOperationException($"Still {Centering?.ErrorArcmin:0.0}′ off after {max} attempts");
    }

    // ---- Camera rotation, by hand ----

    /// <summary>How close to the planned angle is close enough, degrees.</summary>
    public const double RotationTolerance = 1;
    private CancellationTokenSource? _rotating;
    private TaskCompletionSource<bool>? _rotationChoice; // true: turned it, check again; false: keep it as it is

    /// <summary>
    /// Starts matching the camera's rotation to <paramref name="angle"/> (position angle of the frame's top), then
    /// re-centres on the J2000 <paramref name="target"/> if the camera was turned.
    /// </summary>
    public bool StartMatchingRotation(EquatorialCoordinates target, double angle, out string? error)
    {
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (!Solver.Available) { error = Solver.Problem; return false; }
        if (Rotation is { Running: true }) { error = "Already matching the camera's rotation"; return false; }
        var cts = new CancellationTokenSource();
        _rotating = cts;
        Rotation = new RotationStatus(true, false, (angle % 360 + 360) % 360, null, null, null, false, 0, "Starting", false, RotationTolerance);
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await MatchRotationAsync(target, angle, waitLimit: null, fromSequence: false, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Rotation = Rotation is { } r ? r with { Running = false, Waiting = false, Message = "Cancelled" } : null;
            }
            catch (Exception ex)
            {
                Rotation = Rotation is { } r ? r with { Running = false, Waiting = false, Message = ex.Message } : null;
                _log.Error("Matching the camera's rotation failed", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _rotating, null, cts);
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>
    /// For sequences: matches the camera's rotation, waiting for someone to turn it. Carries on with the camera as
    /// it is if nobody does within <paramref name="waitLimit"/>. <paramref name="progress"/> gets each instruction.
    /// </summary>
    public async Task MatchRotationAndWaitAsync(EquatorialCoordinates target, double angle, TimeSpan waitLimit,
        Action<string>? progress, CancellationToken ct)
    {
        if (!Solver.Available) throw new InvalidOperationException(Solver.Problem ?? "No plate solver");
        try
        {
            await MatchRotationAsync(target, angle, waitLimit, fromSequence: true, ct, progress);
        }
        catch (Exception ex)
        {
            Rotation = Rotation is { } r ? r with { Running = false, Waiting = false, Message = ex is OperationCanceledException ? "Cancelled" : ex.Message } : null;
            throw;
        }
    }

    /// <summary>The camera has been turned: check it again.</summary>
    public bool RotationTurned() => _rotationChoice?.TrySetResult(true) == true;

    /// <summary>Stop asking and carry on with the camera as it is.</summary>
    public bool KeepRotation() => _rotationChoice?.TrySetResult(false) == true;

    public void CancelRotation() => _rotating?.Cancel();

    /// <summary>Turn a connected rotator rather than asking for the camera to be turned by hand (off in tests of the hand route).</summary>
    public bool UseRotator { get; set; } = true;

    /// <summary>Rotator moves before giving up on reaching the angle (it should take one or two).</summary>
    private const int MaxRotatorMoves = 6;

    private async Task MatchRotationAsync(EquatorialCoordinates target, double angle, TimeSpan? waitLimit, bool fromSequence,
        CancellationToken ct, Action<string>? progress = null)
    {
        angle = (angle % 360 + 360) % 360;
        _log.Info($"Matching the camera's rotation to {Degrees(angle)}°");
        double? before = null, asked = null;
        string? movedRotator = null; // the last turn was this rotator's, not by hand
        string? usedRotator = null;  // any turn this run was a rotator's
        var rotatorMoves = 0;
        var turned = false;
        for (var check = 1; ; check++)
        {
            Rotation = new RotationStatus(true, false, angle, Rotation?.CameraAngle, null, null, false, check,
                "Taking a frame to measure the camera's angle", fromSequence, RotationTolerance, usedRotator);
            await _imaging.CaptureAsync(_options.SolveExposureSeconds, save: false, ct);
            Rotation = Rotation with { Message = "Solving" };
            var solve = await SolveLatestAsync(ct, hint: target);
            if (!solve.Solved) throw new InvalidOperationException($"Plate solve failed: {solve.Error}");
            var camera = LatestCameraAngle() ?? throw new InvalidOperationException("The plate solve gave no orientation");

            // If the camera moved the opposite way to what was asked, the words (or the rotator's direction) were the
            // wrong way round for this setup (e.g. a mirror in the light path): swap them from now on.
            if (before is { } was && asked is { } wanted)
            {
                var moved = CameraAngle.Wrap180(camera - was);
                if (Math.Abs(moved) > 2 && Math.Sign(moved) != Math.Sign(wanted))
                {
                    if (movedRotator is { } r)
                    {
                        SetRotatorSense(r, -RotatorSense(r));
                        _log.Info($"{r} turns the camera the other way on the sky from what I assumed: noted for next time");
                    }
                    else
                    {
                        RotationReversed = !RotationReversed;
                        _log.Warn("The camera's angle moved the other way from what I asked: swapping clockwise and anticlockwise from now on");
                    }
                }
            }

            var (turn, upsideDown) = CameraAngle.Turn(angle, camera);
            if (Math.Abs(turn) <= RotationTolerance)
            {
                var done = $"Camera at {Degrees(camera)}°{(upsideDown ? " (upside down)" : "")}, within {RotationTolerance:0}° of the plan's {Degrees(angle)}°";
                Rotation = Rotation with { CameraAngle = Math.Round(camera, 2), TurnDegrees = Math.Round(turn, 2), UpsideDown = upsideDown, Message = done };
                _log.Info(done);
                progress?.Invoke(done);
                if (turned) await RecenterAfterTurningAsync(target, progress, ct);
                Rotation = Rotation with { Running = false, Message = done };
                return;
            }

            // A rotator: turn it, then measure again (the first move also shows which way it turns the sky).
            if (UseRotator && _equipment.Rotator is { IsConnected: true } rotator)
            {
                if (++rotatorMoves > MaxRotatorMoves)
                    throw new InvalidOperationException($"{rotator.Name} didn't bring the camera within {RotationTolerance:0}° of {Degrees(angle)}° in {MaxRotatorMoves} moves (it's at {Degrees(camera)}°): is the camera on it?");
                var move = turn * RotatorSense(rotator.Name);
                var to = ((rotator.Angle + move) % 360 + 360) % 360;
                var turning = $"Turning {rotator.Name} {Math.Abs(move):0.#}° to {to:0.#}°: the camera's at {Degrees(camera)}°, the plan is {Degrees(angle)}°" +
                              (upsideDown ? " (the plan upside down: the same sky)" : "");
                Rotation = Rotation with
                {
                    CameraAngle = Math.Round(camera, 2), TurnDegrees = Math.Round(turn, 2), UpsideDown = upsideDown,
                    Message = turning, Rotator = rotator.Name,
                };
                _log.Info(turning);
                progress?.Invoke(turning);
                await rotator.MoveToAsync(to, ct);
                turned = true;
                (before, asked, movedRotator, usedRotator) = (camera, turn, rotator.Name, rotator.Name);
                continue;
            }

            var direction = (turn > 0) != RotationReversed ? "anticlockwise" : "clockwise";
            var instruction = $"Turn the camera {Math.Abs(turn):0.#}° {direction}, as seen from behind it" +
                              (upsideDown ? " (that frames the plan upside down: the same sky)" : "") +
                              $". It's at {Degrees(camera)}°, the plan is {Degrees(angle)}°.";
            var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _rotationChoice = choice;
            Rotation = Rotation with
            {
                Waiting = true, CameraAngle = Math.Round(camera, 2), TurnDegrees = Math.Round(turn, 2), Direction = direction,
                UpsideDown = upsideDown, Message = instruction,
            };
            _log.Info(instruction);
            progress?.Invoke(instruction);

            Notifications?.Notify(NotificationKind.NeedsYou, "NOVA is waiting for you: turn the camera", instruction);
            bool? turnedIt;
            using (ct.Register(() => choice.TrySetCanceled(ct)))
            {
                var waited = waitLimit is { } limit ? Task.Delay(limit, ct) : Task.Delay(Timeout.Infinite, ct);
                var first = await Task.WhenAny(choice.Task, waited);
                ct.ThrowIfCancellationRequested();
                turnedIt = first == choice.Task ? await choice.Task : null;
            }
            _rotationChoice = null;

            if (turnedIt != true)
            {
                var kept = turnedIt is null
                    ? $"Nobody turned the camera in {waitLimit!.Value.TotalMinutes:0} minutes: carrying on with it at {Degrees(camera)}°"
                    : $"Keeping the camera at {Degrees(camera)}° (the plan was {Degrees(angle)}°)";
                if (turnedIt is null) _log.Warn(kept); else _log.Info(kept);
                Rotation = Rotation with { Waiting = false, Message = kept };
                progress?.Invoke(kept);
                if (turned) await RecenterAfterTurningAsync(target, progress, ct);
                Rotation = Rotation with { Running = false, Message = kept };
                return;
            }
            turned = true;
            (before, asked, movedRotator) = (camera, turn, null);
        }
    }

    /// <summary>Turning the camera can nudge the pointing: centre again.</summary>
    private async Task RecenterAfterTurningAsync(EquatorialCoordinates target, Action<string>? progress, CancellationToken ct)
    {
        if (_equipment.Mount is not { IsConnected: true }) return;
        Rotation = Rotation! with { Waiting = false, Message = "Re-centring after turning the camera" };
        progress?.Invoke("Re-centring after turning the camera");
        await CenterAsync(target, ct);
    }

    /// <summary>The camera's angle in the latest image, if it was solved.</summary>
    public double? LatestCameraAngle()
    {
        if (_imaging.LatestImage is not { } image || WcsOf(image.Id) is not { } wcs) return null;
        return CameraAngle.OfTop(wcs, image.Width, image.Height);
    }

    private string RotationFile => Path.Combine(_options.ResolveConfigDirectory(), "rotation.json");

    /// <summary>What's been learnt about turning cameras: by hand, and with each rotator. Kept between runs.</summary>
    private sealed record RotationMemory(bool Reversed = false, Dictionary<string, int>? Rotators = null);

    private RotationMemory LoadRotationMemory()
    {
        try
        {
            return File.Exists(RotationFile)
                ? System.Text.Json.JsonSerializer.Deserialize<RotationMemory>(File.ReadAllText(RotationFile), Sequence.Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new();
        }
    }

    private void SaveRotationMemory(RotationMemory memory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RotationFile)!);
            File.WriteAllText(RotationFile, System.Text.Json.JsonSerializer.Serialize(memory, Sequence.Json));
        }
        catch (IOException ex)
        {
            _log.Warn($"Couldn't remember the camera's turning direction: {ex.Message}");
        }
    }

    /// <summary>
    /// Clockwise and anticlockwise are the other way round for this setup (learnt when a turn moved the angle the
    /// wrong way).
    /// </summary>
    public bool RotationReversed
    {
        get => LoadRotationMemory().Reversed;
        private set => SaveRotationMemory(LoadRotationMemory() with { Reversed = value });
    }

    /// <summary>
    /// +1 if turning this rotator to a larger angle turns the frame towards east on the sky (a larger position angle),
    /// −1 if the other way. Assumed +1 until a move shows otherwise.
    /// </summary>
    public int RotatorSense(string rotator) => LoadRotationMemory().Rotators?.GetValueOrDefault(rotator) is -1 ? -1 : 1;

    private void SetRotatorSense(string rotator, int sense)
    {
        var memory = LoadRotationMemory();
        var rotators = new Dictionary<string, int>(memory.Rotators ?? new()) { [rotator] = sense };
        SaveRotationMemory(memory with { Rotators = rotators });
    }

    /// <summary>Image height on the sky from the camera's active optics preset, so the solver needn't guess.</summary>
    private double? FieldHeight(byte[] fits, int heightPixels)
    {
        if (_equipment.Camera is not { } camera) return null;
        var scale = _optics.All.FirstOrDefault(o => o.Camera == camera.Name)?.ImageScale;
        if (scale is null) return null;
        var header = Fits.ReadHeader(fits);
        var binning = header.FirstOrDefault(c => c.Key == "XBINNING") is { } b
                      && double.TryParse(b.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x > 0 ? x : 1;
        return scale.Value * binning * heightPixels / 3600;
    }

    private static SolveInfo ToInfo(int imageId, PlateSolveResult r) => r.Solution is { } s
        ? new SolveInfo(imageId, true, s.Center.RaHours, s.Center.DecDegrees, s.Center.RaText, s.Center.DecText,
            Math.Round(s.Rotation, 2), Math.Round(s.ImageScale, 3), s.Flipped,
            Math.Round(s.FieldWidthDegrees, 3), Math.Round(s.FieldHeightDegrees, 3), null,
            Math.Round(r.Duration.TotalSeconds, 1), DateTime.UtcNow)
        : new SolveInfo(imageId, false, null, null, null, null, null, null, false, null, null, r.Error,
            Math.Round(r.Duration.TotalSeconds, 1), DateTime.UtcNow);
}
