using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;
using Nova.Core.Focusing;

namespace Nova.Server.Services;

/// <summary>One filter's result while measuring offsets.</summary>
/// <param name="Position">Where autofocus put best focus, or null if it didn't succeed.</param>
public sealed record OffsetMeasurement(string Filter, int? Position, string Message);

/// <param name="Drift">How far the reference filter's focus moved between the first and last autofocus (steps): a
/// large value means the temperature changed during the measurement, which the offsets are corrected for.</param>
public sealed record OffsetMeasureStatus(bool Running, string Message, IReadOnlyList<OffsetMeasurement> Results, int? Drift);

/// <summary>A focuser's per-filter focus offsets, in steps from the reference filter.</summary>
/// <param name="Enabled">Filter changes move the focuser by the difference between the two filters' offsets.</param>
public sealed record FocusOffsetsInfo(
    bool Enabled, string? Reference, IReadOnlyDictionary<string, int> Offsets, OffsetMeasureStatus? Measuring);

/// <summary>
/// Per-filter focus offsets: filters focus at slightly different positions, so instead of refocusing after every
/// filter change, the focuser moves by the known difference. Offsets are relative to a reference filter (the first
/// one unless chosen), typed in or measured: <see cref="StartMeasure"/> autofocuses on the reference, on every
/// other filter, then on the reference again, and corrects for any drift in between. Kept per focuser in
/// focus-offsets.json. Every filter change NOVA makes (the Filter wheel panel, sequences) goes through
/// <see cref="ChangeFilterAsync"/>.
/// </summary>
public sealed class FocusOffsetService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly EquipmentService _equipment;
    private readonly AutofocusService _autofocus;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly Dictionary<string, Stored> _focusers;
    private CancellationTokenSource? _measuring;
    private OffsetMeasureStatus? _measure;

    private sealed record Stored(bool Enabled, string? Reference, Dictionary<string, int> Offsets);

    public FocusOffsetService(EquipmentService equipment, AutofocusService autofocus, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _autofocus = autofocus;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "focus-offsets.json");
        _focusers = Load();
    }

    /// <summary>The filter names as the wheel shows them (an unnamed slot is "Slot n").</summary>
    public static string FilterName(IFilterWheel wheel, int slot) =>
        wheel.Names.ElementAtOrDefault(slot - 1) is { Length: > 0 } name ? name : $"Slot {slot}";

    public FocusOffsetsInfo? For(string focuser)
    {
        lock (_sync)
        {
            var stored = _focusers.GetValueOrDefault(focuser);
            if (stored is null && _measure is null) return null;
            return new FocusOffsetsInfo(stored?.Enabled ?? false, stored?.Reference,
                new Dictionary<string, int>(stored?.Offsets ?? []), _measure);
        }
    }

    /// <summary>Saves a focuser's offsets (typed in), and whether filter changes apply them.</summary>
    public void Save(string focuser, bool enabled, string? reference, IReadOnlyDictionary<string, int> offsets)
    {
        if (string.IsNullOrWhiteSpace(focuser)) throw new ArgumentException("Which focuser?");
        if (offsets.Values.Any(v => Math.Abs(v) > 100_000)) throw new ArgumentException("Offsets must be within ±100000 steps");
        lock (_sync)
            _focusers[focuser] = new Stored(enabled, string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
                offsets.Where(o => o.Key.Trim().Length > 0).ToDictionary(o => o.Key.Trim(), o => o.Value));
        Persist();
        _log.Info($"{focuser}: filter offsets {(enabled ? "on" : "off")} ({string.Join(", ", offsets.Select(o => $"{o.Key} {o.Value:+0;-0;0}"))})");
    }

    /// <summary>
    /// Changes filter, then (with offsets on, for a focuser that reports its position) moves the focuser by the
    /// difference between the old and new filters' offsets. A filter without an offset leaves focus alone.
    /// </summary>
    public async Task ChangeFilterAsync(IFilterWheel wheel, int slot, CancellationToken ct)
    {
        var before = wheel.Position >= 1 ? FilterName(wheel, wheel.Position) : null;
        await wheel.MoveToAsync(slot, ct);
        var after = FilterName(wheel, slot);
        if (before is null || before == after || _equipment.Focuser is not { IsConnected: true, CanMoveAbsolute: true } focuser) return;

        int? delta;
        lock (_sync)
            delta = _focusers.GetValueOrDefault(focuser.Name) is { Enabled: true } stored
                    && stored.Offsets.TryGetValue(before, out var from) && stored.Offsets.TryGetValue(after, out var to)
                ? to - from
                : null;
        if (delta is not { } steps || steps == 0) return;
        var target = Math.Clamp(focuser.Position + steps, focuser.MinPosition, focuser.MaxPosition > focuser.MinPosition ? focuser.MaxPosition : int.MaxValue);
        _log.Info($"{before} → {after}: focuser {steps:+0;-0} steps (filter offsets), to {target}");
        await FocuserMoves.MoveToAsync(focuser, target, _autofocus.Settings.Backlash, _autofocus.Settings.Approach, ct);
    }

    public const int DefaultPasses = 3;

    /// <summary>
    /// Starts measuring the offsets in the background (progress in <see cref="FocusOffsetsInfo.Measuring"/>):
    /// <paramref name="passes"/> times through every filter, averaged.
    /// </summary>
    public bool StartMeasure(out string? error, int passes = DefaultPasses)
    {
        if (passes is < 1 or > 5) { error = "Passes must be between 1 and 5"; return false; }
        if (_equipment.FilterWheel is not { IsConnected: true } wheel || wheel.SlotCount < 2) { error = "No filter wheel connected"; return false; }
        if (_equipment.Focuser is not { IsConnected: true, CanMoveAbsolute: true } focuser)
        {
            error = "Measuring offsets needs a focuser that reports its position";
            return false;
        }
        if (_equipment.Camera is not { IsConnected: true }) { error = "No camera connected"; return false; }
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_measuring is not null) { error = "Already measuring"; return false; }
            _measuring = cts = new CancellationTokenSource();
        }
        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await MeasureAsync(wheel, focuser, passes, cts.Token);
            }
            finally
            {
                lock (_sync) _measuring = null;
                cts.Dispose();
            }
        });
        return true;
    }

    public void CancelMeasure()
    {
        lock (_sync) _measuring?.Cancel();
        _autofocus.Cancel();
    }

    /// <summary>Measures and waits (tests, automation).</summary>
    public async Task<OffsetMeasureStatus> MeasureAndWaitAsync(CancellationToken ct, int passes = DefaultPasses)
    {
        if (!StartMeasure(out var error, passes)) throw new InvalidOperationException(error);
        await using var _ = ct.Register(CancelMeasure);
        while (true)
        {
            lock (_sync) if (_measuring is null) break;
            await Task.Delay(50, CancellationToken.None);
        }
        return _measure!;
    }

    private async Task MeasureAsync(IFilterWheel wheel, IFocuser focuser, int passes, CancellationToken ct)
    {
        var filters = Enumerable.Range(1, wheel.SlotCount).Select(slot => (Slot: slot, Name: FilterName(wheel, slot))).ToList();
        string? chosen;
        lock (_sync) chosen = _focusers.GetValueOrDefault(focuser.Name)?.Reference;
        var reference = filters.FirstOrDefault(f => f.Name == chosen) is { Name: not null } r && chosen is not null ? r : filters[0];
        // Passes of: every other filter, then the reference again (to follow any drift), after a first reference run.
        // A single autofocus scatters by tens of steps, as much as typical offsets, hence several passes, averaged.
        var others = filters.Where(f => f.Slot != reference.Slot).ToList();
        var order = new List<(int Slot, string Name)> { reference };
        for (var pass = 0; pass < passes; pass++) order.AddRange(others.Append(reference));
        var results = new List<OffsetMeasurement>();
        var positions = new List<int?>();

        void Report(string message)
        {
            lock (_sync) _measure = new OffsetMeasureStatus(true, message, results.ToList(), null);
        }

        _log.Info($"Measuring filter offsets for {focuser.Name}: autofocus on {string.Join(", ", order.Select(f => f.Name))}");
        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var (slot, name) = order[i];
                Report($"Autofocus {i + 1} of {order.Count}: {name}");
                await wheel.MoveToAsync(slot, ct); // straight to the filter: offsets are what's being measured
                var status = await _autofocus.RunAsync(ct);
                var position = status.Succeeded == true ? status.BestPosition : null;
                positions.Add(position);
                results.Add(new OffsetMeasurement(name, position, position is { } p ? $"best focus {p}" : status.Message));
            }

            // Each filter against the reference focus at that moment: within each pass, between the reference runs
            // that open and close it (focus drifts as the temperature changes).
            var span = others.Count + 1;
            var samples = others.ToDictionary(f => f.Name, _ => new List<double>());
            var drifts = new List<int>();
            for (var pass = 0; pass < passes; pass++)
            {
                var (open, close) = (pass * span, (pass + 1) * span);
                var (before, after) = (positions[open], positions[close]);
                if (before is { } b0 && after is { } a0) drifts.Add(a0 - b0);
                if (before is null && after is null) continue;
                for (var i = open + 1; i < close; i++)
                {
                    if (positions[i] is not { } at) continue;
                    var referenceThen = (before, after) switch
                    {
                        ({ } f, { } l) => f + (l - f) * (i - open) / (double)span,
                        ({ } f, null) => f,
                        (null, { } l) => l,
                        _ => 0,
                    };
                    samples[order[i].Name].Add(at - referenceThen);
                }
            }
            if (positions.Where((_, i) => i % span == 0).All(p => p is null))
                throw new InvalidOperationException($"Autofocus on the reference filter ({reference.Name}) didn't succeed: nothing to measure from");
            int? drift = drifts.Count > 0 ? drifts.Sum() : null;

            var offsets = new Dictionary<string, int> { [reference.Name] = 0 };
            var spread = new List<string>();
            foreach (var (filter, values) in samples.Where(s => s.Value.Count > 0))
            {
                offsets[filter] = (int)Math.Round(values.Average());
                spread.Add($"{filter} {offsets[filter]:+0;-0;0} ({string.Join(", ", values.Select(v => $"{v:+0;-0;0}"))})");
            }

            lock (_sync)
            {
                var stored = _focusers.GetValueOrDefault(focuser.Name);
                // Keep offsets typed in for filters that couldn't be measured this time.
                var merged = new Dictionary<string, int>(stored?.Offsets ?? []);
                foreach (var (filter, offset) in offsets) merged[filter] = offset;
                _focusers[focuser.Name] = new Stored(true, reference.Name, merged);
                var missing = results.Count(r => r.Position is null);
                _measure = new OffsetMeasureStatus(false,
                    $"Measured {offsets.Count - 1} of {filters.Count - 1} offsets: {string.Join("; ", spread)}" +
                    (missing > 0 ? $". {missing} autofocus runs failed" : "") +
                    (drift is { } d && Math.Abs(d) > 0 ? $"; focus drifted {d:+0;-0} steps meanwhile, which they're corrected for" : "") +
                    ". Filter changes now apply them.",
                    results.ToList(), drift);
            }
            Persist();
            _log.Info($"{focuser.Name}: filter offsets {string.Join(", ", offsets.Select(o => $"{o.Key} {o.Value:+0;-0;0}"))}" +
                      (drift is { } dr ? $" (drift {dr:+0;-0;0} steps)" : ""));
            // The last run leaves the reference filter in, at its best focus.
        }
        catch (OperationCanceledException)
        {
            lock (_sync) _measure = new OffsetMeasureStatus(false, "Cancelled", results.ToList(), null);
            _log.Warn("Measuring filter offsets cancelled");
        }
        catch (Exception ex)
        {
            lock (_sync) _measure = new OffsetMeasureStatus(false, ex.Message, results.ToList(), null);
            _log.Error("Measuring filter offsets failed", ex);
        }
    }

    private Dictionary<string, Stored> Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Dictionary<string, Stored>>(File.ReadAllText(_file), Json) ?? [];
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return [];
    }

    private void Persist()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_focusers, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }
}
