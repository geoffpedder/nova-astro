using System.Text.Json;
using System.Text.Json.Serialization;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>Where to point, J2000, as typed (e.g. "00:42:44", "+41:16:09") or decimal hours/degrees.</summary>
/// <param name="Rotation">The framing's camera angle (position angle of the frame's top, degrees), from the target planner; null: any.</param>
public sealed record SequenceTarget(string Name, string Ra, string Dec, double? Rotation = null)
{
    public EquatorialCoordinates Coordinates => EquatorialCoordinates.Parse(Ra, Dec);
}

/// <summary>One line of the exposure plan: <see cref="Count"/> frames of <see cref="ExposureSeconds"/> through a filter.</summary>
/// <param name="Filter">Filter name as the wheel knows it; null to leave the wheel alone.</param>
/// <param name="Gain">Null keeps the camera's current setting; likewise Offset and Binning.</param>
public sealed record SequenceRow(
    string? Filter, double ExposureSeconds, int Count,
    double? Gain = null, double? Offset = null, int? Binning = null, FrameType FrameType = FrameType.Light, bool Enabled = true);

/// <param name="CoolTo">Sensor temperature to cool to (°C) before imaging, when <see cref="CoolCamera"/>.</param>
/// <param name="CenterTarget">Slew to the target and plate solve until centred.</param>
/// <param name="MatchRotation">After centring on a target with a planned rotation, ask for the camera to be turned to
/// it (and wait a while for that).</param>
public sealed record SequenceStart(
    bool CoolCamera = true, double CoolTo = -10, bool CenterTarget = true, bool Autofocus = true, bool StartGuiding = true,
    bool MatchRotation = true);

/// <param name="EveryFrames">Dither after this many frames; 0 = never.</param>
public sealed record SequenceDither(int EveryFrames = 3, double Pixels = 5, bool RaOnly = false,
    double SettlePixels = 1.5, double SettleSeconds = 10, double SettleTimeout = 60);

/// <summary>When to refocus between frames. Null or 0 turns a trigger off.</summary>
/// <param name="TemperatureChange">°C the focuser's temperature has moved since the last autofocus.</param>
/// <param name="HfrIncreasePercent">How much worse HFR may get than just after the last autofocus (same filter).</param>
public sealed record SequenceAutofocus(
    bool AfterFilterChange = false, double? TemperatureChange = 2, double? HfrIncreasePercent = 15, int? EveryFrames = null);

/// <summary>
/// Meridian flip for German equatorial mounts: once the target is <see cref="MinutesAfterMeridian"/> past the
/// meridian, stop guiding, GoTo the target again (the mount moves to the other side of the pier), re-centre and
/// start guiding again. A frame that would run beyond <see cref="MaxMinutesAfterMeridian"/> isn't started:
/// the sequence waits for the flip instead. Set the maximum within what your mount can track past the meridian.
/// </summary>
public sealed record SequenceMeridianFlip(
    bool Enabled = true, double MinutesAfterMeridian = 5, double MaxMinutesAfterMeridian = 15,
    bool Recenter = true, bool AutofocusAfter = false, double SettleSeconds = 5);

/// <summary>
/// Flats (and dark flats) with a panel put on by hand, using the flats settings: whenever the camera is about to be
/// turned for the next target's rotation, and at the end of the night (before warming the camera). Between targets
/// they're skipped if nobody puts the panel on within a while; at the end NOVA waits until you do (or skip them).
/// </summary>
public sealed record SequenceFlats(bool Enabled = false);

/// <param name="WarmCamera">Warm the sensor gradually, then turn the cooler off.</param>
/// <param name="StopAt">When the night ends for every target: "AstronomicalDawn" (the default), "NauticalDawn",
/// "Sunrise", a local time ("05:30") or "Never". No frame is started that would run past it; targets still to come
/// are skipped, then the end steps run.</param>
public sealed record SequenceEnd(bool StopGuiding = true, bool WarmCamera = false, bool Park = false, string? StopAt = SequenceEnd.AstronomicalDawn)
{
    public const string AstronomicalDawn = "AstronomicalDawn";
    public const string NauticalDawn = "NauticalDawn";
    public const string Sunrise = "Sunrise";
    public const string Never = "Never";

    public static bool IsValidStop(string? stopAt) =>
        string.IsNullOrWhiteSpace(stopAt) || stopAt is AstronomicalDawn or NauticalDawn or Sunrise or Never
        || SequenceTargetPlan.TryParseClock(stopAt, out _);

    /// <summary>
    /// When a sequence started at <paramref name="startUtc"/> stops for the night, and how to say it ("astronomical
    /// dawn (05:12)"); null for Never, or a dawn without a site. Always after the start: one started after this
    /// morning's dawn (e.g. testing in twilight) runs until the next one. Where the Sun doesn't get 18° down (short
    /// summer nights) astronomical dawn falls back to nautical dawn, then sunrise.
    /// </summary>
    public static (DateTime Utc, string Label)? NightEnd(string? stopAt, DateTime startUtc, GeoLocation? site)
    {
        if (stopAt is null or "" or Never) return null;
        if (SequenceTargetPlan.TryParseClock(stopAt, out var clock))
        {
            var utc = SequenceTargetPlan.ClockToUtc(clock, startUtc.ToLocalTime());
            if (utc <= startUtc) utc = utc.AddDays(1);
            return (utc, $"the night's stop time ({stopAt.Trim()})");
        }
        if (site is not { } at) return null;

        DateTime? Pick(NightTimes night) => stopAt switch
        {
            Sunrise => night.Sunrise,
            NauticalDawn => night.NauticalDawn ?? night.Sunrise,
            _ => night.AstronomicalDawn ?? night.NauticalDawn ?? night.Sunrise,
        };
        if (Night.Around(startUtc, at) is not { } tonight) return null;
        var end = Pick(tonight);
        if (end <= startUtc && Night.Around(tonight.Sunrise.AddMinutes(1), at) is { } next) end = Pick(next);
        if (end is not { } when || when <= startUtc) return null;
        var name = stopAt switch { Sunrise => "sunrise", NauticalDawn => "nautical dawn", _ => "astronomical dawn" };
        return (when, $"{name} ({when.ToLocalTime():HH:mm})");
    }
}

public enum SequenceOrder
{
    /// <summary>All of the first row, then all of the second…</summary>
    ByRow,
    /// <summary>One frame from each row in turn (L, R, G, B, L, R…), so a cloudy end still leaves a full set.</summary>
    Cycle,
}

/// <summary>
/// One target of a night's plan: where, which frames, and when.
/// </summary>
/// <param name="StartAfter">Local time ("22:30"): wait until then before starting this target.</param>
/// <param name="StopAt">Local time ("01:15"): move on to the next target then (a frame that would run past it isn't started).</param>
/// <param name="MinAltitude">Degrees: wait until the target is this high, and move on once it sinks below. Without it,
/// the horizon.</param>
/// <param name="LiveStack">Live stack its light frames (a stack per filter), saved when the target ends.</param>
public sealed record SequenceTargetPlan(
    SequenceTarget? Target, List<SequenceRow> Rows, SequenceOrder Order = SequenceOrder.ByRow,
    string? StartAfter = null, string? StopAt = null, double? MinAltitude = null, bool LiveStack = false)
{
    /// <summary>"22:30" or "1:15" as a time of day.</summary>
    public static bool TryParseClock(string? text, out TimeSpan time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(text)
               && TimeSpan.TryParseExact(text.Trim(), ["h\\:mm", "hh\\:mm"], System.Globalization.CultureInfo.InvariantCulture, out time)
               && time < TimeSpan.FromDays(1);
    }

    /// <summary>
    /// The next local occurrence of a time of day, as UTC: within the night around <paramref name="nowLocal"/>
    /// (12 hours back to 12 hours ahead), so "01:30" at 21:00 means tonight after midnight and "20:00" means an
    /// hour ago.
    /// </summary>
    public static DateTime ClockToUtc(TimeSpan time, DateTime nowLocal)
    {
        var at = nowLocal.Date + time;
        if (at < nowLocal - TimeSpan.FromHours(12)) at = at.AddDays(1);
        else if (at > nowLocal + TimeSpan.FromHours(12)) at = at.AddDays(-1);
        return DateTime.SpecifyKind(at, DateTimeKind.Local).ToUniversalTime();
    }
}

/// <summary>
/// A night's imaging plan: start-up steps, one or more targets each with its frames (and optional times and
/// altitude limit), and what to do at the end. Older single-target plans (<see cref="Target"/>, <see cref="Rows"/>,
/// <see cref="Order"/>) still load: they're one target.
/// </summary>
public sealed record Sequence(
    string Name, SequenceTarget? Target, SequenceStart Start, List<SequenceRow> Rows,
    SequenceOrder Order = SequenceOrder.ByRow, SequenceDither? Dither = null, SequenceAutofocus? Autofocus = null,
    SequenceEnd? End = null, SequenceMeridianFlip? MeridianFlip = null, List<SequenceTargetPlan>? Targets = null,
    SequenceFlats? Flats = null, bool LiveStack = false)
{
    /// <summary>The targets, in order (a single-target plan is one).</summary>
    public IReadOnlyList<SequenceTargetPlan> Plans =>
        Targets is { Count: > 0 } targets ? targets : [new SequenceTargetPlan(Target, Rows, Order, LiveStack: LiveStack)];

    /// <summary>This plan narrowed to one of its targets (its shared settings, that target's frames).</summary>
    public Sequence For(SequenceTargetPlan plan) =>
        this with { Target = plan.Target, Rows = plan.Rows, Order = plan.Order, Targets = null, LiveStack = plan.LiveStack };

    /// <summary>What this target's frames are live stacked as: its name.</summary>
    public string StackName => Target?.Name ?? Name;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SequenceDither DitherSettings => Dither ?? new SequenceDither();
    public SequenceAutofocus AutofocusSettings => Autofocus ?? new SequenceAutofocus();
    public SequenceEnd EndSettings => End ?? new SequenceEnd();
    public SequenceMeridianFlip MeridianFlipSettings => MeridianFlip ?? new SequenceMeridianFlip();
    public SequenceFlats FlatsSettings => Flats ?? new SequenceFlats();

    /// <summary>What's wrong with this plan, or null.</summary>
    public string? Validate()
    {
        var plans = Plans;
        for (var k = 0; k < plans.Count; k++)
        {
            var plan = plans[k];
            var which = plans.Count > 1 ? $"{plan.Target?.Name ?? $"Target {k + 1}"}: " : "";
            string Say(string message) => which.Length > 0 ? which + message : char.ToUpperInvariant(message[0]) + message[1..];
            if (plan.Rows.Count == 0 || !plan.Rows.Any(r => r.Enabled && r.Count > 0)) return Say($"add at least one row of frames");
            foreach (var (row, i) in plan.Rows.Select((r, i) => (r, i + 1)))
            {
                if (row.ExposureSeconds is < 0 or > 3600) return Say($"row {i}: exposure must be between 0 and 3600 seconds");
                if (row.Count is < 0 or > 10_000) return Say($"row {i}: count must be between 0 and 10000");
                if (row.Binning is < 1 or > 8) return Say($"row {i}: binning must be between 1 and 8");
            }
            if (plan.Target is { } t)
            {
                if (string.IsNullOrWhiteSpace(t.Name)) return Say($"give the target a name");
                try
                {
                    _ = t.Coordinates;
                }
                catch (FormatException ex)
                {
                    return Say($"target coordinates: {ex.Message}");
                }
            }
            else if (plans.Count > 1)
            {
                return $"Target {k + 1} needs coordinates (only a single-target plan can image wherever the scope points)";
            }
            if (Start.CenterTarget && plan.Target is null) return "Centring needs a target";
            if (plan.StartAfter is { Length: > 0 } a && !SequenceTargetPlan.TryParseClock(a, out _)) return Say($"start after must be a time like 22:30");
            if (plan.StopAt is { Length: > 0 } b && !SequenceTargetPlan.TryParseClock(b, out _)) return Say($"stop at must be a time like 01:15");
            if (plan.MinAltitude is < -10 or > 89) return Say($"minimum altitude must be between -10 and 89°");
        }
        if (!SequenceEnd.IsValidStop(EndSettings.StopAt)) return "The night's stop must be a dawn, sunrise, a time like 05:30, or never";
        if (Start.CoolCamera && Start.CoolTo is < -50 or > 30) return "Cooling target must be between -50 and 30 °C";
        if (DitherSettings.EveryFrames < 0 || DitherSettings.Pixels is <= 0 or > 100) return "Dither settings are out of range";
        var flip = MeridianFlipSettings;
        if (flip.MinutesAfterMeridian is < 0 or > 120 || flip.MaxMinutesAfterMeridian is < 0 or > 240)
            return "Meridian flip minutes are out of range";
        if (flip.MaxMinutesAfterMeridian < flip.MinutesAfterMeridian)
            return "The meridian flip's latest time must be after its flip time";
        return null;
    }

    /// <summary>Total frames and exposure time planned (enabled rows).</summary>
    public (int Frames, double Seconds) Totals =>
        (Plans.SelectMany(p => p.Rows).Where(r => r.Enabled).Sum(r => r.Count),
         Plans.SelectMany(p => p.Rows).Where(r => r.Enabled).Sum(r => r.Count * r.ExposureSeconds));
}
