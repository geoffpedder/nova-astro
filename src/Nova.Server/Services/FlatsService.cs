using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>How flats are taken.</summary>
/// <param name="TargetPercent">Median brightness to aim for, as a percentage of full scale (65535).</param>
/// <param name="Count">Flats to take, and as many dark flats.</param>
/// <param name="DarkFlats">Also take dark flats (same exposure, panel off and scope covered).</param>
/// <param name="MinExposure">Shortest exposure, seconds: shorter ones can show shutter or panel-flicker banding.</param>
/// <param name="UsePanel">With a flat panel connected, let NOVA switch it on and off, set its brightness and close its cover.</param>
public sealed record FlatsSettings(
    double TargetPercent = 50, int Count = 25, bool DarkFlats = true, double MinExposure = 0.1, double MaxExposure = 30,
    bool UsePanel = true)
{
    public string? Validate() =>
        TargetPercent is < 10 or > 90 ? "Target brightness must be between 10% and 90%"
        : Count is < 1 or > 500 ? "Count must be between 1 and 500"
        : MinExposure is < 0.001 or > 60 || MaxExposure < MinExposure || MaxExposure > 600 ? "Exposure limits are out of range"
        : null;
}

/// <summary>Start flats by hand. Null camera settings keep what the camera has.</summary>
/// <param name="Name">What they're for (a target, or several): names the folder they're saved in.</param>
public sealed record FlatsRequest(string? Name, double? Gain = null, double? Offset = null, int? Binning = null, FlatsSettings? Settings = null);

/// <summary>One test frame while finding the exposure.</summary>
public sealed record FlatTest(double Exposure, double Median);

/// <param name="Phase">Panel, Calibrating, Flats, Cover, DarkFlats, Done, Skipped, Failed or Cancelled.</param>
/// <param name="Ask">What NOVA is waiting for you to do: PanelOn, Dimmer, Brighter or Cover; null when it isn't.</param>
/// <param name="FromSequence">The sequencer is waiting on these (between targets it gives up after a while).</param>
public sealed record FlatsStatus(
    bool Running, string Phase, string Message, string? Ask, string? Name, double TargetAdu, double? Exposure,
    double? Median, int Flats, int DarkFlats, int Count, bool TakeDarkFlats, IReadOnlyList<FlatTest> Tests,
    bool FromSequence, string? Folder, string? Panel = null, double? PanelBrightness = null);

public sealed record FlatsInfo(bool Running, FlatsSettings Settings, FlatsStatus? Status);

/// <summary>
/// Flats with a panel switched on by hand (or a T-shirt, or a tablet): asks for the panel, finds the exposure that
/// gives the target brightness from a few test frames (brightness grows in proportion to exposure), takes the flats,
/// checking each one, then asks for the scope to be covered and takes as many dark flats at the same exposure.
/// Saved under the night's folder in Flats/&lt;name&gt;. The exposure found is remembered (per gain, offset, binning and
/// filter) to start from next time.
/// </summary>
public sealed class FlatsService
{
    /// <summary>Tells your phone when a sequence's flats wait for you (set at startup).</summary>
    public NotificationService? Notifications { get; set; }
    private bool _fromSequence;

    private const double FullScale = 65535;
    private const double CalibrationTolerance = 0.05; // within 5% of the target to start
    private const double KeepTolerance = 0.25;        // a flat more than 25% off is taken again
    private const int MaxTests = 10;
    private const int MaxRetakes = 5;

    private readonly EquipmentService _equipment;
    private readonly ImagingService _imaging;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private Stored _stored;
    private CancellationTokenSource? _run;
    private TaskCompletionSource<bool>? _answer; // true: done what was asked; false: skip that part

    private sealed record Stored(FlatsSettings Settings, Dictionary<string, double> Exposures, Dictionary<string, double>? Brightness = null);

    public FlatsService(EquipmentService equipment, ImagingService imaging, ActivityLog log, IOptions<NovaOptions> options)
    {
        _equipment = equipment;
        _imaging = imaging;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "flats.json");
        _stored = Load();
    }

    public FlatsStatus? Status { get; private set; }

    public FlatsSettings Settings
    {
        get { lock (_sync) return _stored.Settings; }
    }

    public bool Running => Status is { Running: true };

    public FlatsInfo Info => new(Running, Settings, Status);

    public void UpdateSettings(FlatsSettings settings)
    {
        if (settings.Validate() is { } problem) throw new ArgumentException(problem);
        lock (_sync) _stored = _stored with { Settings = settings };
        Save();
    }

    // ---- By hand ----

    public bool Start(FlatsRequest request, out string? error)
    {
        var settings = request.Settings ?? Settings;
        if ((error = settings.Validate()) is not null) return false;
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        if (_imaging.IsBusy) { error = "An exposure is running"; return false; }
        if (Running) { error = "Flats are already being taken"; return false; }
        if (request.Settings is { } changed) UpdateSettings(changed);

        var cts = new CancellationTokenSource();
        _run = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(request, settings, waitLimit: null, fromSequence: false, progress: null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Status = Status is { } s ? s with { Running = false, Ask = null, Phase = "Cancelled", Message = "Cancelled" } : null;
                _log.Warn("Flats cancelled");
            }
            catch (Exception ex)
            {
                Status = Status is { } s ? s with { Running = false, Ask = null, Phase = "Failed", Message = ex.Message } : null;
                _log.Error("Flats failed", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _run, null, cts);
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>Done what was asked (panel on, dimmed it, covered the scope…).</summary>
    public bool Continue() => _answer?.TrySetResult(true) == true;

    /// <summary>Skip what's being asked for: the flats, or (when covering) just the dark flats.</summary>
    public bool Skip() => _answer?.TrySetResult(false) == true;

    public void Cancel() => _run?.Cancel();

    /// <summary>
    /// For sequences: takes flats (and dark flats) with the camera as it is, waiting for someone to put the panel on.
    /// Returns false if nobody did within <paramref name="waitLimit"/> (null: wait as long as it takes) or they
    /// were skipped. Leaves the camera on Light frames.
    /// </summary>
    /// <param name="leaveCovered">Leave a flip-flat's cover closed afterwards (the end of the night); otherwise it's opened
    /// again if it was open.</param>
    public async Task<bool> TakeAndWaitAsync(string name, FlatsSettings settings, TimeSpan? waitLimit,
        Action<string>? progress, CancellationToken ct, bool leaveCovered = false)
    {
        try
        {
            return await RunAsync(new FlatsRequest(name), settings, waitLimit, fromSequence: true, progress, ct, leaveCovered);
        }
        catch (Exception ex)
        {
            Status = Status is { } s
                ? s with { Running = false, Ask = null, Phase = ex is OperationCanceledException ? "Cancelled" : "Failed", Message = ex is OperationCanceledException ? "Cancelled" : ex.Message }
                : null;
            throw;
        }
    }

    // ---- The run ----

    private async Task<bool> RunAsync(FlatsRequest request, FlatsSettings settings, TimeSpan? waitLimit, bool fromSequence,
        Action<string>? progress, CancellationToken ct, bool leaveCovered = false)
    {
        _fromSequence = fromSequence;
        // A flat panel NOVA controls: no need to ask anyone to switch it on, dim it or cover the scope.
        var panel = settings.UsePanel && _equipment.FlatPanel is { IsConnected: true, HasLight: true } p ? p : null;
        var coverWasOpen = panel is { HasCover: true, CoverClosed: false };
        var camera = _equipment.Camera is { IsConnected: true } c ? c : throw new InvalidOperationException("No camera connected");
        var target = settings.TargetPercent / 100 * FullScale;
        var name = string.IsNullOrWhiteSpace(request.Name) ? DateTime.Now.ToString("HH-mm") : request.Name.Trim();
        var tests = new List<FlatTest>();
        Status = new FlatsStatus(true, "Panel", "", null, name, Math.Round(target), null, null, 0, 0, settings.Count,
            settings.DarkFlats, tests, fromSequence, null, panel?.Name);
        void Say(string message)
        {
            Status = Status! with { Message = message };
            progress?.Invoke(message);
        }

        try
        {
            await camera.ApplyAsync(new CameraSettingsUpdate(
                Gain: camera.Controls.Gain is null ? null : request.Gain,
                Offset: camera.Controls.Offset is null ? null : request.Offset,
                Binning: camera.Controls.Binning is null ? null : request.Binning,
                FrameType: camera.Controls.FrameType is null ? null : FrameType.Flat), ct);

            // 1. The panel: switched on by NOVA, or by hand when asked.
            _log.Info($"Flats for {name}: {settings.Count} at {settings.TargetPercent:0}% brightness{(settings.DarkFlats ? ", then dark flats" : "")}" +
                      (panel is null ? "" : $", with {panel.Name}"));
            var key = Key(camera);
            double? brightness = null;
            if (panel is not null)
            {
                if (panel.HasCover)
                {
                    Say($"Closing {panel.Name}'s cover");
                    await panel.SetCoverAsync(true, ct);
                }
                lock (_sync) brightness = _stored.Brightness?.GetValueOrDefault(key) is > 0 and var b ? b : null;
                brightness ??= panel.Brightness?.Value;
                Say($"Switching {panel.Name} on");
                await panel.SetLightAsync(true, brightness, ct);
                Status = Status with { PanelBrightness = brightness };
            }
            else if (await AskAsync("Panel", "PanelOn",
                         "Put the flat panel on the scope and switch it on, then press Panel's on. Don't turn or refocus the camera.",
                         waitLimit, Say, ct) is not true)
                return Gave("the panel wasn't put on", waitLimit, Say);

            // 2. The exposure.
            double exposure;
            lock (_sync) exposure = _stored.Exposures.TryGetValue(key, out var known) ? known : 1;
            exposure = Math.Clamp(exposure, settings.MinExposure, settings.MaxExposure);
            Status = Status with { Phase = "Calibrating" };
            for (var test = 1; ; test++)
            {
                if (test > MaxTests) throw new InvalidOperationException($"Couldn't find an exposure for {settings.TargetPercent:0}% brightness in {MaxTests} tries: is the panel flickering?");
                Say($"Test frame {test}: {exposure:0.###} s");
                var median = (await _imaging.CaptureAsync(exposure, save: false, ct, keep: false)).Statistics.Median;
                tests.Add(new FlatTest(Math.Round(exposure, 4), Math.Round(median)));
                Status = Status with { Exposure = Math.Round(exposure, 4), Median = Math.Round(median), Tests = tests.ToList() };
                if (Math.Abs(median - target) <= CalibrationTolerance * target) break;

                // Brightness is proportional to exposure (a saturated frame says only "much less").
                var next = median >= FullScale * 0.98 ? exposure / 4 : exposure * target / Math.Max(median, 1);
                // A dimmable panel: change its brightness so the exposure lands comfortably inside the limits.
                if (panel?.Brightness is { } range && brightness is { } level
                    && (next < settings.MinExposure && exposure <= settings.MinExposure * 1.001 && level > range.Min
                        || next > settings.MaxExposure && exposure >= settings.MaxExposure * 0.999 && level < range.Max))
                {
                    var aim = next < settings.MinExposure ? Math.Min(settings.MaxExposure, settings.MinExposure * 4) : Math.Max(settings.MinExposure, settings.MaxExposure / 4);
                    var newLevel = Math.Clamp(Math.Round(level * next / aim), range.Min, range.Max);
                    if (newLevel == level) newLevel = Math.Clamp(level + (next < settings.MinExposure ? -1 : 1), range.Min, range.Max);
                    Say($"{(newLevel < level ? "Dimming" : "Brightening")} {panel.Name}: {level:0} → {newLevel:0}");
                    await panel.SetLightAsync(true, newLevel, ct);
                    brightness = newLevel;
                    Status = Status with { PanelBrightness = newLevel };
                    exposure = Math.Clamp(next * level / newLevel, settings.MinExposure, settings.MaxExposure);
                    continue;
                }
                if (next < settings.MinExposure && exposure <= settings.MinExposure * 1.001)
                {
                    if (panel is not null)
                        throw new InvalidOperationException($"Too bright: {median:0} at the shortest exposure ({settings.MinExposure:0.###} s), even with {panel.Name} at its dimmest");
                    if (await AskAsync("Calibrating", "Dimmer",
                            $"Too bright: {median:0} at the shortest exposure ({settings.MinExposure:0.###} s). Dim the panel or add a layer of cloth, then press Done.",
                            waitLimit, Say, ct) is not true)
                        return Gave("the panel was too bright", waitLimit, Say);
                    Status = Status with { Phase = "Calibrating" };
                    continue;
                }
                if (next > settings.MaxExposure && exposure >= settings.MaxExposure * 0.999)
                {
                    if (panel is not null)
                        throw new InvalidOperationException($"Too dim: {median:0} at the longest exposure ({settings.MaxExposure:0.#} s), even with {panel.Name} at its brightest");
                    if (await AskAsync("Calibrating", "Brighter",
                            $"Too dim: {median:0} at the longest exposure ({settings.MaxExposure:0.#} s). Turn the panel up, then press Done.",
                            waitLimit, Say, ct) is not true)
                        return Gave("the panel was too dim", waitLimit, Say);
                    Status = Status with { Phase = "Calibrating" };
                    continue;
                }
                exposure = Math.Clamp(next, settings.MinExposure, settings.MaxExposure);
            }
            lock (_sync)
            {
                _stored.Exposures[key] = exposure;
                if (brightness is { } kept) _stored = _stored with { Brightness = new Dictionary<string, double>(_stored.Brightness ?? new()) { [key] = kept } };
            }
            Save();
            _log.Info($"Flats: {exposure:0.###} s gives {Status.Median:0} ({settings.TargetPercent:0}% of full scale)");

            // 3. The flats, each checked; drifting brightness nudges the exposure.
            var folder = Path.Combine("Flats", name);
            Status = Status with { Phase = "Flats", Folder = folder };
            var taken = 0;
            var retakes = 0;
            while (taken < settings.Count)
            {
                Say($"Flat {taken + 1} of {settings.Count}: {exposure:0.###} s");
                var image = await _imaging.CaptureAsync(exposure, save: true, ct, "Flats", taken + 1, name);
                var median = image.Statistics.Median;
                Status = Status with { Median = Math.Round(median) };
                if (Math.Abs(median - target) > KeepTolerance * target)
                {
                    if (image.SavedPath is { } bad) TryDelete(bad);
                    if (++retakes > MaxRetakes) throw new InvalidOperationException($"The flats keep coming out at {median:0} instead of about {target:0}: is the panel steady?");
                    _log.Warn($"Flat {taken + 1} came out at {median:0} (aiming for {target:0}): taking it again");
                }
                else
                {
                    taken++;
                    Status = Status with { Flats = taken };
                }
                if (Math.Abs(median - target) > CalibrationTolerance * target)
                    exposure = Math.Clamp(exposure * target / Math.Max(median, 1), settings.MinExposure, settings.MaxExposure);
            }

            // 4. Dark flats.
            if (settings.DarkFlats)
            {
                // With NOVA's panel: just switch it off (on a flip-flat the closed cover keeps the light out).
                var dark = panel is not null;
                if (panel is not null)
                {
                    Say($"Switching {panel.Name} off for the dark flats");
                    await panel.SetLightAsync(false, null, ct);
                }
                if (dark || await AskAsync("Cover", "Cover",
                        "Switch the panel off and cover the scope (cap it, or leave the panel on unlit if it blocks all light), then press Covered.",
                        waitLimit, Say, ct) is true)
                {
                    await camera.ApplyAsync(new CameraSettingsUpdate(FrameType: camera.Controls.FrameType is null ? null : FrameType.Dark), ct);
                    Status = Status with { Phase = "DarkFlats" };
                    for (var i = 1; i <= settings.Count; i++)
                    {
                        Say($"Dark flat {i} of {settings.Count}: {exposure:0.###} s");
                        var image = await _imaging.CaptureAsync(exposure, save: true, ct, "Flats", i, name);
                        Status = Status with { DarkFlats = i, Median = Math.Round(image.Statistics.Median) };
                        if (i == 1 && image.Statistics.Median > 0.5 * target)
                            throw new InvalidOperationException($"The dark flat is still bright ({image.Statistics.Median:0}): is the scope covered and the panel off?");
                    }
                }
                else
                {
                    _log.Warn($"Flats for {name}: dark flats skipped");
                }
            }

            var done = $"{taken} flats{(Status.DarkFlats > 0 ? $" and {Status.DarkFlats} dark flats" : "")} at {exposure:0.###} s, in {folder}";
            Status = Status with { Running = false, Ask = null, Phase = "Done" };
            Say(done);
            _log.Info($"Flats for {name}: {done}." + (panel is null ? " You can take the panel off." : ""));
            return true;
        }
        finally
        {
            _answer = null;
            // NOVA's panel: off, and the cover back as it was (or left closed at the end of the night).
            if (panel is { IsConnected: true })
            {
                try
                {
                    await panel.SetLightAsync(false, null, CancellationToken.None);
                    if (coverWasOpen && !leaveCovered)
                    {
                        _log.Info($"Opening {panel.Name}'s cover again");
                        await panel.SetCoverAsync(false, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Couldn't put {panel.Name} back as it was: {ex.Message}");
                }
            }
            // Leave the camera ready for lights.
            if (camera.IsConnected && camera.Controls.FrameType is not null)
            {
                try
                {
                    await camera.ApplyAsync(new CameraSettingsUpdate(FrameType: FrameType.Light), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Couldn't set the camera back to light frames: {ex.Message}");
                }
            }
        }
    }

    private bool Gave(string why, TimeSpan? waitLimit, Action<string> say)
    {
        var message = Status!.Ask is null && waitLimit is { } limit
            ? $"No flats: nobody answered in {limit.TotalMinutes:0} minutes ({why})"
            : $"Flats skipped ({why})";
        Status = Status with { Running = false, Ask = null, Phase = "Skipped" };
        say(message);
        _log.Warn(message);
        return false;
    }

    /// <summary>Asks for something to be done; true when it's done, false when skipped, null when nobody answered in time.</summary>
    private async Task<bool?> AskAsync(string phase, string ask, string message, TimeSpan? waitLimit, Action<string> say, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        Status = Status! with { Phase = phase, Ask = ask };
        say(message);
        _log.Info(message);
        // Someone may be indoors while a sequence runs: tell their phone. (By hand, they're at the page.)
        if (_fromSequence) Notifications?.Notify(NotificationKind.NeedsYou, "NOVA is waiting for you: flats", message);
        using (ct.Register(() => answer.TrySetCanceled(ct)))
        {
            var waited = waitLimit is { } limit ? Task.Delay(limit, ct) : Task.Delay(Timeout.Infinite, ct);
            var first = await Task.WhenAny(answer.Task, waited);
            ct.ThrowIfCancellationRequested();
            _answer = null;
            if (first != answer.Task)
            {
                Status = Status with { Ask = null };
                return null;
            }
            var result = await answer.Task;
            Status = Status with { Ask = result ? null : Status.Ask };
            return result;
        }
    }

    /// <summary>Remembered exposures are per gain, offset, binning and filter.</summary>
    private string Key(ICamera camera)
    {
        var c = camera.Controls;
        var wheel = _equipment.FilterWheel;
        var filter = wheel is { IsConnected: true } && wheel.Position >= 1 && wheel.Position <= wheel.Names.Count ? wheel.Names[wheel.Position - 1] : "";
        return $"{camera.Name}|{c.Gain?.Value}|{c.Offset?.Value}|{c.Binning?.Value}|{filter}";
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _log.Warn($"Couldn't remove {path}: {ex.Message}");
        }
    }

    private Stored Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file), Sequence.Json) is { } stored)
                return stored with { Settings = stored.Settings ?? new FlatsSettings(), Exposures = stored.Exposures ?? new() };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.Warn($"Couldn't read {_file}: {ex.Message}");
        }
        return new Stored(new FlatsSettings(), new Dictionary<string, double>());
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_stored, Sequence.Json);
            File.WriteAllText(_file, json);
        }
        catch (IOException ex)
        {
            _log.Warn($"Couldn't save the flats settings: {ex.Message}");
        }
    }
}
