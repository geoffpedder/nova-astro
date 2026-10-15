using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.Imaging;

namespace Nova.Server.Services;

/// <summary>NOVA's own guider settings (saved in guider.json).</summary>
/// <param name="Camera">The guide camera; null = the first camera that isn't the imaging camera.</param>
/// <param name="Output">Where guide pulses go: null = the mount, otherwise a camera's ST4 port (by camera name).</param>
/// <param name="CalibrationStepMs">Each calibration pulse (ms); 0 = worked out from the image scale and guide rate.</param>
/// <param name="ReverseDecAfterFlip">For mounts whose Dec pulses change sense after a meridian flip.</param>
/// <param name="UseDarks">Subtract the guide camera's dark library (when it has one) from every guide frame.</param>
/// <param name="Gain">The guide camera's gain, set before every frame; null leaves it as the driver has it.</param>
/// <param name="GuideLog">Write a guide log (PHD2's format) into each night's image folder.</param>
public sealed record NovaGuiderSettings(
    string? Camera = null, string? Output = null, double ExposureSeconds = 2, int Binning = 1, int CalibrationStepMs = 0,
    bool ReverseDecAfterFlip = false, GuideAlgorithmSettings? Algorithms = null, bool UseDarks = true, double? Gain = null, bool GuideLog = true)
{
    public GuideAlgorithmSettings Algorithms { get; init; } = Algorithms ?? new GuideAlgorithmSettings();
}

/// <summary>A guide log file: which night, where it is (the mini PC or the drive), its size and when it last grew.</summary>
public sealed record GuideLogFile(string Name, string Night, bool OnDrive, long Bytes, DateTime ModifiedUtc);

/// <summary>A guide output to choose from: <c>Name</c> null is the mount.</summary>
public sealed record GuideOutputChoice(string? Name, string Label);

/// <summary>The calibration for display: angles in degrees, rates in px/s (and ″/s when the scale is known).</summary>
public sealed record GuideCalibrationInfo(
    double RaAngle, double DecAngle, double RaRate, double DecRate, double? RaArcsecPerSecond, double? DecArcsecPerSecond,
    double Orthogonality, double DecDegrees, PierSide? PierSide, DateTime Utc, string? Warning,
    IReadOnlyList<CalibrationPoint>? Points = null, double DecBacklashPixels = 0, int DecBacklashMs = 0);

/// <summary>Dec backlash compensation now: what calibration measured, the pulse it adds on a reversal now (adjusted as it
/// guides), and how many reversals it has compensated.</summary>
public sealed record DecBacklashStatus(bool Enabled, double MeasuredPixels, int MeasuredMs, int PulseMs, int Applied);

/// <summary>The guide star now: where it is, where it should be (lock), and how well it's seen.</summary>
public sealed record GuideStarInfo(double X, double Y, double LockX, double LockY, double Snr, double Hfr, int Found, int Tracked);

/// <summary>A tracked star in the guide frame (pixels): the lock star or a reference, and whether the last frame found it.</summary>
public sealed record GuideStarMark(double X, double Y, bool Lock, bool Found);

/// <param name="Activity">What it's doing in words ("West step 4", "Lost the guide star"), or null when idle.</param>
/// <param name="CameraGain">The guide camera's gain now and its range, when it's connected and has one.</param>
/// <param name="Frame">Increments with every guide frame, so the browser knows when to fetch frame.png.</param>
public sealed record NovaGuiderStatus(
    NovaGuiderSettings Settings, IReadOnlyList<string> Cameras, IReadOnlyList<GuideOutputChoice> Outputs,
    string? Camera, string? Activity, GuideCalibrationInfo? Calibration, GuideStarInfo? Star,
    int Frame, int? FrameWidth, int? FrameHeight, IReadOnlyList<GuideStarMark> Stars,
    GuideDarkInfo? Darks = null, string? DarkProgress = null, NumberSetting? CameraGain = null,
    GuidingAssistantResult? Assistant = null, bool CameraConnected = false,
    IReadOnlyList<GuideProfileInfo>? Profiles = null, string? Profile = null, GuideDefaults? Defaults = null,
    PecStatus? Pec = null, DecBacklashStatus? Backlash = null);

/// <summary>A guide settings profile to choose from: the user's own (everything, equipment included) or built in.</summary>
public sealed record GuideProfileInfo(string Name, bool BuiltIn, string? Description);

/// <summary>The guide camera's dark library (at the current binning).</summary>
/// <param name="GainMatches">Taken at the camera's gain now (darks at another gain aren't used).</param>
/// <param name="InUse">Being subtracted from the guide frames.</param>
public sealed record GuideDarkInfo(
    IReadOnlyList<double> Exposures, int Frames, DateTime Utc, double? Gain, bool GainMatches, int HotPixels, bool InUse);

/// <summary>
/// NOVA's own guider: exposes the guide camera in a loop, follows the guide stars, calibrates, and sends corrections
/// as guide pulses to the mount (or a camera's ST4 port). The algorithms live in Nova.Core/Guiding; this runs them
/// against the equipment and keeps the state, history and settings.
/// </summary>
public sealed class NovaGuider : IAsyncDisposable
{
    private const int HistoryLength = 300; // 10 minutes of 2 s frames, for the Guiding tab's graph
    private const int LostFramesBeforeReselect = 3;

    private readonly EquipmentService _equipment;
    private readonly OpticsService? _optics;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly string _darkFolder;
    private readonly string _localImages;
    private readonly GuideLog _guideLog;
    private string? _guideLogPath;
    private readonly Dictionary<string, RawImage> _darkCache = new(StringComparer.Ordinal);
    private string? _darkProgress;
    private readonly object _sync = new();
    private readonly GuideStarTracker _tracker = new();
    private readonly GuideController _controller = new();
    private readonly DecDriftMonitor _drift = new();
    private readonly GuideHistory _history = new(HistoryLength);
    private readonly Random _dither = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private GuiderFile _saved;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private Mode _mode = Mode.Looping;
    private GuideCalibrator? _calibrator;
    private TaskCompletionSource? _calibrationDone;
    private GuidingAssistant? _assistant;
    private DateTime _assistantStart;
    private double _assistantSeconds;
    private TaskCompletionSource? _assistantDone;
    private SettleTracker? _settle;
    private TaskCompletionSource? _settled;
    private bool _rehome;
    private bool _driftReported;
    private int _lostFrames;
    private string? _activity;
    private GuideMeasurement? _last;
    private RawImage? _frame;
    private IReadOnlyList<DetectedStar> _frameStars = [];
    private int _frameId;
    private double? _scale;

    private enum Mode { Looping, Calibrating, Guiding, Assistant }

    public NovaGuider(EquipmentService equipment, OpticsService? optics, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _optics = optics;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "guider.json");
        _darkFolder = Path.Combine(options.Value.ResolveConfigDirectory(), "guide-darks");
        _localImages = options.Value.ResolveImageDirectory();
        _saved = Load();
        _controller.Settings = _saved.Nova.Algorithms;
        _guideLog = new GuideLog(() => Settings.GuideLog ? _guideLogPath : null);
        _guideLog.Problem += problem =>
        {
            // The drive was pulled out (or filled up): carry on in the mini PC's image folder.
            var local = LogPathIn(_localImages, DateTime.UtcNow);
            if (_guideLogPath != local)
            {
                _guideLogPath = local;
                _log.Warn($"{problem}. The guide log carries on in {local}");
            }
            else _log.Warn(problem);
        };
        equipment.DevicesChanged += (_, _) => ApplyMountDefaults();
        _controller.Pec.Restarted += reason =>
        {
            _guideLog.Info(reason);
            _log.Info(reason);
        };
    }

    /// <summary>Dither and settle defaults for the mount in use (smaller and tighter for ZWO's AM mounts).</summary>
    public GuideDefaults Defaults => GuidePresets.DefaultsFor(_equipment.Mount?.Name);

    /// <summary>
    /// The first time a ZWO AM mount shows up, sets the guide settings for it (the "ZWO AM mount" preset), unless they
    /// were already changed from NOVA's defaults: then they're left alone and the log points at the preset. Either way
    /// it turns on Reverse Dec after flip, which these mounts need (their Dec guide pulses change sense after a meridian
    /// flip); setups that had the AM defaults before that was added get it once too.
    /// </summary>
    private void ApplyMountDefaults()
    {
        var mount = _equipment.Mount?.Name;
        if (!GuidePresets.IsZwoAm(mount)) return;
        bool untouched, first, reversed;
        lock (_sync)
        {
            if (_saved.MountDefaults == "zwo-am-2") return;
            first = _saved.MountDefaults != "zwo-am";
            untouched = first && _saved.Nova with { Camera = null, Output = null } == new NovaGuiderSettings();
            reversed = !_saved.Nova.ReverseDecAfterFlip;
            _saved = _saved with { MountDefaults = "zwo-am-2", Nova = _saved.Nova with { ReverseDecAfterFlip = true } };
            if (untouched)
            {
                _saved = _saved with { Nova = WithPreset(GuidePresets.ZwoAm, _saved.Nova) };
                _controller.Settings = _saved.Nova.Algorithms;
            }
            SaveLocked();
        }
        if (first)
            _log.Info(untouched
                ? $"{mount}: guide settings set for a ZWO AM mount (1.5 s exposures, gain 60; dithers 2.5 px, settle within 0.7 px; Dec reversed after a flip)"
                : $"{mount}: the 'ZWO AM mount' guide profile suits it (your guide settings were left as they are, apart from Reverse Dec after flip, which it needs)");
        else if (reversed)
            _log.Info($"{mount}: Reverse Dec after flip turned on in NOVA's guide settings (ZWO AM mounts need it)");
    }

    private static NovaGuiderSettings WithPreset(GuidePreset preset, NovaGuiderSettings s) =>
        s with { ExposureSeconds = preset.ExposureSeconds, Gain = preset.Gain, Algorithms = preset.Algorithms };

    // ---- Profiles ----

    /// <summary>Saves the current settings (equipment included) under a name, replacing one of the same name.</summary>
    public void SaveProfile(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("A profile name is 1–40 characters");
        if (GuidePresets.All.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"'{name}' is a built-in profile's name");
        lock (_sync)
        {
            _saved = _saved with { Profiles = [.. _saved.Profiles.Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), new GuideProfile(name, Settings)] };
            SaveLocked();
        }
        _log.Info($"Guide profile '{name}' saved");
    }

    /// <summary>
    /// Switches to a profile: one of the user's (all its settings, equipment included) or a built-in one (its exposure,
    /// gain and algorithms; the equipment stays). Switching camera, output or binning needs the guide camera stopped.
    /// </summary>
    public void LoadProfile(string name)
    {
        NovaGuiderSettings next;
        GuideProfile? mine;
        lock (_sync) mine = _saved.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (mine is not null)
        {
            next = mine.Settings;
            if (IsRunning && (next.Camera != Settings.Camera || next.Output != Settings.Output || next.Binning != Settings.Binning))
                throw new InvalidOperationException("Stop the guide camera to switch to a profile with another camera, output or binning");
        }
        else
        {
            var preset = GuidePresets.All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no guide profile '{name}'");
            next = WithPreset(preset, Settings);
        }
        UpdateSettings(next);
        _log.Info($"Guide profile '{mine?.Name ?? name}' loaded");
    }

    public void DeleteProfile(string name)
    {
        lock (_sync)
        {
            if (!_saved.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There's no saved guide profile '{name}' (built-in ones can't be deleted)");
            _saved = _saved with { Profiles = _saved.Profiles.Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() };
            SaveLocked();
        }
        _log.Info($"Guide profile '{name}' deleted");
    }

    /// <summary>The profile the current settings are exactly: one of the user's, else a built-in one; null when changed since.</summary>
    private string? MatchingProfile()
    {
        var s = Settings;
        return _saved.Profiles.FirstOrDefault(p => p.Settings == s)?.Name
               ?? GuidePresets.All.FirstOrDefault(p => WithPreset(p, s) == s)?.Name;
    }

    /// <summary>Where frames go (the mini PC or a USB drive); guide logs go into the same night folders.</summary>
    public StorageService? Storage { get; set; }

    /// <summary>Which guider NOVA uses: "phd2" or "nova".</summary>
    public string Backend => _saved.Backend;

    public NovaGuiderSettings Settings => _saved.Nova;

    public GuiderState State { get; private set; } = GuiderState.Stopped;
    public bool IsSettling => _settle is not null;
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Arcseconds per guide pixel (from the guide camera's optics, else the frame's header).</summary>
    public double? PixelScale => _scale;
    public double? Snr => _last?.Snr;
    public double? Hfd => _last is { Hfr: > 0 } m ? m.Hfr * 2 : null;

    /// <summary>The guide camera to use now, or null when there's none.</summary>
    public ICamera? Camera
    {
        get
        {
            var name = Settings.Camera ?? _equipment.VisibleDevices
                .Where(d => d.Kinds.Contains(DeviceKind.Camera) && d.Name != _equipment.Camera?.Name)
                .Select(d => d.Name).FirstOrDefault();
            return name is null || !_equipment.VisibleDevices.Any(d => d.Name == name) ? null : _equipment.Provider.GetCamera(name);
        }
    }

    /// <summary>Whether guiding can start: there's a guide camera to use.</summary>
    public bool IsReady => Camera is not null;

    public void SetBackend(string backend)
    {
        var value = backend.Trim().ToLowerInvariant();
        if (value is not ("phd2" or "nova")) throw new ArgumentException("The guider is 'phd2' or 'nova'");
        lock (_sync) _saved = _saved with { Backend = value };
        Save();
    }

    public void UpdateSettings(NovaGuiderSettings settings)
    {
        if (settings.ExposureSeconds is < 0.05 or > 30) throw new ArgumentException("The guide exposure must be 0.05–30 s");
        if (settings.Binning is < 1 or > 4) throw new ArgumentException("Guide binning must be 1–4");
        if (settings.CalibrationStepMs is < 0 or > 10_000) throw new ArgumentException("The calibration step must be 0–10000 ms");
        if (settings.Gain is < 0 or > 10_000) throw new ArgumentException("The guide camera gain must be 0–10000");
        if (settings.Gain is { } gain && Camera is { IsConnected: true, Controls.Gain: { } range } && (gain < range.Min || gain > range.Max))
            throw new ArgumentException($"The guide camera's gain goes from {range.Min:0.##} to {range.Max:0.##}");
        var a = settings.Algorithms;
        if (a.RaAggressiveness is < 0 or > 2 || a.DecAggressiveness is < 0 or > 2 || a.RaHysteresis is < 0 or >= 1 ||
            a.RaMinMove < 0 || a.DecMinMove < 0 || a.MaxRaPulseMs is < 50 or > 10_000 || a.MaxDecPulseMs is < 50 or > 10_000)
            throw new ArgumentException("A guiding algorithm setting is out of range");
        if (a.PecPeriodSeconds != 0 && a.PecPeriodSeconds is < PeriodicErrorPredictor.MinPeriod or > PeriodicErrorPredictor.MaxPeriod)
            throw new ArgumentException($"The PEC period is 0 (find it) or {PeriodicErrorPredictor.MinPeriod:0}–{PeriodicErrorPredictor.MaxPeriod:0} s");
        if (a.PecPredictionGain is < 0 or > 1.5 || a.PecReactiveAggressiveness is < 0 or > 2)
            throw new ArgumentException("A Predictive PEC setting is out of range");
        lock (_sync)
        {
            foreach (var change in Changes(_saved.Nova, settings)) _guideLog.Info($"Guiding parameter change, {change}");
            _saved = _saved with { Nova = settings };
            _controller.Settings = settings.Algorithms;
        }
        Save();
    }

    /// <summary>What differs between two sets of settings, as "Name = value" for the guide log.</summary>
    private static IEnumerable<string> Changes(NovaGuiderSettings before, NovaGuiderSettings after)
    {
        var (a, b) = (before.Algorithms, after.Algorithms);
        (string Name, object? Old, object? New)[] fields =
        [
            ("Exposure", before.ExposureSeconds * 1000, after.ExposureSeconds * 1000), ("Gain", before.Gain, after.Gain),
            ("Use darks", before.UseDarks, after.UseDarks),
            ("RA Hysteresis", a.RaHysteresis, b.RaHysteresis), ("RA Aggression", a.RaAggressiveness, b.RaAggressiveness),
            ("RA Minimum move", a.RaMinMove, b.RaMinMove), ("Dec Aggression", a.DecAggressiveness, b.DecAggressiveness),
            ("Dec Minimum move", a.DecMinMove, b.DecMinMove), ("Dec FastSwitch", a.DecFastSwitch, b.DecFastSwitch),
            ("DEC guide mode", a.DecMode, b.DecMode), ("Max RA duration", a.MaxRaPulseMs, b.MaxRaPulseMs),
            ("Max DEC duration", a.MaxDecPulseMs, b.MaxDecPulseMs),
            ("X guide algorithm", a.RaAlgorithm, b.RaAlgorithm), ("PEC period", a.PecPeriodSeconds, b.PecPeriodSeconds),
            ("PEC prediction gain", a.PecPredictionGain, b.PecPredictionGain), ("PEC reactive aggression", a.PecReactiveAggressiveness, b.PecReactiveAggressiveness),
            ("Backlash comp", a.DecBacklashCompensation, b.DecBacklashCompensation),
        ];
        return fields.Where(f => !Equals(f.Old, f.New)).Select(f => FormattableString.Invariant($"{f.Name} = {f.New ?? "driver"}"));
    }

    public NovaGuiderStatus Status
    {
        get
        {
            var cameras = _equipment.VisibleDevices.Where(d => d.Kinds.Contains(DeviceKind.Camera)).Select(d => d.Name).ToList();
            var outputs = new List<GuideOutputChoice>();
            if (_equipment.Mount is { } mount) outputs.Add(new GuideOutputChoice(null, $"Mount ({mount.Name})"));
            foreach (var name in cameras)
                if (_equipment.Provider.GetCamera(name) is IGuideOutput { CanPulseGuide: true })
                    outputs.Add(new GuideOutputChoice(name, $"{name} (ST4)"));

            lock (_sync)
            {
                var stored = StoredFor(Camera?.Name);
                var info = stored is null ? null : Describe(stored.Calibration, stored.Warning) with { Points = stored.Points };
                var lockHome = _tracker.LockHome;
                var star = _last is { } m && lockHome is { } home
                    ? new GuideStarInfo(Math.Round(m.X, 2), Math.Round(m.Y, 2), Math.Round(home.X, 2), Math.Round(home.Y, 2),
                        Math.Round(m.Snr, 1), Math.Round(m.Hfr, 2), m.Found, m.Tracked)
                    : null;
                var marks = IsRunning
                    ? _tracker.Stars.Select((s, i) => new GuideStarMark(Math.Round(s.X, 2), Math.Round(s.Y, 2), i == 0, s.Found)).ToList()
                    : [];
                return new NovaGuiderStatus(Settings, cameras, outputs, Camera?.Name, IsRunning ? _activity : null, info, star,
                    _frameId, _frame?.Width, _frame?.Height, marks, DarkInfo(Camera), _darkProgress,
                    Camera is { IsConnected: true } c ? c.Controls.Gain : null, AssistantFor(Camera?.Name),
                    Camera?.IsConnected ?? false,
                    [.. _saved.Profiles.Select(p => new GuideProfileInfo(p.Name, false, null)),
                     .. GuidePresets.All.Select(p => new GuideProfileInfo(p.Name, true, p.Description))],
                    MatchingProfile(), Defaults,
                    Settings.Algorithms.RaAlgorithm == RaAlgorithm.PredictivePec ? _controller.Pec.Status : null,
                    BacklashStatus(stored?.Calibration));
            }
        }
    }

    /// <summary>RMS error over the recent steady guide steps (settling after starting or a dither left out).</summary>
    public GuideStats Stats
    {
        get
        {
            lock (_sync) return _history.Stats(PixelScale);
        }
    }

    public IReadOnlyList<GuidePoint> History
    {
        get
        {
            lock (_sync) return _history.Points(PixelScale);
        }
    }

    public DecDrift? Drift
    {
        get
        {
            lock (_sync) return PixelScale is { } scale ? _drift.Estimate(scale) : null;
        }
    }

    /// <summary>
    /// The latest guide frame as a PNG, or null before the first. Guide frames are short and faint, so the usual auto
    /// stretch (background at a quarter brightness) mostly shows noise: <paramref name="stretch"/> "low" keeps the
    /// background black with just the stars, "medium" (the default) shows a little background, "high" is the full
    /// auto stretch, for finding faint stars.
    /// </summary>
    public byte[]? FramePng(string? stretch = null)
    {
        RawImage? frame;
        lock (_sync) frame = _frame;
        if (frame is null) return null;
        var (background, clipping) = stretch?.ToLowerInvariant() switch
        {
            "low" => (0.02, -0.5),
            "high" => (0.25, -2.8),
            _ => (0.06, -1.0),
        };
        var stats = Preview.ComputeStatistics(frame.Pixels);
        return Png.EncodeGray8(frame.Width, frame.Height, Preview.AutoStretch(frame.Pixels, stats, background, clipping));
    }

    /// <summary>Exposes the guide camera in a loop and follows the brightest suitable star (no corrections).</summary>
    public async Task LoopAsync(CancellationToken ct)
    {
        await EnsureLoopAsync(ct);
        lock (_sync)
        {
            FailWaiters(new OperationCanceledException("Guiding was switched to looping"));
            _mode = Mode.Looping;
            _guideLog.EndGuiding(DateTime.UtcNow);
        }
    }

    /// <summary>Calibrates (looping if needed) and waits until it's done.</summary>
    public async Task CalibrateAsync(CancellationToken ct)
    {
        await EnsureLoopAsync(ct);
        var output = Output ?? throw new InvalidOperationException("No guide output: connect the mount (or choose a camera with an ST4 port)");
        var camera = Camera!;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            FailWaiters(new OperationCanceledException("Calibration started"));
            var (dec, pier) = Pointing();
            var step = Settings.CalibrationStepMs > 0
                ? Settings.CalibrationStepMs
                : PixelScale is { } scale ? GuideCalibrator.StepMilliseconds(scale, output.GuideRateSidereal ?? 0.5, dec ?? 0) : 1000;
            _calibrator = new GuideCalibrator(step, dec ?? 0, pier, skipDec: Settings.Algorithms.DecMode == DecGuideMode.Off);
            _calibrationDone = done;
            _mode = Mode.Calibrating;
            _activity = "Calibrating";
            var now = DateTime.UtcNow;
            _guideLog.EndGuiding(now);
            StartLogFile(now);
            _guideLog.BeginCalibration(now, [.. LogHeader(camera, output, calibration: null),
                FormattableString.Invariant($"Calibration step = {step} ms, Assume orthogonal axes = no")]);
        }
        _log.Info($"Calibrating the guider ({camera.Name} → {output.Name})…");
        await done.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Makes the star nearest a point of the guide frame (pixels) the lock star, with the best others as references.
    /// While guiding, guiding carries on from that star where it is now.
    /// </summary>
    public void SelectStar(double x, double y)
    {
        lock (_sync)
        {
            if (!IsRunning || _frame is null) throw new InvalidOperationException("Loop the guide camera first");
            if (_mode == Mode.Calibrating) throw new InvalidOperationException("Not while calibrating");
            if (!_tracker.SelectAt(_frameStars, _frame.Width, _frame.Height, x, y))
                throw new InvalidOperationException("No usable star there");
            if (_tracker.LockHome is { } home) _guideLog.Info(FormattableString.Invariant($"Star selected at {home.X:0.000}, {home.Y:0.000}"));
            _controller.Reset();
            _controller.Pec.Reset();
            _lostFrames = 0;
            _last = null;
        }
    }

    /// <summary>
    /// The guiding assistant: follows the star with corrections off for <paramref name="seconds"/> and measures the
    /// seeing jitter, the drift and RA's periodic error, then recommends min moves and an exposure (see
    /// <see cref="GuidingAssistant"/>). Guiding stops while it runs (no pulses); Stop ends it early.
    /// </summary>
    public async Task AssistantAsync(double seconds, CancellationToken ct)
    {
        if (seconds is < 30 or > 900) throw new ArgumentException("Run the assistant for 30–900 seconds");
        await EnsureLoopAsync(ct);
        await WaitForStarAsync(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            FailWaiters(new OperationCanceledException("The guiding assistant started"));
            _guideLog.EndGuiding(DateTime.UtcNow);
            (_assistant, _assistantStart, _assistantSeconds, _assistantDone) = (new GuidingAssistant(), DateTime.UtcNow, seconds, done);
            _rehome = true; // measure from where the star is now
            _mode = Mode.Assistant;
            _activity = "Guiding assistant";
        }
        _log.Info($"Guiding assistant: following the star unguided for {seconds:0} s…");
        await done.Task.WaitAsync(ct);
    }

    /// <summary>Takes the last assistant run's recommendations: the min moves and (when it gave one) the exposure.</summary>
    public void ApplyAssistant()
    {
        var result = AssistantFor(Camera?.Name) ?? throw new InvalidOperationException("Run the guiding assistant first");
        var s = Settings;
        UpdateSettings(s with
        {
            ExposureSeconds = result.RecommendedExposure ?? s.ExposureSeconds,
            Algorithms = s.Algorithms with { RaMinMove = result.RecommendedRaMinMove, DecMinMove = result.RecommendedDecMinMove },
        });
        _log.Info($"Guiding assistant's settings applied: min move RA {result.RecommendedRaMinMove:0.00} px, Dec {result.RecommendedDecMinMove:0.00} px" +
                  (result.RecommendedExposure is { } e ? $", exposure {e:0.#} s" : ""));
    }

    /// <summary>The last assistant result for this guide camera.</summary>
    private GuidingAssistantResult? AssistantFor(string? camera) => _saved.Assistant is { } a && a.Camera == camera ? a.Result : null;

    /// <summary>The seeing jitter per axis (″) from the assistant, when it measured along RA and Dec in arcseconds.</summary>
    public (double Ra, double Dec)? Seeing =>
        AssistantFor(Camera?.Name) is { Units: "arcsec", Axes: "RA/Dec" } r ? (r.JitterRa, r.JitterDec) : null;

    public void ClearCalibration()
    {
        lock (_sync) _saved = _saved with { Calibration = null };
        Save();
        _log.Info("Guider calibration cleared");
    }

    /// <summary>Starts guiding (calibrating first when there's no calibration for this camera and output) and waits until settled.</summary>
    public async Task GuideAsync(SettleSettings settle, bool recalibrate, CancellationToken ct)
    {
        await EnsureLoopAsync(ct);
        bool calibrated;
        lock (_sync) calibrated = StoredFor(Camera?.Name) is not null;
        if (recalibrate || !calibrated) await CalibrateAsync(ct);
        _ = Output ?? throw new InvalidOperationException("No guide output: connect the mount (or choose a camera with an ST4 port)");
        await WaitForStarAsync(ct); // a loop that has only just started has no star yet

        Task wait;
        lock (_sync)
        {
            FailWaiters(new OperationCanceledException("Guiding restarted"));
            if (!_tracker.HasLock) throw new InvalidOperationException("No guide star found");
            _history.Clear();
            _drift.Reset();
            _driftReported = false;
            _controller.Reset();
            _controller.Pec.Reset();
            (_pecState, _pecPier) = (null, null);
            _rehome = true;
            _mode = Mode.Guiding;
            _activity = "Guiding";
            var now = DateTime.UtcNow;
            StartLogFile(now);
            _guideLog.BeginGuiding(now, LogHeader(Camera!, Output!, Current()));
            wait = StartSettle(settle);
        }
        await wait.WaitAsync(ct);
    }

    /// <summary>Waits until the loop has found a guide star: a few exposures' time, then gives up.</summary>
    private async Task WaitForStarAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, 3 * Settings.ExposureSeconds + 5));
        while (true)
        {
            lock (_sync)
            {
                if (_tracker.HasLock && _last is not null) return;
            }
            if (!IsRunning) throw new InvalidOperationException("Guiding stopped");
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("No guide star found");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Moves the lock position by a random amount and waits until guiding has settled there.</summary>
    public async Task DitherAsync(double pixels, bool raOnly, SettleSettings settle, CancellationToken ct)
    {
        Task wait;
        lock (_sync)
        {
            if (_mode != Mode.Guiding || !IsRunning) throw new InvalidOperationException("Not guiding");
            var calibration = Current() ?? throw new InvalidOperationException("Not calibrated");
            var (dx, dy) = GuideController.DitherOffset(_dither, pixels, raOnly, calibration);
            _tracker.ShiftHome(dx, dy);
            _controller.Reset();
            _controller.LockMoved(calibration.ToAxes(dx, dy).Ra);
            _history.MarkDither();
            if (_tracker.LockHome is { } home)
                _guideLog.Info(FormattableString.Invariant($"DITHER by {dx:0.000}, {dy:0.000}, new lock pos = {home.X:0.000}, {home.Y:0.000}"));
            wait = StartSettle(settle);
        }
        await wait.WaitAsync(ct);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            (cts, loop) = (_loopCts, _loop);
            FailWaiters(new OperationCanceledException("Guiding stopped"));
        }
        if (cts is null) return;
        cts.Cancel();
        try
        {
            if (loop is not null) await loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ---- Dark library ----

    /// <summary>Why a dark library request can't be done, or null when it can.</summary>
    public string? DarkRequestProblem(IReadOnlyList<double> exposures, int frames)
    {
        if (exposures.Count == 0) return "Choose at least one exposure";
        if (exposures.Any(e => e is < 0.05 or > 30)) return "Dark exposures must be 0.05–30 s";
        if (frames is < 1 or > 50) return "Take 1–50 darks per exposure";
        if (IsRunning) return "Stop the guide camera first (darks need the scope covered)";
        if (_darkProgress is not null) return "Already taking darks";
        return Camera is null ? "No guide camera: choose one in the Guider panel" : null;
    }

    /// <summary>
    /// Takes <paramref name="frames"/> darks at each exposure (scope covered!), combines them into a master dark per
    /// exposure and keeps them for this camera and binning, replacing older ones at the same exposure.
    /// </summary>
    public async Task BuildDarksAsync(IReadOnlyList<double> exposures, int frames, CancellationToken ct)
    {
        if (DarkRequestProblem(exposures, frames) is { } problem) throw new InvalidOperationException(problem);
        var camera = Camera!;
        _darkProgress = "Starting";
        try
        {
            if (!camera.IsConnected)
            {
                await camera.ConnectAsync(ct);
                _equipment.NotifyDevicesChanged();
            }
            if (camera.Controls.Binning is { } binning && (int)binning.Value != Settings.Binning)
                await camera.ApplyAsync(new CameraSettingsUpdate(Binning: Settings.Binning), ct);
            await SetFrameTypeAsync(camera, FrameType.Dark, ct);
            await ApplyGainAsync(camera, ct); // darks must be at the gain they'll be used with
            var gain = camera.Controls.Gain?.Value;
            Directory.CreateDirectory(_darkFolder);

            foreach (var exposure in exposures.Distinct().Order())
            {
                var taken = new List<RawImage>();
                for (var i = 1; i <= frames; i++)
                {
                    _darkProgress = $"Dark {exposure:0.##} s, {i} of {frames}";
                    taken.Add((await camera.ExposeAsync(new ExposureRequest(exposure), null, ct)).Image);
                }
                var master = DarkFrames.Combine(taken);
                var name = $"{Safe(camera.Name)}-bin{Settings.Binning}-{exposure.ToString("0.###", CultureInfo.InvariantCulture)}s.fits";
                var path = Path.Combine(_darkFolder, name);
                var pixels = master.Pixels.Select(p => (ushort)Math.Clamp(Math.Round(p), 0, 65535)).ToArray();
                await File.WriteAllBytesAsync(path, Fits.WriteUInt16(master.Width, master.Height, pixels,
                    [new FitsCard("IMAGETYP", "Dark Frame"), new FitsCard("EXPTIME", exposure), new FitsCard("NCOMBINE", frames)]), ct);
                var dark = new StoredDark(camera.Name, Settings.Binning, gain, exposure, frames, DateTime.UtcNow, DarkFrames.HotPixels(master), name);
                lock (_sync)
                {
                    _saved = _saved with
                    {
                        Darks = [.. _saved.Darks.Where(d => !(d.Camera == dark.Camera && d.Binning == dark.Binning && Math.Abs(d.ExposureSeconds - exposure) < 0.001)), dark],
                    };
                    _darkCache[name] = master;
                    SaveLocked();
                }
            }
            _log.Info($"Guide darks taken: {string.Join(", ", exposures.Distinct().Order().Select(e => $"{e:0.##} s"))}, {frames} each");
        }
        finally
        {
            _darkProgress = null;
            try
            {
                await SetFrameTypeAsync(camera, FrameType.Light, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warn($"Couldn't set {camera.Name} back to light frames: {ex.Message}");
            }
        }

        static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
    }

    /// <summary>Forgets this guide camera's darks (all binnings) and deletes their files.</summary>
    public void ClearDarks()
    {
        var camera = Camera?.Name;
        lock (_sync)
        {
            foreach (var d in _saved.Darks.Where(d => d.Camera == camera))
            {
                _darkCache.Remove(d.File);
                try { File.Delete(Path.Combine(_darkFolder, d.File)); } catch (IOException) { }
            }
            _saved = _saved with { Darks = _saved.Darks.Where(d => d.Camera != camera).ToList() };
            SaveLocked();
        }
        _log.Info("Guide darks cleared");
    }

    /// <summary>Sets the guide camera's gain to the setting, when there is one and the camera differs.</summary>
    private async Task ApplyGainAsync(ICamera camera, CancellationToken ct)
    {
        if (Settings.Gain is not { } gain || camera.Controls.Gain is not { } current || Math.Abs(current.Value - gain) < 0.01) return;
        await camera.ApplyAsync(new CameraSettingsUpdate(Gain: Math.Clamp(gain, current.Min, current.Max)), ct);
    }

    private static async Task SetFrameTypeAsync(ICamera camera, FrameType type, CancellationToken ct)
    {
        if (camera.Controls.FrameType is null || camera.Controls.FrameType == type) return;
        await camera.ApplyAsync(new CameraSettingsUpdate(FrameType: type), ct);
    }

    /// <summary>This camera's darks at the current binning that fit its gain now (all of them when the gain isn't known).</summary>
    private List<StoredDark> UsableDarks(ICamera camera)
    {
        var gain = camera.Controls.Gain?.Value;
        lock (_sync)
            return _saved.Darks.Where(d => d.Camera == camera.Name && d.Binning == Settings.Binning &&
                                           (d.Gain is null || gain is null || Math.Abs(d.Gain.Value - gain.Value) < 0.5)).ToList();
    }

    private GuideDarkInfo? DarkInfo(ICamera? camera)
    {
        if (camera is null) return null;
        List<StoredDark> all;
        lock (_sync) all = _saved.Darks.Where(d => d.Camera == camera.Name && d.Binning == Settings.Binning).ToList();
        if (all.Count == 0) return null;
        var usable = UsableDarks(camera);
        return new GuideDarkInfo(all.Select(d => d.ExposureSeconds).Order().ToList(), all.Min(d => d.Frames), all.Max(d => d.Utc),
            all[0].Gain, usable.Count == all.Count, all.Max(d => d.HotPixels), Settings.UseDarks && usable.Count > 0);
    }

    /// <summary>The frame with the matching dark taken out (or the nearest exposure's, scaled); as it was without one.</summary>
    private RawImage WithoutDark(ICamera camera, RawImage image, double exposure)
    {
        if (!Settings.UseDarks) return image;
        var darks = UsableDarks(camera);
        if (DarkFrames.Choose(darks.Select(d => d.ExposureSeconds).ToList(), exposure) is not { } choice) return image;
        var stored = darks[choice.Index];
        RawImage? dark;
        lock (_sync)
        {
            if (!_darkCache.TryGetValue(stored.File, out dark))
            {
                try
                {
                    dark = Fits.Read(File.ReadAllBytes(Path.Combine(_darkFolder, stored.File)));
                    _darkCache[stored.File] = dark;
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    _log.Warn($"Can't read the guide dark {stored.File}: {ex.Message}");
                    _saved = _saved with { Darks = _saved.Darks.Where(d => d != stored).ToList() };
                    return image;
                }
            }
        }
        return dark.Width == image.Width && dark.Height == image.Height ? DarkFrames.Subtract(image, dark, choice.Ratio) : image;
    }

    // ---- The loop ----

    private IGuideOutput? Output =>
        Settings.Output is { } name
            ? _equipment.Provider.GetCamera(name) as IGuideOutput ?? _equipment.Provider.GetMount(name) as IGuideOutput
            : _equipment.Mount as IGuideOutput;

    private (double? Dec, PierSide? Pier) Pointing() =>
        _equipment.Mount is { IsConnected: true } m ? (m.Coordinates.DecDegrees, m.SideOfPier) : (null, null);

    private async Task EnsureLoopAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        if (_darkProgress is not null) throw new InvalidOperationException("Taking guide darks: wait until they're done, or Stop");
        var camera = Camera ?? throw new InvalidOperationException("No guide camera: choose one in the Guider panel");
        if (!camera.IsConnected)
        {
            await camera.ConnectAsync(ct);
            _log.Info($"{camera.Name} connected");
            _equipment.NotifyDevicesChanged();
        }
        if (Output is { IsConnected: false } output) await output.ConnectAsync(ct);
        if (camera.Controls.Binning is { } binning && (int)binning.Value != Settings.Binning)
            await camera.ApplyAsync(new CameraSettingsUpdate(Binning: Settings.Binning), ct);

        lock (_sync)
        {
            if (IsRunning) return;
            _tracker.Clear();
            _last = null;
            _mode = Mode.Looping;
            _activity = "Looping";
            _lostFrames = 0;
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            State = GuiderState.Looping;
            _loop = Task.Run(() => RunAsync(camera, cts.Token));
        }
    }

    private async Task RunAsync(ICamera camera, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var exposure = Settings.ExposureSeconds;
                await ApplyGainAsync(camera, ct); // a changed setting takes effect from the next frame
                var frame = await camera.ExposeAsync(new ExposureRequest(exposure), null, ct);
                var image = WithoutDark(camera, frame.Image, exposure);
                var stars = StarDetector.Analyze(image, new StarDetectionOptions(MinSnr: 6, MaxStars: 60)).Stars;
                var pulses = Process(camera, image, stars, DateTime.UtcNow);
                if (pulses.Count > 0 && Output is { } output)
                    await Task.WhenAll(pulses.Select(p => output.PulseAsync(p.Direction, TimeSpan.FromMilliseconds(p.Milliseconds), ct)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Guiding stopped", ex);
            lock (_sync) FailWaiters(ex);
        }
        finally
        {
            lock (_sync)
            {
                FailWaiters(new OperationCanceledException("Guiding stopped"));
                _guideLog.EndGuiding(DateTime.UtcNow);
                State = GuiderState.Stopped;
                _activity = null;
                _loopCts?.Dispose();
                _loopCts = null;
            }
        }
    }

    /// <summary>One frame: find the stars, then calibrate or guide. Returns the pulses to send.</summary>
    private List<GuidePulse> Process(ICamera camera, RawImage image, IReadOnlyList<DetectedStar> stars, DateTime utc)
    {
        var pulses = new List<GuidePulse>();
        string? message = null;
        string? pecInfo = null;
        lock (_sync)
        {
            _frame = image;
            _frameStars = stars;
            _frameId++;
            var preset = _optics?.All.FirstOrDefault(o => o.Camera == camera.Name)?.ImageScale;
            var binning = FitsMetadata.Number(image, "XBINNING") is > 0 and var b ? b : 1;
            _scale = preset is { } p ? p * binning : FitsMetadata.ImageScale(image);

            if (!_tracker.HasLock && !_tracker.Select(stars, image.Width, image.Height))
            {
                _last = null;
                Lost(utc, "No guide star found");
                return pulses;
            }

            if (_rehome)
            {
                _tracker.SetHomeHere();
                _rehome = false;
            }

            var m = _tracker.Measure(stars);
            if (m is null)
            {
                _last = null;
                _lostFrames++;
                Lost(utc, "Lost the guide star");
                if (_mode == Mode.Calibrating)
                {
                    _calibrator!.Lost();
                    message = FinishCalibration(camera);
                }
                else if (_mode == Mode.Looping && _lostFrames >= LostFramesBeforeReselect)
                {
                    _tracker.Clear();
                }
                return pulses;
            }
            _lostFrames = 0;
            _last = m;

            switch (_mode)
            {
                case Mode.Looping:
                    State = GuiderState.Looping;
                    _activity = "Looping";
                    break;

                case Mode.Assistant:
                    State = GuiderState.Looping;
                    var cal = Current();
                    var (alongRa, alongDec) = cal is null ? (m.Dx, m.Dy) : cal.ToAxes(m.Dx, m.Dy);
                    var elapsed = (utc - _assistantStart).TotalSeconds;
                    _assistant!.Add(elapsed, alongRa, alongDec);
                    _activity = $"Guiding assistant: {elapsed:0} of {_assistantSeconds:0} s";
                    if (elapsed >= _assistantSeconds) message = FinishAssistant(camera, utc, cal is not null);
                    break;

                case Mode.Calibrating:
                    State = GuiderState.Calibrating;
                    if (_calibrator!.Next(m.X, m.Y) is { } step) pulses.Add(new GuidePulse(step.Direction, step.Milliseconds));
                    else message = FinishCalibration(camera);
                    _activity = _calibrator?.Status ?? _activity;
                    break;

                case Mode.Guiding when Current() is { } calibration:
                    State = GuiderState.Guiding;
                    pecInfo = PecWatch(calibration);
                    var c = _controller.Correct(m.Dx, m.Dy, calibration, _clock.Elapsed.TotalSeconds, steady: _settle is null);
                    _history.Add(c.RaPixels, c.DecPixels, settling: _settle is not null);
                    var raGuide = c.Ra is { } rp ? rp.Milliseconds / 1000.0 * calibration.RaRate * (rp.Direction == GuideDirection.East ? 1 : -1) : 0;
                    var decGuide = c.Dec is { } dp ? dp.Milliseconds / 1000.0 * calibration.DecRate * (dp.Direction == GuideDirection.South ? 1 : -1) : 0;
                    _guideLog.Step(utc, new GuideLogStep(m.Dx, m.Dy, c.RaPixels, c.DecPixels, raGuide, decGuide, c.Ra, c.Dec, m.Mass, m.Snr));
                    if (c.Ra is { } ra) pulses.Add(ra);
                    if (c.Dec is { } dec) pulses.Add(dec);
                    // What actually moved the star: the part taking up backlash didn't (and would look like drift).
                    var decMove = c.Dec is { } d ? (d.Milliseconds - c.DecBacklashMs) / 1000.0 * calibration.DecRate : 0;
                    _drift.Add(_clock.Elapsed.TotalSeconds, decMove, c.Dec?.Direction.ToString(), counts: _settle is null);
                    if (!_driftReported && PixelScale is { } scale && _drift.Estimate(scale) is { Significant: true } drift)
                    {
                        _driftReported = true;
                        message = $"Dec is drifting {drift.ArcsecPerMinute:0}″ a minute (guiding keeps pushing {drift.Direction.ToLowerInvariant()}): " +
                                  $"the polar alignment is at least {DecDriftMonitor.Describe(drift.PolarErrorArcmin)} off. Polar align again for better guiding.";
                    }
                    _activity = _settle is null ? "Guiding" : "Settling";
                    Settle(utc, m.Distance);
                    break;
            }
        }
        if (pecInfo is not null) _log.Info(pecInfo);
        if (message is not null) _log.Warn(message);
        return pulses;
    }

    private void Lost(DateTime utc, string activity)
    {
        _activity = activity;
        if (_mode == Mode.Looping) return;
        State = GuiderState.LostLock;
        if (_mode == Mode.Guiding) _guideLog.StarLost(utc, activity);
        _drift.Add(_clock.Elapsed.TotalSeconds, 0, null, counts: false);
        Settle(utc, null);
    }

    /// <summary>Stores a finished calibration (or reports why it failed) and goes back to looping. Returns a warning to log.</summary>
    private string? FinishCalibration(ICamera camera)
    {
        var calibrator = _calibrator!;
        var done = _calibrationDone;
        (_calibrator, _calibrationDone, _mode) = (null, null, Mode.Looping);
        _guideLog.EndCalibration(DateTime.UtcNow, calibrator.Points, calibrator.Result, calibrator.Error, PixelScale);
        if (calibrator.Result is { } result)
        {
            _saved = _saved with
            {
                Calibration = new StoredCalibration(camera.Name, Settings.Output, Settings.Binning, result, calibrator.Warning, [.. calibrator.Points]),
            };
            SaveLocked();
            var info = Describe(result, null);
            var backlash = result.DecBacklashPixels >= DecBacklashCompensator.MinimumPixels
                ? $", Dec backlash {(PixelScale is { } s ? $"{result.DecBacklashPixels * s:0.#}″" : $"{result.DecBacklashPixels:0.0} px")} ({result.DecBacklashMs:0} ms)" +
                  (Settings.Algorithms.DecBacklashCompensation ? ", compensated" : "")
                : "";
            _log.Info($"Guider calibrated: RA {info.RaAngle:0}° at {Rate(info.RaRate, info.RaArcsecPerSecond)}, " +
                      $"Dec {info.DecAngle:0}° at {Rate(info.DecRate, info.DecArcsecPerSecond)}, {info.Orthogonality:0.#}° from square{backlash}");
            _tracker.SetHomeHere();
            done?.TrySetResult();
            return calibrator.Warning;
        }
        done?.TrySetException(new InvalidOperationException(calibrator.Error ?? "Calibration failed"));
        return null;

        static string Rate(double px, double? arcsec) => arcsec is { } a ? $"{a:0.0}″/s" : $"{px:0.0} px/s";
    }

    private Task StartSettle(SettleSettings settle)
    {
        _settle = new SettleTracker(settle, DateTime.UtcNow);
        _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _guideLog.Info("SETTLING STATE CHANGE, Settling started");
        return _settled.Task;
    }

    private void Settle(DateTime utc, double? distance)
    {
        if (_settle is null) return;
        try
        {
            if (!_settle.Add(utc, distance)) return;
            _settled?.TrySetResult();
            _guideLog.Info("SETTLING STATE CHANGE, Settling complete");
        }
        catch (TimeoutException ex)
        {
            _settled?.TrySetException(ex);
            _guideLog.Info($"SETTLING STATE CHANGE, Settling failed: {ex.Message}");
        }
        (_settle, _settled) = (null, null);
    }

    /// <summary>Works out and keeps the assistant's result, and goes back to looping. Returns its summary to log.</summary>
    private string? FinishAssistant(ICamera camera, DateTime utc, bool calibrated)
    {
        var result = _assistant!.Result(utc, PixelScale, calibrated, Settings.ExposureSeconds);
        var done = _assistantDone;
        (_assistant, _assistantDone, _mode) = (null, null, Mode.Looping);
        _activity = "Looping";
        if (result is null)
        {
            done?.TrySetException(new InvalidOperationException("The guiding assistant lost the star too often to measure anything"));
            return null;
        }
        _saved = _saved with { Assistant = new StoredAssistant(camera.Name, result) };
        SaveLocked();
        done?.TrySetResult();
        _log.Info($"Guiding assistant: {result.Summary}. Recommends min move RA {result.RecommendedRaMinMove:0.00} px, " +
                  $"Dec {result.RecommendedDecMinMove:0.00} px" + (result.RecommendedExposure is { } e ? $", exposure {e:0.#} s" : ""));
        return null;
    }

    private void FailWaiters(Exception ex)
    {
        if (_assistant is not null)
        {
            _assistantDone?.TrySetException(ex);
            (_assistant, _assistantDone) = (null, null);
            if (_mode == Mode.Assistant) _mode = Mode.Looping;
        }
        if (_calibrator is { } calibrator)
            _guideLog.EndCalibration(DateTime.UtcNow, calibrator.Points, null, ex.Message, PixelScale);
        _settled?.TrySetException(ex);
        _calibrationDone?.TrySetException(ex);
        (_settle, _settled, _calibrator, _calibrationDone) = (null, null, null, null);
        if (_mode == Mode.Calibrating) _mode = Mode.Looping;
    }

    /// <summary>The stored calibration, adapted to where the mount points now.</summary>
    private GuideCalibration? Current()
    {
        if (StoredFor(Camera?.Name) is not { } stored) return null;
        var (dec, pier) = Pointing();
        return dec is null ? stored.Calibration : stored.Calibration.For(dec.Value, pier, Settings.ReverseDecAfterFlip);
    }

    /// <summary>The saved calibration, if it was made with this camera, output and binning.</summary>
    private StoredCalibration? StoredFor(string? camera) =>
        _saved.Calibration is { } c && c.Camera == camera && c.Output == Settings.Output && c.Binning == Settings.Binning ? c : null;

    /// <summary>Dec backlash compensation for display, once a calibration has measured some backlash.</summary>
    private DecBacklashStatus? BacklashStatus(GuideCalibration? calibration)
    {
        if (calibration is null || calibration.DecBacklashPixels < DecBacklashCompensator.MinimumPixels) return null;
        var b = _controller.Backlash;
        var measuredMs = (int)Math.Round(calibration.DecBacklashMs);
        // Before guiding seeds it from this calibration, the pulse is the measured one.
        var pulse = b.MeasuredMs > 0 && Math.Abs(b.MeasuredMs - calibration.DecBacklashMs) < 1 ? (int)Math.Round(b.PulseMs) : measuredMs;
        return new DecBacklashStatus(Settings.Algorithms.DecBacklashCompensation, calibration.DecBacklashPixels, measuredMs, pulse,
            b.MeasuredMs > 0 ? b.Applied : 0);
    }

    private GuideCalibrationInfo Describe(GuideCalibration c, string? warning)
    {
        static double Deg(double r) => Math.Round(((r * 180 / Math.PI) % 360 + 360) % 360, 1);
        double? Arcsec(double rate) => PixelScale is { } s ? Math.Round(rate * s, 2) : null;
        return new GuideCalibrationInfo(Deg(c.RaAngle), Deg(c.DecAngle), Math.Round(c.RaRate, 3), Math.Round(c.DecRate, 3),
            Arcsec(c.RaRate), Arcsec(c.DecRate), Math.Round(c.OrthogonalityError, 1), Math.Round(c.DecDegrees, 1), c.PierSide, c.Utc, warning,
            DecBacklashPixels: Math.Round(c.DecBacklashPixels, 2), DecBacklashMs: (int)Math.Round(c.DecBacklashMs));
    }

    // ---- Predictive PEC ----

    private PecState? _pecState;
    private PierSide? _pecPier;

    /// <summary>
    /// Before each guide step with Predictive PEC: starts the model over after a pier-side change (the RA gear is
    /// driven the other way round), and notes when it starts predicting. Returns a line for the activity log, if any.
    /// </summary>
    private string? PecWatch(GuideCalibration calibration)
    {
        if (Settings.Algorithms.RaAlgorithm != RaAlgorithm.PredictivePec) return null;
        var (_, pier) = Pointing();
        if (_pecPier is not null && pier is not null && pier != _pecPier)
        {
            _controller.Pec.Reset();
            _pecState = null;
            _guideLog.Info("PEC: pier side changed, learning again");
        }
        _pecPier = pier ?? _pecPier;

        var status = _controller.Pec.Status;
        if (status.State == _pecState) return null;
        var was = _pecState;
        _pecState = status.State;
        if (status.State != PecState.Predicting || was is null && status.Minutes < 1) return null;
        var scale = PixelScale;
        var size = scale is { } s ? FormattableString.Invariant($"±{status.AmplitudePixels * s:0.0}″") : FormattableString.Invariant($"±{status.AmplitudePixels:0.00} px");
        var line = FormattableString.Invariant(
            $"PEC: predicting, period {string.Join(" + ", status.Periods.Select(p => $"{p:0} s"))}, {status.Quality * 100:0}% of the RA motion explained, {size}");
        _guideLog.Info(line);
        return line;
    }

    // ---- Guide log ----

    private string LogPathIn(string root, DateTime utc)
    {
        var night = GuideLog.Night(utc);
        return Path.Combine(root, night, $"NOVA_GuideLog_{night}.txt");
    }

    /// <summary>Picks the night's log file at the start of a calibration or guiding run: where frames are going now.</summary>
    private void StartLogFile(DateTime utc)
    {
        string root;
        try
        {
            root = Storage?.ImageRoot() ?? _localImages;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            root = _localImages;
        }
        _guideLogPath = LogPathIn(root, utc);
    }

    /// <summary>The settings, equipment and pointing at the start of a run, in PHD2's words.</summary>
    private List<string> LogHeader(ICamera camera, IGuideOutput output, GuideCalibration? calibration)
    {
        var s = Settings;
        var a = s.Algorithms;
        var scale = PixelScale;
        var size = camera.SensorSize is { } z ? $"{z.Width} x {z.Height}" : _frame is { } f ? $"{f.Width} x {f.Height}" : "unknown";
        var darks = UsableDarks(camera);
        var dark = s.UseDarks && darks.Count > 0 ? $"have dark, dark dur = {darks.Min(d => d.ExposureSeconds) * 1000:0}" : "no dark";
        var (dec, pier) = Pointing();
        var ra = _equipment.Mount is { IsConnected: true } mount ? mount.Coordinates.RaHours : (double?)null;
        var lines = new List<string>
        {
            "Dither = both axes, Dither scale = 1.000, Image noise reduction = none, Guide-frame time lapse = 0, Server enabled",
            FormattableString.Invariant($"Pixel scale = {(scale is { } sc ? sc.ToString("0.00", CultureInfo.InvariantCulture) : "N/A")} arc-sec/px, Binning = {s.Binning}, Focal length = N/A mm"),
            FormattableString.Invariant($"Search region = {_tracker.SearchRadius:0} px, Star mass tolerance = disabled"),
            "Equipment Profile = NOVA",
            FormattableString.Invariant($"Camera = {camera.Name}, gain = {camera.Controls.Gain?.Value.ToString("0", CultureInfo.InvariantCulture) ?? "N/A"}, full size = {size}, {dark}, no defect map, pixel size = {camera.PixelSize?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A"} um"),
            FormattableString.Invariant($"Exposure = {s.ExposureSeconds * 1000:0} ms"),
            calibration is { } c
                ? FormattableString.Invariant($"Mount = {output.Name}, connected, guiding enabled, xAngle = {Deg(c.RaAngle):0.0}, xRate = {c.RaRate:0.000}, yAngle = {Deg(c.DecAngle):0.0}, yRate = {c.DecRate:0.000}, parity = +/+")
                : $"Mount = {output.Name}, connected, guiding enabled",
            a.RaAlgorithm == RaAlgorithm.PredictivePec
                ? FormattableString.Invariant($"X guide algorithm = Predictive PEC, Period = {(a.PecPeriodSeconds > 0 ? $"{a.PecPeriodSeconds:0.0} s" : "auto")}, Prediction gain = {a.PecPredictionGain:0.000}, Reactive aggression = {a.PecReactiveAggressiveness:0.000}, Hysteresis = {a.RaHysteresis:0.000}, Minimum move = {a.RaMinMove:0.000}")
                : FormattableString.Invariant($"X guide algorithm = Hysteresis, Hysteresis = {a.RaHysteresis:0.000}, Aggression = {a.RaAggressiveness:0.000}, Minimum move = {a.RaMinMove:0.000}"),
            FormattableString.Invariant($"Y guide algorithm = Resist Switch, Minimum move = {a.DecMinMove:0.000} Aggression = {a.DecAggressiveness * 100:0}% FastSwitch = {(a.DecFastSwitch ? "enabled" : "disabled")}"),
            a.DecBacklashCompensation && _controller.Backlash.PulseMs >= 10
                ? FormattableString.Invariant($"Backlash comp = enabled, pulse = {_controller.Backlash.PulseMs:0} ms")
                : calibration is { DecBacklashPixels: >= DecBacklashCompensator.MinimumPixels } && a.DecBacklashCompensation
                    ? FormattableString.Invariant($"Backlash comp = enabled, pulse = {calibration.DecBacklashMs:0} ms")
                    : "Backlash comp = disabled, pulse = 0 ms",
            FormattableString.Invariant($"Max RA duration = {a.MaxRaPulseMs}, Max DEC duration = {a.MaxDecPulseMs}, DEC guide mode = {a.DecMode}"),
        };
        if (calibration is { } cal && scale is { } px)
            lines.Add(FormattableString.Invariant($"RA Guide Speed = {cal.RaRate * px:0.0} a-s/s, Dec Guide Speed = {cal.DecRate * px:0.0} a-s/s, Cal Dec = {cal.DecDegrees:0.0}, Last Cal Issue = None, Timestamp = {cal.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
        lines.Add(FormattableString.Invariant($"RA = {(ra is { } r ? r.ToString("0.00", CultureInfo.InvariantCulture) : "N/A")} hr, Dec = {(dec is { } d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "N/A")} deg, Hour angle = N/A hr, Pier side = {pier?.ToString() ?? "N/A"}, Rotator pos = N/A, Alt = N/A deg, Az = N/A deg"));
        if (_tracker.LockHome is { } home) lines.Add(FormattableString.Invariant($"Lock position = {home.X:0.000}, {home.Y:0.000}"));
        if (_last is { } m) lines.Add(FormattableString.Invariant($"Star position = {m.X:0.000}, {m.Y:0.000}, HFD = {m.Hfr * 2:0.00} px"));
        return lines;

        static double Deg(double r) => ((r * 180 / Math.PI) % 360 + 540) % 360 - 180;
    }

    /// <summary>The guide logs on the mini PC and the drive, newest night first.</summary>
    public IReadOnlyList<GuideLogFile> GuideLogs()
    {
        var roots = new List<(string Root, bool Drive)> { (_localImages, false) };
        try
        {
            if (Storage?.ImageRoot() is { } root && Path.GetFullPath(root) != Path.GetFullPath(_localImages)) roots.Add((root, true));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // no drive
        }
        var files = new List<GuideLogFile>();
        foreach (var (root, drive) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var night in Directory.EnumerateDirectories(root))
            foreach (var file in Directory.EnumerateFiles(night, "NOVA_GuideLog_*.txt"))
            {
                var info = new FileInfo(file);
                files.Add(new GuideLogFile(info.Name, Path.GetFileName(night), drive, info.Length, info.LastWriteTimeUtc));
            }
        }
        return files.OrderByDescending(f => f.Night).ThenBy(f => f.OnDrive).ToList();
    }

    /// <summary>A guide log's full path, by name (only NOVA's guide logs, only in the image folders).</summary>
    public string? GuideLogPath(string name, bool onDrive)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^NOVA_GuideLog_\d{4}-\d{2}-\d{2}\.txt$")) return null;
        var night = name["NOVA_GuideLog_".Length..^".txt".Length];
        string root;
        try
        {
            root = onDrive ? Storage?.ImageRoot() ?? _localImages : _localImages;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
        var path = Path.Combine(root, night, name);
        return File.Exists(path) ? path : null;
    }

    // ---- Settings file ----

    private sealed record StoredCalibration(
        string Camera, string? Output, int Binning, GuideCalibration Calibration, string? Warning, IReadOnlyList<CalibrationPoint>? Points = null);

    private sealed record StoredDark(
        string Camera, int Binning, double? Gain, double ExposureSeconds, int Frames, DateTime Utc, int HotPixels, string File);

    private sealed record StoredAssistant(string Camera, GuidingAssistantResult Result);

    private sealed record GuideProfile(string Name, NovaGuiderSettings Settings);

    /// <param name="MountDefaults">Which mount's defaults were offered already ("zwo-am"), so they're applied once.</param>
    private sealed record GuiderFile(
        string Backend = "phd2", NovaGuiderSettings? Nova = null, StoredCalibration? Calibration = null, IReadOnlyList<StoredDark>? Darks = null,
        StoredAssistant? Assistant = null, IReadOnlyList<GuideProfile>? Profiles = null, string? MountDefaults = null)
    {
        public IReadOnlyList<GuideProfile> Profiles { get; init; } = Profiles ?? [];
        public IReadOnlyList<StoredDark> Darks { get; init; } = Darks ?? [];
        public NovaGuiderSettings Nova { get; init; } = Nova ?? new NovaGuiderSettings();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private GuiderFile Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<GuiderFile>(File.ReadAllText(_file), Json) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        // A new install guides with NOVA's own guider; one that has been used already (a site, optics or a sequence set
        // up, from before NOVA had a guider of its own, so it was using PHD2) keeps PHD2. Either can be switched in the
        // Guider panel. The choice is saved straight away, so it doesn't change once other settings appear.
        var settings = Path.GetDirectoryName(_file)!;
        var used = UsedSettings.Any(name => File.Exists(Path.Combine(settings, name)) || Directory.Exists(Path.Combine(settings, name)));
        var fresh = new GuiderFile(Backend: used ? "phd2" : "nova");
        try
        {
            Directory.CreateDirectory(settings);
            File.WriteAllText(_file, JsonSerializer.Serialize(fresh, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // decided again next start
        }
        return fresh;
    }

    /// <summary>Settings that only exist once NOVA has been used (set up by hand, not written at start).</summary>
    private static readonly string[] UsedSettings = ["sites.json", "optics.json", "sequences", "autofocus.json", "flats.json", "image-history.json"];

    private void Save()
    {
        lock (_sync) SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_saved, Json));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save the guider settings to {_file}", ex);
        }
    }
}
