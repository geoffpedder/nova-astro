using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.PlateSolving;

namespace Nova.Server.Services;

public sealed record SequenceRowProgress(int Done, int Count, bool Skipped);

/// <param name="State">Waiting (not started yet), Running, Done, Skipped or Stopped.</param>
/// <param name="Note">Why it was skipped or stopped early, e.g. "set below 30°" or "stop time 01:15 reached".</param>
public sealed record SequenceTargetProgress(string Name, string State, IReadOnlyList<SequenceRowProgress> Rows, string? Note);

/// <param name="State">Running, Paused, Done, Failed or Aborted.</param>
/// <param name="Step">What the sequence is doing: Cooling, Centring, Autofocus, Guiding, Exposing, Dithering, Ending…</param>
/// <param name="CurrentRow">Index into the current target's rows of the frame being taken.</param>
/// <param name="Rows">The current target's rows; <paramref name="Targets"/> has every target's.</param>
/// <param name="EtaUtc">When the frames should be done, from the remaining exposure time plus measured overheads.</param>
public sealed record SequencerStatus(
    string State, string SequenceName, string? Target, string Step, string Message, bool PauseRequested,
    int FramesDone, int FramesTotal, IReadOnlyList<SequenceRowProgress> Rows, int? CurrentRow,
    double? LastHfr, int Autofocuses, int Dithers, int Failures,
    DateTime StartedUtc, DateTime? EtaUtc, DateTime? FinishedUtc, int Flips = 0, DateTime? FlipDueUtc = null,
    IReadOnlyList<SequenceTargetProgress>? Targets = null, int CurrentTarget = 0,
    DateTime? NightEndUtc = null, string? NightEnd = null, DateTime? FlipLatestUtc = null, string? FlipNote = null);

public sealed record SequencerInfo(bool Running, SequencerStatus? Status, IReadOnlyList<string> Saved);

/// <summary>
/// Runs a <see cref="Sequence"/>: cool the camera, then each target in turn — wait for its start time and for
/// it to be high enough, centre, focus and guide, take its frames (changing filters and camera settings,
/// dithering, refocusing, flipping at the meridian) until they're done, its stop time comes or it sinks too
/// low — and finally stop guiding, warm the camera or park as asked. Pause waits for the current frame to
/// finish; Abort cancels it. Sequences are kept as JSON in ~/.config/nova/sequences.
/// </summary>
public sealed class SequencerService
{
    /// <summary>Moves the focuser by the filters' focus offsets on filter changes (set at startup).</summary>
    public FocusOffsetService? FocusOffsets { get; set; }

    /// <summary>Tells your phone when a sequence ends, fails or skips a target (set at startup).</summary>
    public NotificationService? Notifications { get; set; }

    /// <summary>Where frames go: warned at the start if they won't fit on a USB drive (set at startup).</summary>
    public StorageService? Storage { get; set; }

    private const int MaxConsecutiveFailures = 3;
    private const int MaxFlipAttempts = 3;
    private static readonly TimeSpan FlipRetryDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CoolingTimeout = TimeSpan.FromMinutes(20);
    /// <summary>How long a target waits for the camera to be turned before carrying on as it is.</summary>
    public static TimeSpan RotationWait { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>How long flats between targets wait for the panel before going on without them.</summary>
    public static TimeSpan FlatsWait { get; set; } = TimeSpan.FromMinutes(20);

    private readonly EquipmentService _equipment;
    private readonly ImagingService _imaging;
    private readonly PlateSolveService _solver;
    private readonly AutofocusService _autofocus;
    private readonly GuiderService _guider;
    private readonly SiteService _sites;
    private readonly FlatsService? _flats;
    private readonly ActivityLog _log;
    private readonly string _folder;
    private readonly object _sync = new();
    private CancellationTokenSource? _run;
    private TaskCompletionSource? _resume;
    private volatile bool _pauseRequested;
    private volatile bool _skipRequested;
    private volatile bool _skipTargetRequested;

    public SequencerService(EquipmentService equipment, ImagingService imaging, PlateSolveService solver,
        AutofocusService autofocus, GuiderService guider, SiteService sites, ActivityLog log, IOptions<NovaOptions> options,
        FlatsService? flats = null)
    {
        _sites = sites;
        _flats = flats;
        _equipment = equipment;
        _imaging = imaging;
        _solver = solver;
        _autofocus = autofocus;
        _guider = guider;
        _log = log;
        _folder = Path.Combine(options.Value.ResolveConfigDirectory(), "sequences");
    }

    public SequencerStatus? Status { get; private set; }
    public bool Running
    {
        get { lock (_sync) return _run is not null; }
    }

    public SequencerInfo Info => new(Running, Status, SavedNames());

    // ---- Saved sequences ----

    public IReadOnlyList<string> SavedNames() =>
        Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList()
            : [];

    public Sequence? Load(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? JsonSerializer.Deserialize<Sequence>(File.ReadAllText(path), Sequence.Json) : null;
    }

    public void Save(Sequence sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence.Name)) throw new ArgumentException("Give the sequence a name");
        if (sequence.Validate() is { } problem) throw new ArgumentException(problem);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(sequence.Name), JsonSerializer.Serialize(sequence, Sequence.Json));
        _log.Info($"Saved sequence '{sequence.Name}'");
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) throw new ArgumentException($"No sequence called '{name}'");
        File.Delete(path);
        _log.Info($"Deleted sequence '{name}'");
    }

    private string PathFor(string name) =>
        Path.Combine(_folder, (ImagingService.FolderName(name) ?? throw new ArgumentException("Give the sequence a name")) + ".json");

    // ---- Control ----

    public bool Start(Sequence sequence, out string? error)
    {
        if ((error = sequence.Validate()) is not null) return false;
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (_imaging.IsBusy) { error = "An exposure is running"; return false; }
        var wheel = _equipment.FilterWheel;
        foreach (var filter in sequence.Plans.SelectMany(p => p.Rows).Where(r => r.Enabled && r.Filter is not null).Select(r => r.Filter!).Distinct())
        {
            if (wheel is not { IsConnected: true }) { error = $"Filter '{filter}' needs a connected filter wheel"; return false; }
            if (SlotOf(wheel, filter) is null) { error = $"The filter wheel has no filter called '{filter}'"; return false; }
        }
        if (sequence.Start.CenterTarget && _equipment.Mount is not { IsConnected: true }) { error = "Centring needs a connected mount"; return false; }
        if (sequence.Start.CenterTarget && !_solver.Solver.Available) { error = _solver.Solver.Problem; return false; }
        if (sequence.FlatsSettings.Enabled && _flats is null) { error = "Flats aren't available"; return false; }
        if (sequence.FlatsSettings.Enabled && _flats!.Running) { error = "Flats are being taken"; return false; }

        // Saving to a USB drive: say now if the night won't fit (the frames past that go to the mini PC).
        if (Storage is { } storage && _equipment.Camera.SensorSize is { } sensor)
        {
            var bytes = sequence.Plans.SelectMany(p => p.Rows).Where(r => r.Enabled)
                .Sum(r => (long)r.Count * (2L * sensor.Width * sensor.Height / Math.Max(1, (r.Binning ?? 1) * (r.Binning ?? 1)) + 20_000));
            if (storage.SpaceWarning(bytes) is { } warning) _log.Warn(warning);
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_run is not null) { error = "A sequence is already running"; return false; }
            _run = cts = new CancellationTokenSource();
        }
        _pauseRequested = false;
        _skipRequested = false;
        _skipTargetRequested = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(sequence, cts.Token);
            }
            finally
            {
                lock (_sync) _run = null;
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>Pause after the current frame (or step) finishes.</summary>
    public void Pause()
    {
        if (!Running) return;
        _pauseRequested = true;
        Status = Status! with { PauseRequested = true };
        _log.Info("Sequence will pause after the current frame");
    }

    public void Resume()
    {
        _pauseRequested = false;
        if (Status is { } s) Status = s with { PauseRequested = false };
        _resume?.TrySetResult();
    }

    /// <summary>Skips the rest of the current row.</summary>
    public void Skip() => _skipRequested = true;

    /// <summary>Moves on to the next target (also while waiting for this one).</summary>
    public void SkipTarget() => _skipTargetRequested = true;

    // The running sequence's refocus triggers and end-of-night steps: changeable while it runs.
    private SequenceAutofocus? _liveAutofocus;
    private SequenceEnd? _liveEnd;

    /// <summary>
    /// Changes when the running sequence refocuses, and what happens at the end of the night (and when it ends), from
    /// the next frame on. A new stop time is worked out from now.
    /// </summary>
    public void UpdateLiveSettings(SequenceAutofocus autofocus, SequenceEnd end)
    {
        if (!Running || _liveAutofocus is null || _liveEnd is null) throw new InvalidOperationException("No sequence is running");
        if (Status?.Step is "Ending" or "Done") throw new InvalidOperationException("The sequence is already ending");
        if (autofocus.TemperatureChange is < 0 || autofocus.HfrIncreasePercent is < 0 || autofocus.EveryFrames is < 0)
            throw new ArgumentException("Refocus triggers can't be negative");
        var changes = new List<string>();
        if (autofocus != _liveAutofocus) changes.Add("refocus triggers");
        if (end.StopAt != _liveEnd.StopAt)
        {
            var nightEnd = SequenceEnd.NightEnd(end.StopAt, DateTime.UtcNow, Site());
            _nightEnd = nightEnd;
            Status = Status! with { NightEndUtc = nightEnd?.Utc, NightEnd = nightEnd?.Label };
            changes.Add(nightEnd is { } n ? $"stop at {n.Label}" : "no stop time");
        }
        if (end with { StopAt = _liveEnd.StopAt } != _liveEnd) changes.Add("end steps");
        _liveAutofocus = autofocus;
        _liveEnd = end;
        if (changes.Count > 0) _log.Info($"Sequence changed while running: {string.Join(", ", changes)}");
        RefreshStatus?.Invoke();
    }

    /// <summary>
    /// Changes how many frames a row of the running sequence takes, e.g. to add more when there's time. Not
    /// below the frames already taken (setting it to that finishes the row); raising a skipped row's count
    /// brings it back.
    /// </summary>
    public void SetCount(int target, int row, int count)
    {
        if (_targets is not { } targets || _parts is not { } parts || !Running) throw new InvalidOperationException("No sequence is running");
        if (Status?.Step is "Ending" or "Done") throw new InvalidOperationException("The sequence has finished its frames");
        if (target < 0 || target >= targets.Length) throw new ArgumentException("No such target");
        if (_targetState?[target] is "Done" or "Skipped" or "Stopped") throw new ArgumentException("That target is finished");
        var state = targets[target];
        if (row < 0 || row >= state.Count.Length) throw new ArgumentException("No such row");
        if (!parts[target].Rows[row].Enabled) throw new ArgumentException("That row is turned off");
        if (count < state.Done[row]) throw new ArgumentException($"Row {row + 1} already has {state.Done[row]} frames");
        if (count > 10_000) throw new ArgumentException("Count must be at most 10000");
        var before = state.Count[row];
        state.Count[row] = count;
        if (count > state.Done[row]) state.Skipped[row] = false;
        _log.Info($"{(targets.Length > 1 ? $"{parts[target].Target?.Name}, row" : "Row")} {row + 1}: {before} → {count} frames");
        RefreshStatus?.Invoke();
    }

    private RunState[]? _targets;     // per target, made at the start so counts can change before a target begins
    private List<Sequence>? _parts;   // the sequence narrowed to each target
    private string[]? _targetState;
    private Action? RefreshStatus;

    /// <summary>Stops now: the current exposure is cancelled; end-of-sequence steps don't run.</summary>
    public void Abort()
    {
        lock (_sync) _run?.Cancel();
    }

    // ---- Running ----

    private sealed class RunState(Sequence sequence)
    {
        public readonly int[] Done = new int[sequence.Rows.Count];
        // Planned frames per row (0 for disabled rows). Its own copy, so counts can change during the run.
        public readonly int[] Count = sequence.Rows.Select(r => r.Enabled ? r.Count : 0).ToArray();
        public readonly bool[] Skipped = new bool[sequence.Rows.Count];
        public int? Current;
        public string? Filter;            // what the wheel is on
        public string? LastFilter;        // filter of the previous frame
        public double? LastHfr;
        public readonly Dictionary<string, double> HfrBaseline = new(); // per filter, first frame after autofocus
        public double? AutofocusTemperature;
        public int FramesSinceAutofocus;
        public int FramesSinceDither;
        public int Autofocuses, Dithers, Failures, ConsecutiveFailures;
        public double OverheadSeconds = 3; // per-frame time beyond the exposure, learned as frames come in
        // Meridian flip: for mounts that don't report their pier side, "seen east of the meridian, not yet flipped".
        public bool SeenEast, Flipped, WarnedNoSite;
        public int Flips, FlipAttempts;
        /// <summary>When the flip should actually happen (it waits for the frame running then), the latest it can, and how.</summary>
        public DateTime? FlipDueUtc, FlipRetryUtc, FlipLatestUtc;
        public string? FlipNote;
        /// <summary>The flip this warning was for (its due time), so each flip is announced once.</summary>
        public DateTime? FlipWarnedFor;
        public (double? Gain, double? Offset, int? Binning, FrameType? Type) Applied;

        /// <summary>Starting the next target: keep what describes the equipment and the night, not the target.</summary>
        public void TakeOver(RunState previous)
        {
            Filter = previous.Filter;
            LastFilter = previous.LastFilter;
            LastHfr = previous.LastHfr;
            AutofocusTemperature = previous.AutofocusTemperature;
            FramesSinceAutofocus = previous.FramesSinceAutofocus;
            Autofocuses = previous.Autofocuses;
            Dithers = previous.Dithers;
            Failures = previous.Failures;
            OverheadSeconds = previous.OverheadSeconds;
            WarnedNoSite = previous.WarnedNoSite;
            Flips = previous.Flips;
            Applied = previous.Applied;
        }
    }

    private async Task RunAsync(Sequence sequence, CancellationToken ct)
    {
        var plans = sequence.Plans;
        var parts = _parts = plans.Select(sequence.For).ToList();
        var states = _targets = parts.Select(p => new RunState(p)).ToArray();
        var targetState = _targetState = plans.Select(_ => "Waiting").ToArray();
        var notes = new string?[plans.Count];
        var current = 0;
        var state = states[0];
        string Name(int k) => plans[k].Target?.Name ?? (plans.Count > 1 ? $"Target {k + 1}" : "Here");
        int FramesDone() => states.Sum(s => s.Done.Sum());

        var (total, _) = sequence.Totals;
        Status = new SequencerStatus("Running", sequence.Name, plans[0].Target?.Name, "Starting", "Starting", false,
            0, total, Progress(parts[0], states[0]), null, null, 0, 0, 0, DateTime.UtcNow, null, null,
            Targets: TargetsProgress(), CurrentTarget: 0);
        // The night's end (dawn, by default), shared by every target: worked out once, at the start.
        _nightEnd = SequenceEnd.NightEnd(sequence.EndSettings.StopAt, DateTime.UtcNow, Site());
        if (_nightEnd is null && sequence.EndSettings.StopAt is SequenceEnd.AstronomicalDawn or SequenceEnd.NauticalDawn or SequenceEnd.Sunrise)
            _log.Warn("Not stopping at dawn: set your site (latitude and longitude) in the Mount panel");
        Status = Status with { NightEndUtc = _nightEnd?.Utc, NightEnd = _nightEnd?.Label };
        (_liveAutofocus, _liveEnd) = (sequence.AutofocusSettings, sequence.EndSettings);
        _log.Info($"Sequence '{sequence.Name}' started: {total} frames" +
                  (plans.Count > 1 ? $" of {plans.Count} targets ({string.Join(", ", plans.Select((_, k) => Name(k)))})"
                      : plans[0].Target is { } only ? $" of {only.Name}" : ""));
        await PrepareMountForFlipsAsync(sequence, ct);

        IReadOnlyList<SequenceTargetProgress> TargetsProgress() =>
            plans.Select((_, k) => new SequenceTargetProgress(Name(k), targetState[k], Progress(parts[k], states[k]), notes[k])).ToList();

        void Report(string step, string message) =>
            Status = Status! with
            {
                Target = plans[current].Target?.Name, Step = step, Message = message,
                Rows = Progress(parts[current], state), CurrentRow = state.Current,
                FramesDone = FramesDone(), FramesTotal = states.Sum(s => s.Count.Sum()), LastHfr = state.LastHfr,
                Autofocuses = state.Autofocuses, Dithers = state.Dithers, Failures = state.Failures,
                EtaUtc = Eta(parts, states, targetState, current), Flips = state.Flips, FlipDueUtc = state.FlipDueUtc,
                FlipLatestUtc = state.FlipLatestUtc, FlipNote = state.FlipNote,
                Targets = TargetsProgress(), CurrentTarget = current,
            };

        RefreshStatus = () => Report(Status!.Step, Status.Message);

        // For the end-of-night message: each target's frames and how it ended, and what the mount did.
        string NightSummary()
        {
            var lines = plans.Select((_, k) =>
                $"{Name(k)}: {states[k].Done.Sum()} of {states[k].Count.Sum()} frames" +
                (targetState[k] is "Done" ? "" : $" ({targetState[k].ToLowerInvariant()}{(notes[k] is { } note ? $": {note}" : "")})"));
            var end = (_liveEnd ?? sequence.EndSettings).Park ? "\nThe mount is parked." : "";
            return string.Join("\n", lines) + end;
        }

        // Targets imaged since the last flats, which the next flats will cover (all at the same camera angle).
        var flatsFor = new List<int>();
        async Task FlatsAsync(TimeSpan? waitLimit, bool leaveCovered = false)
        {
            if (flatsFor.Count == 0) return;
            var angle = plans[flatsFor[0]].Target?.Rotation;
            var name = string.Join(" + ", flatsFor.Select(Name)) + (angle is { } a ? $" ({a:0.#}°)" : "");
            flatsFor.Clear();
            if (_guider.IsConnected && _guider.IsGuiding)
            {
                Report("Flats", "Stopping guiding for the flats");
                await _guider.StopAsync(ct);
            }
            Report("Flats", $"Flats for {name}");
            try
            {
                await _flats!.TakeAndWaitAsync(name, _flats.Settings, waitLimit, message => Report("Flats", message), ct, leaveCovered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Flats that go wrong shouldn't cost the rest of the night's lights.
                _log.Error($"Flats for {name} failed; carrying on", ex);
            }
            state.Applied = default; // the flats changed the frame type
        }

        try
        {
            if (sequence.Start.CoolCamera) await CoolAsync(sequence.Start.CoolTo, Report, ct);

            for (current = 0; current < parts.Count; current++)
            {
                var part = parts[current];
                if (current > 0) states[current].TakeOver(state);
                state = states[current];
                _skipTargetRequested = false;

                // Wait for the target's start time and for it to be high enough, or skip it if it won't be.
                if (await WaitForTargetAsync(plans[current], Name(current), state, Report, ct) is not { } window)
                {
                    targetState[current] = "Skipped";
                    notes[current] = _skipNote;
                    continue;
                }
                targetState[current] = "Running";
                if (plans.Count > 1) _log.Info($"Target {current + 1} of {plans.Count}: {Name(current)}");

                try
                {
                    await TargetStartAsync(part, state, Name(current), Report, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && plans.Count > 1)
                {
                    // One target that can't be reached shouldn't cost the rest of the night.
                    targetState[current] = "Skipped";
                    notes[current] = ex.Message;
                    _log.Error($"Skipping {Name(current)}", ex);
                    Notifications?.Notify(NotificationKind.TargetSkipped, $"Skipped {Name(current)}",
                        $"{ex.Message}\nThe sequence carries on with the next target.");
                    continue;
                }

                while (NextRow(part, state) is { } rowIndex)
                {
                    await PausePointAsync(Report, ct);
                    if (_skipTargetRequested)
                    {
                        _skipTargetRequested = false;
                        targetState[current] = "Skipped";
                        notes[current] = "Skipped by you";
                        _log.Info($"Skipped the rest of {Name(current)}");
                        break;
                    }
                    if (_skipRequested)
                    {
                        // Skip the row in progress (the one the last frame came from), then pick again.
                        _skipRequested = false;
                        if (state.Current is { } row && !state.Skipped[row])
                        {
                            state.Skipped[row] = true;
                            _log.Info($"Skipped the rest of row {row + 1}");
                            continue;
                        }
                    }
                    if (StopReason(plans[current], part.Rows[rowIndex], state, window) is { } why)
                    {
                        targetState[current] = "Stopped";
                        notes[current] = why;
                        _log.Info($"{Name(current)}: {why}; moving on");
                        break;
                    }
                    state.Current = rowIndex;
                    await FrameAsync(part, rowIndex, state, Report, ct);
                }
                if (targetState[current] == "Running") targetState[current] = "Done";
                state.Current = null;

                if (sequence.FlatsSettings.Enabled && state.Done.Sum() > 0)
                {
                    flatsFor.Add(current);
                    if (CameraTurnsAfter(sequence, plans, current)) await FlatsAsync(FlatsWait);
                }
            }
            current = Math.Min(current, parts.Count - 1);

            var endSteps = _liveEnd ?? sequence.EndSettings;
            await EndStepsAsync(endSteps, Report, ct,
                beforeWarming: sequence.FlatsSettings.Enabled ? () => FlatsAsync(waitLimit: null, leaveCovered: endSteps.Park) : null);
            Report("Done", $"Finished: {FramesDone()} frames");
            Status = Status! with { State = "Done", FinishedUtc = DateTime.UtcNow, CurrentRow = null, PauseRequested = false };
            _log.Info($"Sequence '{sequence.Name}' finished: {FramesDone()} frames, {state.Autofocuses} autofocus runs, {state.Dithers} dithers");
            Notifications?.Notify(NotificationKind.SequenceDone, $"Sequence '{sequence.Name}' finished", NightSummary());
        }
        catch (OperationCanceledException)
        {
            Status = Status! with { State = "Aborted", Message = "Aborted", FinishedUtc = DateTime.UtcNow, PauseRequested = false };
            _log.Warn($"Sequence '{sequence.Name}' aborted after {FramesDone()} frames");
        }
        catch (Exception ex)
        {
            Status = Status! with { State = "Failed", Message = ex.Message, FinishedUtc = DateTime.UtcNow, PauseRequested = false };
            _log.Error($"Sequence '{sequence.Name}' stopped", ex);
            Notifications?.Notify(NotificationKind.SequenceFailed, $"Sequence '{sequence.Name}' stopped: {ex.Message}", NightSummary());
        }
        finally
        {
            _resume = null;
        }
    }

    /// <summary>A target's start: centre on it (guiding stopped while the mount moves), focus, guide.</summary>
    private async Task TargetStartAsync(Sequence part, RunState state, string name, Action<string, string> report, CancellationToken ct)
    {
        var start = part.Start;
        // A flip-flat closed over the telescope (after flats, or from last night): open it, or every frame is dark.
        if (_equipment.FlatPanel is { IsConnected: true, HasCover: true, CoverClosed: true } panel)
        {
            report("Cover", $"Opening {panel.Name}'s cover");
            await panel.SetLightAsync(false, null, ct);
            await panel.SetCoverAsync(false, ct);
        }
        if (start.CenterTarget && part.Target is { } target)
        {
            if (_guider.IsConnected && _guider.IsGuiding)
            {
                report("Guiding", "Stopping guiding to move to the next target");
                await _guider.StopAsync(ct);
            }
            _sites.EnsureAwayFromSun(target.Coordinates, target.Name);
            report("Centring", $"Slewing to {target.Name} and centring");
            await _solver.CenterAndWaitAsync(target.Coordinates, ct);

            if (start.MatchRotation && target.Rotation is { } angle)
            {
                report("Rotation", $"Checking the camera's rotation for {target.Name}");
                await _solver.MatchRotationAndWaitAsync(target.Coordinates, angle, RotationWait, message => report("Rotation", message), ct);
            }
        }

        // Focus (and guide) with the first row's filter in place.
        if (NextRow(part, state) is { } first && part.Rows[first].Filter is { } firstFilter)
            await SetFilterAsync(firstFilter, state, report, ct);

        if (start.Autofocus) await AutofocusAsync(state, $"start of {name}", report, ct);

        if (start.StartGuiding && !_guider.IsGuiding) await StartGuidingAsync(part, report, ct);
    }

    private string? _skipNote;
    private (DateTime Utc, string Label)? _nightEnd;

    /// <summary>
    /// Before a target: wait for its start time and until it's high enough. Returns its stop time (UTC, or
    /// <see cref="DateTime.MaxValue"/>), or null to skip it (its window has passed, it won't get high enough
    /// in time, or Skip target was pressed) — with the reason in <see cref="_skipNote"/>.
    /// </summary>
    private async Task<DateTime?> WaitForTargetAsync(SequenceTargetPlan plan, string name, RunState state,
        Action<string, string> report, CancellationToken ct)
    {
        var nowLocal = DateTime.Now;
        var startAfter = SequenceTargetPlan.TryParseClock(plan.StartAfter, out var a) ? SequenceTargetPlan.ClockToUtc(a, nowLocal) : (DateTime?)null;
        var stop = SequenceTargetPlan.TryParseClock(plan.StopAt, out var b) ? SequenceTargetPlan.ClockToUtc(b, nowLocal) : DateTime.MaxValue;
        // The night's end applies to every target, when it comes before the target's own stop time.
        var byNightEnd = _nightEnd is { } end && end.Utc < stop;
        if (byNightEnd) stop = _nightEnd!.Value.Utc;
        var until = byNightEnd ? _nightEnd!.Value.Label : plan.StopAt;
        if (stop <= DateTime.UtcNow)
        {
            _skipNote = byNightEnd ? $"{until} had come" : $"its stop time ({plan.StopAt}) had already passed";
            _log.Warn($"Skipping {name}: {_skipNote}");
            return null;
        }

        var waitUntil = startAfter is { } s && s > DateTime.UtcNow ? s : DateTime.UtcNow;
        var why = startAfter > DateTime.UtcNow ? $"its start time, {plan.StartAfter}" : null;

        // High enough: the minimum altitude if set, else above the horizon. Needs the site and coordinates.
        if (plan.Target is { } target && Site() is { } site)
        {
            // Never below the Mount panel's lower limit, nor above its upper one (the camera can reach the tripod).
            var lowest = Math.Max(plan.MinAltitude ?? 0, _sites.LowerLimit);
            var highest = _sites.UpperLimit;
            var limit = stop < DateTime.UtcNow.AddHours(24) ? stop : DateTime.UtcNow.AddHours(24);
            DateTime? up = null;
            for (var t = waitUntil; t < limit; t = t.AddMinutes(2))
                if (Horizon.Altitude(target.Coordinates, t, site) is var altitude && altitude >= lowest && altitude <= highest) { up = t; break; }
            if (up is null)
            {
                _skipNote = highest < 90
                    ? $"it isn't between {lowest:0}° and {highest:0}° {(stop == DateTime.MaxValue ? "in the next 24 hours" : $"before {until}")}"
                    : $"it doesn't get above {lowest:0}° {(stop == DateTime.MaxValue ? "in the next 24 hours" : $"before {until}")}";
                _log.Warn($"Skipping {name}: {_skipNote}");
                return null;
            }
            if (up > waitUntil)
            {
                why = Horizon.Altitude(target.Coordinates, waitUntil, site) > highest
                    ? $"it to come down below the upper limit of {highest:0}°"
                    : $"it to rise above {lowest:0}°";
                waitUntil = up.Value;
            }
        }
        else if (plan.MinAltitude is not null && plan.Target is not null && !state.WarnedNoSite)
        {
            _log.Warn("Minimum altitudes are ignored: set your site (latitude and longitude) in the Mount panel");
            state.WarnedNoSite = true;
        }

        while (waitUntil - DateTime.UtcNow is { TotalSeconds: > 0 } left)
        {
            await PausePointAsync(report, ct);
            if (_skipTargetRequested)
            {
                _skipTargetRequested = false;
                _skipNote = "Skipped by you";
                _log.Info($"Skipped {name}");
                return null;
            }
            report("Waiting", $"Waiting for {why} for {name} ({waitUntil.ToLocalTime():HH:mm}, in {Describe(left)})");
            await Task.Delay(left < TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5), ct);
        }
        return stop;
    }

    /// <summary>Why this target should end before the next frame: its stop time, or it has sunk too low.</summary>
    private string? StopReason(SequenceTargetPlan plan, SequenceRow row, RunState state, DateTime stop)
    {
        if (stop != DateTime.MaxValue && DateTime.UtcNow.AddSeconds(row.ExposureSeconds + state.OverheadSeconds) > stop)
            return _nightEnd is { } end && end.Utc == stop
                ? $"{end.Label}: no time for another frame"
                : $"stop time {plan.StopAt} reached";
        if (row.FrameType == FrameType.Light && plan.Target is { } target && Site() is { } site)
        {
            var altitude = Horizon.Altitude(target.Coordinates, DateTime.UtcNow, site);
            var lowest = Math.Max(plan.MinAltitude ?? 0, _sites.LowerLimit);
            if (altitude < lowest) return lowest <= 0 ? "it set below the horizon" : $"it sank below {lowest:0}°";
            if (altitude > _sites.UpperLimit) return $"it rose above the upper limit of {_sites.UpperLimit:0}°";
        }
        return null;
    }

    private async Task FrameAsync(Sequence sequence, int rowIndex, RunState state, Action<string, string> report, CancellationToken ct)
    {
        var row = sequence.Rows[rowIndex];
        var label = $"{row.Filter ?? row.FrameType.ToString()} {row.ExposureSeconds:0.##}s";
        var number = $"{state.Done[rowIndex] + 1}/{state.Count[rowIndex]}";

        if (row.FrameType == FrameType.Light) await MeridianAsync(sequence, row, state, report, ct);

        if (row.Filter is { } filter && filter != state.Filter) await SetFilterAsync(filter, state, report, ct);
        await ApplyCameraAsync(row, state, ct);

        if (AutofocusReason(_liveAutofocus ?? sequence.AutofocusSettings, row, state) is { } reason)
        {
            await AutofocusAsync(state, reason, report, ct);
            if (sequence.Start.StartGuiding && !_guider.IsGuiding) await StartGuidingAsync(sequence, report, ct);
        }

        if (sequence.Start.StartGuiding && _guider.IsConnected && !_guider.IsGuiding)
        {
            _log.Warn("Guiding isn't running: restarting it");
            await StartGuidingAsync(sequence, report, ct);
        }

        var dither = sequence.DitherSettings;
        if (dither.EveryFrames > 0 && state.FramesSinceDither >= dither.EveryFrames && _guider.IsGuiding)
        {
            report("Dithering", $"Dithering {dither.Pixels:0.#} px before {label} {number}");
            try
            {
                await _guider.DitherAndWaitAsync(dither.Pixels, dither.RaOnly, Settle(dither), ct);
                state.Dithers++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"Dither didn't settle ({ex.Message}); carrying on");
            }
            state.FramesSinceDither = 0;
        }

        report("Exposing", $"{label} {number}");
        var started = DateTime.UtcNow;
        try
        {
            // Numbered through the target's frames (each target has its own folder): …_0024.fits is its 24th.
            var image = await _imaging.CaptureAsync(row.ExposureSeconds, save: true, ct, sequence.Target?.Name,
                frameNumber: state.Done.Sum() + 1);
            state.ConsecutiveFailures = 0;
            state.Done[rowIndex]++;
            state.FramesSinceDither++;
            state.FramesSinceAutofocus++;
            state.LastFilter = row.Filter;
            if (image.Stars.MedianHfr is { } hfr)
            {
                state.LastHfr = hfr;
                state.HfrBaseline.TryAdd(row.Filter ?? "", hfr);
            }
            var overhead = (DateTime.UtcNow - started).TotalSeconds - row.ExposureSeconds;
            if (overhead > 0) state.OverheadSeconds = state.OverheadSeconds * 0.7 + overhead * 0.3;
            report("Exposing", $"Saved {label} {number}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Failures++;
            if (++state.ConsecutiveFailures >= MaxConsecutiveFailures)
                throw new InvalidOperationException($"{MaxConsecutiveFailures} frames in a row failed; last: {ex.Message}");
            _log.Warn($"Frame {label} {number} failed ({ex.Message}); trying again");
        }
    }

    /// <summary>
    /// Before a light frame: flip now if the target is far enough past the meridian, or wait for the flip if
    /// the frame would run past the latest time; otherwise carry on.
    /// </summary>
    private async Task MeridianAsync(Sequence sequence, SequenceRow row, RunState state, Action<string, string> report, CancellationToken ct)
    {
        var flip = sequence.MeridianFlipSettings;
        (state.FlipDueUtc, state.FlipLatestUtc, state.FlipNote) = (null, null, null);
        if (!flip.Enabled || _equipment.Mount is not { IsConnected: true } mount || mount.State == MountState.Parked) return;
        if (Site() is not { } site)
        {
            if (!state.WarnedNoSite) _log.Warn("Meridian flip off: set your site (latitude and longitude) so NOVA knows where the meridian is");
            state.WarnedNoSite = true;
            return;
        }

        // A mount that stopped tracking (ZWO's "stop after the meridian") stays at one hour angle, so the flip would
        // never come due: put tracking back on, and time the flip by the target, which moves on with the sky.
        if (mount.State == MountState.Idle)
        {
            _log.Warn($"{mount.Name} isn't tracking (mounts can stop by themselves at the meridian): turning tracking back on");
            await mount.StartTrackingAsync(ct);
        }
        var aim = sequence.Target?.Coordinates is { } target ? Precession.J2000ToJNow(target, DateTime.UtcNow) : mount.Coordinates;
        var hourAngle = Horizon.HourAngle(aim, DateTime.UtcNow, site.Longitude);
        if (hourAngle < 0) state.SeenEast = true;
        var due = mount.SideOfPier is { } side ? side == PierSide.West : state.SeenEast && !state.Flipped;
        if (!due || hourAngle is < -6 or > 6) return;

        var minutesPast = Horizon.SiderealToClock(hourAngle).TotalMinutes;
        var flipAt = DateTime.UtcNow.AddMinutes(flip.MinutesAfterMeridian - minutesPast);
        if (state.FlipRetryUtc is { } retry && retry > flipAt) flipAt = retry;
        var latest = DateTime.UtcNow.AddMinutes(flip.MaxMinutesAfterMeridian - minutesPast);
        (state.FlipDueUtc, state.FlipLatestUtc) = (flipAt, latest);

        if (flipAt > DateTime.UtcNow)
        {
            // Not time yet: take the frame if it ends before the latest time, else wait for the flip. Flips happen between
            // frames, so a frame that runs past the flip time puts the flip off until it ends.
            var frameEnds = DateTime.UtcNow.AddSeconds(row.ExposureSeconds + state.OverheadSeconds);
            if (frameEnds <= latest)
            {
                if (frameEnds >= flipAt)
                    (state.FlipDueUtc, state.FlipNote) = (frameEnds, $"after this frame (due {Clock(flipAt)}; at the latest {Clock(latest)})");
                else state.FlipNote = $"after the frame running at {Clock(flipAt)} (at the latest {Clock(latest)})";
                WarnOfFlip(state, report);
                return;
            }
            state.FlipNote = $"waiting for it rather than start a frame that would end after {Clock(latest)}";
            WarnOfFlip(state, report);
            while (flipAt - DateTime.UtcNow is { TotalSeconds: > 0 } wait)
            {
                report("Meridian flip", $"Waiting {Describe(wait)} for the meridian flip (the next frame would run past the limit)");
                await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
            }
        }
        await FlipAsync(sequence, mount, state, report, ct);
    }

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    /// <summary>Puts the coming flip in the log about 5 minutes ahead (once per flip), so whoever is watching knows.</summary>
    private void WarnOfFlip(RunState state, Action<string, string> report)
    {
        if (state.FlipDueUtc is not { } due || state.FlipWarnedFor is { } warned && Math.Abs((warned - due).TotalMinutes) < 2) return;
        var lead = due - DateTime.UtcNow - FlipWarning;
        if (lead > TimeSpan.FromMinutes(30)) return; // checked again before each frame
        state.FlipWarnedFor = due;
        var flips = state.Flips;
        _ = Task.Run(async () =>
        {
            if (lead > TimeSpan.Zero) await Task.Delay(lead);
            // Still coming (not already flipped, the sequence still running)?
            if (!Running || state.Flips != flips || state.FlipDueUtc is not { } now) return;
            var minutes = Math.Max(1, (int)Math.Round((now - DateTime.UtcNow).TotalMinutes));
            _log.Warn($"Meridian flip in about {minutes} min (≈{Clock(now)}, {state.FlipNote}): guiding stops and the mount slews to the other side of the pier");
            Notifications?.Notify(NotificationKind.MeridianFlip, $"Meridian flip in about {minutes} min",
                $"At about {Clock(now)}, {state.FlipNote}. Guiding stops and the mount slews to the other side of the pier.");
        });
    }

    private static readonly TimeSpan FlipWarning = TimeSpan.FromMinutes(5);

    private async Task FlipAsync(Sequence sequence, IMount mount, RunState state, Action<string, string> report, CancellationToken ct)
    {
        var flip = sequence.MeridianFlipSettings;
        var wasGuiding = _guider.IsGuiding;
        if (_guider.IsConnected)
        {
            report("Meridian flip", "Meridian flip: stopping guiding");
            await _guider.StopAsync(ct);
        }

        var target = sequence.Target?.Coordinates ?? Precession.JNowToJ2000(mount.Coordinates, DateTime.UtcNow);
        var before = mount.SideOfPier;
        _sites.EnsureAwayFromSun(target, "The flip's target");
        report("Meridian flip", "Meridian flip: slewing to the other side of the pier");
        _log.Info($"Meridian flip{(before is null ? "" : $" from the {before} side of the pier")}");
        await _sites.SlewSafelyAsync(mount, Precession.J2000ToJNow(target, DateTime.UtcNow), ct);

        if (before is not null && mount.SideOfPier == before)
        {
            // Some mounts keep tracking past the meridian and only flip once they're further past.
            if (++state.FlipAttempts >= MaxFlipAttempts)
                throw new InvalidOperationException(
                    $"The mount didn't flip after {MaxFlipAttempts} tries (still on the {before} side of the pier), so the sequence " +
                    "stopped before it could reach the mount's limit. Check the mount's meridian settings.");
            state.FlipRetryUtc = DateTime.UtcNow + FlipRetryDelay;
            _log.Warn($"The mount stayed on the {before} side of the pier; trying the flip again in {FlipRetryDelay.TotalMinutes:0} minutes");
        }
        else
        {
            state.Flipped = true;
            state.Flips++;
            state.FlipAttempts = 0;
            state.FlipRetryUtc = null;
            (state.FlipDueUtc, state.FlipLatestUtc, state.FlipNote) = (null, null, null);
        }

        // The flip's settle counts from the end of the slew, which has already settled for the general time.
        var settle = flip.SettleSeconds - _sites.SettleSeconds;
        if (settle > 0) await Task.Delay(TimeSpan.FromSeconds(settle), ct);
        if (flip.Recenter && _equipment.Camera is { IsConnected: true } && _solver.Solver.Available)
        {
            report("Meridian flip", "Re-centring after the flip");
            try
            {
                await _solver.CenterAndWaitAsync(target, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"Re-centring after the flip failed ({ex.Message}); carrying on");
            }
        }
        if (flip.AutofocusAfter) await AutofocusAsync(state, "after the meridian flip", report, ct);
        if (sequence.Start.StartGuiding || wasGuiding) await StartGuidingAsync(sequence, report, ct);
        state.FramesSinceDither = 0;
        if (state.Flipped) _log.Info($"Meridian flip done: now on the {mount.SideOfPier?.ToString() ?? "other"} side of the pier");
    }

    private GeoLocation? Site() => _equipment.Mount?.Location ?? _sites.ActiveSite?.Location;

    /// <summary>
    /// NOVA flips the mount itself (between frames), so the mount must keep tracking past the meridian until at
    /// least NOVA's latest flip time (plus room for a retry), and not flip by itself.
    /// </summary>
    private async Task PrepareMountForFlipsAsync(Sequence sequence, CancellationToken ct)
    {
        var flip = sequence.MeridianFlipSettings;
        if (!flip.Enabled || _equipment.Mount is not { IsConnected: true } mount) return;
        var degrees = Math.Min(15, Math.Max(5, (flip.MaxMinutesAfterMeridian + MaxFlipAttempts * FlipRetryDelay.TotalMinutes) / 4 + 2));
        try
        {
            if (await mount.PrepareForMeridianFlipsAsync(degrees, ct) is { } changed)
                _log.Info($"{mount.Name}'s meridian settings changed so NOVA can do the flips: {changed}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Couldn't check {mount.Name}'s meridian settings ({ex.Message}): make sure it keeps tracking past the meridian");
        }
    }

    private static string Describe(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    /// <summary>Why to refocus before the next frame, or null.</summary>
    private string? AutofocusReason(SequenceAutofocus triggers, SequenceRow row, RunState state)
    {
        if (row.FrameType != FrameType.Light) return null;
        if (_equipment.Focuser is not { IsConnected: true, CanMoveAbsolute: true } focuser) return null;
        if (triggers.AfterFilterChange && state.LastFilter is not null && row.Filter != state.LastFilter)
            return $"filter changed to {row.Filter}";
        if (triggers.TemperatureChange is > 0 and var dt && focuser.Temperature is { } now && state.AutofocusTemperature is { } then
            && Math.Abs(now - then) >= dt)
            return $"temperature moved {now - then:+0.0;-0.0} °C";
        if (triggers.HfrIncreasePercent is > 0 and var pct && state.LastHfr is { } last && row.Filter == state.LastFilter
            && state.HfrBaseline.TryGetValue(row.Filter ?? "", out var baseline) && last > baseline * (1 + pct / 100))
            return $"HFR {last:0.00} is {(last / baseline - 1) * 100:0}% above {baseline:0.00}";
        if (triggers.EveryFrames is > 0 and var n && state.FramesSinceAutofocus >= n) return $"{n} frames since the last autofocus";
        return null;
    }

    private async Task AutofocusAsync(RunState state, string reason, Action<string, string> report, CancellationToken ct)
    {
        if (_equipment.Focuser is not { IsConnected: true, CanMoveAbsolute: true } focuser)
        {
            _log.Warn("Autofocus skipped: no absolute focuser connected");
            return;
        }
        report("Autofocus", $"Autofocus ({reason})");
        try
        {
            var result = await _autofocus.RunAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (result.Succeeded == true) state.Autofocuses++;
            else _log.Warn($"Autofocus didn't succeed ({result.Message}); carrying on");
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn($"Autofocus couldn't run ({ex.Message}); carrying on");
        }
        // Either way, start the triggers afresh so a failure doesn't refocus on every frame.
        state.AutofocusTemperature = focuser.Temperature;
        state.FramesSinceAutofocus = 0;
        state.HfrBaseline.Clear();
    }

    private async Task StartGuidingAsync(Sequence sequence, Action<string, string> report, CancellationToken ct)
    {
        if (!_guider.IsConnected)
        {
            _log.Warn("Guiding skipped: PHD2 isn't connected");
            return;
        }
        report("Guiding", "Starting guiding");
        try
        {
            await _guider.GuideAndWaitAsync(Settle(sequence.DitherSettings), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Guiding didn't start ({ex.Message}); carrying on unguided");
        }
    }

    private async Task SetFilterAsync(string filter, RunState state, Action<string, string> report, CancellationToken ct)
    {
        var wheel = _equipment.FilterWheel ?? throw new InvalidOperationException("The filter wheel went away");
        var slot = SlotOf(wheel, filter) ?? throw new InvalidOperationException($"The filter wheel has no filter called '{filter}'");
        if (wheel.Position != slot)
        {
            report("Filter", $"Changing to {filter}");
            if (FocusOffsets is { } offsets) await offsets.ChangeFilterAsync(wheel, slot, ct);
            else await wheel.MoveToAsync(slot, ct);
        }
        state.Filter = filter;
    }

    private async Task ApplyCameraAsync(SequenceRow row, RunState state, CancellationToken ct)
    {
        var wanted = (row.Gain, row.Offset, row.Binning, (FrameType?)row.FrameType);
        if (wanted == state.Applied) return;
        var camera = _equipment.Camera ?? throw new InvalidOperationException("The camera went away");
        var controls = camera.Controls;
        await camera.ApplyAsync(new CameraSettingsUpdate(
            Gain: controls.Gain is null ? null : row.Gain,
            Offset: controls.Offset is null ? null : row.Offset,
            Binning: controls.Binning is null ? null : row.Binning,
            FrameType: controls.FrameType is null ? null : row.FrameType), ct);
        state.Applied = wanted;
    }

    private async Task CoolAsync(double target, Action<string, string> report, CancellationToken ct)
    {
        var camera = _equipment.Camera!;
        if (camera.Controls.Cooler is not { CanCool: true })
        {
            _log.Info("Camera has no cooler: skipping cooling");
            return;
        }
        report("Cooling", $"Cooling to {target:0.#} °C");
        await camera.ApplyAsync(new CameraSettingsUpdate(CoolerOn: true, TargetTemperature: target), ct);
        var deadline = DateTime.UtcNow + CoolingTimeout;
        while (true)
        {
            var now = camera.Controls.Cooler?.Temperature;
            if (now is { } t && Math.Abs(t - target) <= 1) break;
            if (DateTime.UtcNow > deadline)
            {
                _log.Warn($"Cooling: still at {now:0.#} °C after {CoolingTimeout.TotalMinutes:0} minutes; starting anyway");
                break;
            }
            report("Cooling", $"Cooling to {target:0.#} °C (now {now:0.#} °C)");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// Whether the camera will be turned for the target after <paramref name="current"/>: it has a rotation to match that
    /// differs from this one's (or this one had none, so its angle is anyone's guess).
    /// </summary>
    private static bool CameraTurnsAfter(Sequence sequence, IReadOnlyList<SequenceTargetPlan> plans, int current)
    {
        if (current + 1 >= plans.Count || !sequence.Start.CenterTarget || !sequence.Start.MatchRotation) return false;
        if (plans[current + 1].Target?.Rotation is not { } next) return false;
        return plans[current].Target?.Rotation is not { } now
               || Math.Abs(CameraAngle.Turn(next, now).Degrees) > PlateSolveService.RotationTolerance;
    }

    private async Task EndStepsAsync(SequenceEnd end, Action<string, string> report, CancellationToken ct, Func<Task>? beforeWarming = null)
    {
        if (end.StopGuiding && _guider.IsConnected)
        {
            report("Ending", "Stopping guiding");
            await _guider.StopAsync(ct);
        }
        if (end.Park && _equipment.Mount is { IsConnected: true } mount)
        {
            report("Ending", "Parking the mount");
            await mount.ParkAsync(ct);
        }
        // Flats while the camera is still at its imaging temperature.
        if (beforeWarming is not null) await beforeWarming();
        // Parked for the night: a flip-flat's cover goes over the telescope too.
        if (end.Park && _equipment.FlatPanel is { IsConnected: true, HasCover: true, CoverClosed: false } panel)
        {
            report("Ending", $"Closing {panel.Name}'s cover");
            await panel.SetCoverAsync(true, ct);
        }
        if (end.WarmCamera && _equipment.Camera is { IsConnected: true } camera && camera.Controls.Cooler is { CanCool: true, On: true })
        {
            // Gradually (2 °C every 30 s), which is kinder to the sensor than switching off at -10 °C.
            var temperature = camera.Controls.Cooler.Temperature ?? 0;
            while (temperature < 15)
            {
                temperature += 2;
                report("Ending", $"Warming the camera ({temperature:0} °C)");
                await camera.ApplyAsync(new CameraSettingsUpdate(TargetTemperature: temperature), ct);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            await camera.ApplyAsync(new CameraSettingsUpdate(CoolerOn: false), ct);
            _log.Info("Camera warmed and cooler off");
        }
    }

    private async Task PausePointAsync(Action<string, string> report, CancellationToken ct)
    {
        if (!_pauseRequested) return;
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Status = Status! with { State = "Paused" };
        report("Paused", "Paused: press Resume to carry on");
        _log.Info("Sequence paused");
        await _resume.Task.WaitAsync(ct);
        _resume = null;
        Status = Status! with { State = "Running", PauseRequested = false };
        _log.Info("Sequence resumed");
    }

    /// <summary>The row the next frame comes from, or null when everything's done.</summary>
    private static int? NextRow(Sequence sequence, RunState state)
    {
        bool Open(int i) => sequence.Rows[i].Enabled && state.Done[i] < state.Count[i] && !state.Skipped[i];
        var rows = Enumerable.Range(0, sequence.Rows.Count);
        if (sequence.Order == SequenceOrder.ByRow) return rows.Where(Open).Select(i => (int?)i).FirstOrDefault();

        // Cycle: carry on round from the row after the last one used.
        var from = state.Current is { } c ? c + 1 : 0;
        return rows.Select(k => (from + k) % sequence.Rows.Count).Where(Open).Select(i => (int?)i).FirstOrDefault();
    }

    private static IReadOnlyList<SequenceRowProgress> Progress(Sequence sequence, RunState state) =>
        sequence.Rows.Select((r, i) => new SequenceRowProgress(state.Done[i], state.Count[i], state.Skipped[i])).ToList();

    /// <summary>When the frames should be done: this and later targets' remaining frames (not counting waits).</summary>
    private static DateTime Eta(List<Sequence> parts, RunState[] states, string[] targetState, int current)
    {
        var overhead = states[current].OverheadSeconds;
        var remaining = 0.0;
        for (var k = current; k < parts.Count; k++)
        {
            if (targetState[k] is "Skipped" or "Stopped" or "Done") continue;
            remaining += parts[k].Rows.Select((r, i) => r.Enabled && !states[k].Skipped[i]
                ? Math.Max(0, states[k].Count[i] - states[k].Done[i]) * (r.ExposureSeconds + overhead)
                : 0).Sum();
        }
        return DateTime.UtcNow.AddSeconds(remaining);
    }

    private static SettleSettings Settle(SequenceDither d) => new(d.SettlePixels, d.SettleSeconds, d.SettleTimeout);

    private static int? SlotOf(IFilterWheel wheel, string filter)
    {
        for (var i = 0; i < wheel.Names.Count; i++)
            if (string.Equals(wheel.Names[i], filter, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return null;
    }
}
