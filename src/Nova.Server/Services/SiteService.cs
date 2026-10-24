using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>A named observing location, e.g. "Home" or "Dark site". Longitude is +E, -180..180.</summary>
public sealed record Site(string Name, double Latitude, double Longitude, double Elevation)
{
    public GeoLocation Location => new(Latitude, Longitude, Elevation);
}

/// <param name="MountLocation">Where the connected mount says it is.</param>
/// <param name="MountMismatch">The mount reports a location that isn't the active site.</param>
/// <param name="ServerUtc">The mini PC's clock, so the browser can spot a wrong clock.</param>
/// <param name="SunAvoidance">Degrees around the Sun the telescope won't be pointed while it's up.</param>
/// <param name="SunProtection">Whether Sun protection is on (off for indoor testing; the degrees are kept).</param>
/// <param name="SunAltitude">The Sun's altitude now, when the site is known.</param>
/// <param name="SettleSeconds">How long the mount is left to settle after every GoTo, before anything else.</param>
/// <param name="LowerLimit">The lowest altitude the telescope is pointed at (degrees; 0: the horizon).</param>
/// <param name="UpperLimit">The highest (90: no limit). Near the zenith the camera end swings down towards the tripod.</param>
public sealed record SiteStatus(
    IReadOnlyList<Site> Sites, string? Active, GeoLocation? MountLocation, bool MountMismatch, DateTime ServerUtc,
    double SunAvoidance = SiteService.DefaultSunAvoidance, double? SunAltitude = null, bool SunProtection = true,
    double SettleSeconds = SiteService.DefaultSettleSeconds, double LowerLimit = 0, double UpperLimit = 90);

/// <summary>
/// Remembers observing sites and tells the mount where it is and what time it is: on every mount connect
/// and whenever the active site changes. The mount needs both for alt/az, the meridian and parking.
/// </summary>
public sealed class SiteService
{
    private sealed record Settings(
        string? Active = null, List<Site>? Sites = null, double? SunAvoidance = null, bool? SunProtectionOff = null, double? SettleSeconds = null,
        double? LowerLimit = null, double? UpperLimit = null);

    /// <summary>Whether limits have been set (until then only the horizon is checked, and only where it always was).</summary>
    private bool LimitsSet
    {
        get
        {
            lock (_sync) return _settings.LowerLimit is not null || _settings.UpperLimit is not null;
        }
    }

    /// <summary>The lowest altitude the telescope is pointed at (degrees): the horizon unless set.</summary>
    public double LowerLimit
    {
        get
        {
            lock (_sync) return _settings.LowerLimit ?? 0;
        }
    }

    /// <summary>The highest altitude (degrees): none (90) unless set.</summary>
    public double UpperLimit
    {
        get
        {
            lock (_sync) return _settings.UpperLimit ?? 90;
        }
    }

    /// <summary>
    /// Sets the altitude limits, which every GoTo is checked against and every move is watched for, and gives them to
    /// the mount too where its driver has altitude limits (ZWO's), so they hold even without NOVA.
    /// </summary>
    public async Task SetLimitsAsync(double lower, double upper, CancellationToken ct)
    {
        if (double.IsNaN(lower) || lower is < -5 or > 60) throw new ArgumentException("The lower limit must be between -5 and 60 degrees");
        if (double.IsNaN(upper) || upper is < 45 or > 90) throw new ArgumentException("The upper limit must be between 45 and 90 degrees");
        if (upper - lower < 20) throw new ArgumentException("Leave at least 20 degrees between the lower and upper limits");
        lock (_sync) _settings = _settings with { LowerLimit = lower, UpperLimit = upper };
        Save();
        _log.Info(upper >= 90
            ? $"Limits: the telescope stays above {lower:0}°"
            : $"Limits: the telescope stays between {lower:0}° and {upper:0}° altitude");
        if (_equipment.Mount is { IsConnected: true } mount) await GiveMountLimitsAsync(mount, ct);
    }

    /// <summary>The limits into the mount's own (firmware) altitude limits, when they've been set and it has them.</summary>
    private async Task GiveMountLimitsAsync(IMount mount, CancellationToken ct)
    {
        double? lower, upper;
        lock (_sync) (lower, upper) = (_settings.LowerLimit, _settings.UpperLimit);
        if (lower is null && upper is null) return; // never set: leave the driver's own
        try
        {
            if (await mount.SetAltitudeLimitsAsync(lower ?? 0, upper ?? 90, ct))
                _log.Info($"{mount.Name}: its own altitude limits set to {lower ?? 0:0}° to {upper ?? 90:0}°");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Couldn't set {mount.Name}'s own altitude limits ({ex.Message}); NOVA still keeps to them");
        }
    }

    /// <summary>
    /// How long the mount is left after a GoTo before NOVA exposes, solves or moves on (seconds): even a quick mount
    /// rings for a moment after a long slew, which trails the stars in a short solve frame.
    /// </summary>
    public const double DefaultSettleSeconds = 3;

    /// <summary>The settle time until the user sets one; the tests set it to 0 so their slews don't wait.</summary>
    public static double InitialSettleSeconds { get; set; } = DefaultSettleSeconds;

    public double SettleSeconds
    {
        get
        {
            lock (_sync) return _settings.SettleSeconds ?? InitialSettleSeconds;
        }
    }

    public Task SetSettleSecondsAsync(double seconds, CancellationToken ct)
    {
        if (seconds is < 0 or > 60 || double.IsNaN(seconds)) throw new ArgumentException("Settle time must be between 0 and 60 seconds");
        lock (_sync) _settings = _settings with { SettleSeconds = seconds };
        Save();
        _log.Info(seconds == 0 ? "No settling after slews" : $"The mount settles {seconds:0.#} s after each slew");
        return Task.CompletedTask;
    }

    /// <summary>How far from the Sun the telescope stays by default (degrees).</summary>
    public const double DefaultSunAvoidance = 30;

    /// <summary>
    /// The protection used until the user sets one: <see cref="DefaultSunAvoidance"/>. Only the tests change it
    /// (to 0), since where the Sun is depends on when they run.
    /// </summary>
    public static double InitialSunAvoidance { get; set; } = DefaultSunAvoidance;

    /// <summary>The Sun counts as "up" (dangerous) from this altitude: a little below the horizon, to be safe.</summary>
    public const double SunUpAltitude = -3;

    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private Settings _settings;
    private string? _appliedTo; // mount whose current connection already has the site

    public SiteService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "sites.json");
        _settings = Load();
        equipment.DevicesChanged += (_, _) => ApplyOnNewConnection();
        _clockWatch = new Timer(_ => _ = WatchMountAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        _sunWatch = new Timer(_ => _ = WatchMovingAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
    }

    private int _watching;

    private async Task WatchMovingAsync()
    {
        // The timer doesn't wait for the last check: one at a time, or rates come from samples a moment apart.
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            await HaltIfHeadingIntoSunAsync();
            await HaltIfLeavingLimitsAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _watching, 0);
        }
    }

    private (double Altitude, DateTime Utc, double Rate)? _lastMovingAltitude; // the last reading that changed

    /// <summary>
    /// The same backstop for the altitude limits: while the mount moves (a GoTo, a button, polar alignment, another
    /// program), if it's about to go below the lower limit or above the upper one, and heading further out, abort.
    /// Moving back inside is left alone, so the buttons can bring it back.
    /// </summary>
    private async Task HaltIfLeavingLimitsAsync()
    {
        if (!LimitsSet || _equipment.Mount is not { IsConnected: true } mount || mount.State != MountState.Slewing || KnownSite() is not { } site)
        {
            _lastMovingAltitude = null;
            return;
        }
        var (lower, upper) = (LowerLimit, UpperLimit);
        var now = DateTime.UtcNow;
        var altitude = Horizon.AltAz(Horizon.ToVector(mount.Coordinates, now, site)).Altitude;
        // Drivers report the position in steps (about once a second), so the rate comes from readings that changed.
        if (_lastMovingAltitude is not { } last)
        {
            _lastMovingAltitude = (altitude, now, 0);
            return;
        }
        if (Math.Abs(altitude - last.Altitude) > 0.001 && (now - last.Utc).TotalSeconds >= 0.05)
            _lastMovingAltitude = last = (altitude, now, (altitude - last.Altitude) / (now - last.Utc).TotalSeconds);
        var rate = last.Rate;
        // Where it is by now (the reading lags), and half a second on: stop before the limit, not after it.
        var ahead = last.Altitude + rate * ((now - last.Utc).TotalSeconds + 0.5);
        var below = rate < -0.05 && ahead < lower;
        var above = rate > 0.05 && ahead > upper && upper < 90;
        if (!below && !above || Interlocked.Exchange(ref _halting, 1) == 1) return;
        try
        {
            _equipment.CancelMountOperation();
            await mount.AbortAsync(CancellationToken.None);
            _log.Warn(below
                ? $"Stopped {mount.Name}: it was heading below the lower limit of {lower:0}° ({altitude:0}° up, going down {-rate:0.#}° a second). Move it back up before going on."
                : $"Stopped {mount.Name}: it was heading above the upper limit of {upper:0}° ({altitude:0}° up), where the camera can reach the tripod. Move it back down before going on.");
        }
        catch (Exception ex)
        {
            _log.Error($"Couldn't stop {mount.Name} at the altitude limit", ex);
        }
        finally
        {
            _lastMovingAltitude = null;
            Interlocked.Exchange(ref _halting, 0);
        }
    }

    private readonly Timer _sunWatch;
    private (double Distance, DateTime Utc)? _lastMovingSunDistance;
    private int _halting;

    /// <summary>
    /// The backstop for every movement, whoever started it (NOVA, the hand controller, another program): while
    /// the mount moves, if it's inside the Sun zone and getting closer to the Sun, abort. It stops near the zone's
    /// edge, far from the Sun itself; moving out of the zone is left alone.
    /// </summary>
    private async Task HaltIfHeadingIntoSunAsync()
    {
        if (_equipment.Mount is not { IsConnected: true } mount || mount.State is not (MountState.Slewing or MountState.Parking))
        {
            _lastMovingSunDistance = null;
            return;
        }
        var limit = SunAvoidance;
        if (limit <= 0 || SunAltitude is { } up && up < SunUpAltitude) return;
        var now = DateTime.UtcNow;
        var distance = Precession.Separation(mount.Coordinates, SolarSystem.Sun(now));
        // How fast it's closing on the Sun, to stop early enough that braking (and the next check) stays outside.
        var closing = _lastMovingSunDistance is { } last && now > last.Utc
            ? (last.Distance - distance) / (now - last.Utc).TotalSeconds
            : 0;
        _lastMovingSunDistance = (distance, now);
        if (closing <= 0.05 || distance - closing * 0.5 >= limit || Interlocked.Exchange(ref _halting, 1) == 1) return;
        try
        {
            _equipment.CancelMountOperation();
            await mount.AbortAsync(CancellationToken.None);
            _log.Warn($"Stopped {mount.Name}: it was slewing towards the Sun ({distance:0}° away; Sun protection keeps it {limit:0}° away). " +
                      "Move it away from the Sun before going on.");
        }
        catch (Exception ex)
        {
            _log.Error($"Couldn't stop {mount.Name} heading towards the Sun", ex);
        }
        finally
        {
            _lastMovingSunDistance = null;
            Interlocked.Exchange(ref _halting, 0);
        }
    }

    /// <summary>Whether a straight move from where the mount points to a JNow position stays clear of the Sun zone.</summary>
    public bool PathIsClear(IMount mount, EquatorialCoordinates targetJNow)
    {
        var limit = SunAvoidance;
        if (limit <= 0 || KnownSite() is not { } site || SunAltitude is { } up && up < SunUpAltitude) return true;
        var now = DateTime.UtcNow;
        return SunSafeRoute.DirectIsSafe(SunSafeRoute.At(mount.Coordinates, now, site), SunSafeRoute.At(targetJNow, now, site),
            SunSafeRoute.At(SolarSystem.Sun(now), now, site), limit);
    }

    /// <summary>
    /// A GoTo that keeps clear of the Sun on the way, not just at the end: when the direct path could pass
    /// through the Sun zone, it goes round by the pole (see <see cref="SunSafeRoute"/>). Every GoTo NOVA makes
    /// goes through here. The target itself must already have been checked (<see cref="EnsureAwayFromSun"/>).
    /// </summary>
    public async Task SlewSafelyAsync(IMount mount, EquatorialCoordinates targetJNow, CancellationToken ct)
    {
        if (LimitsSet && OutsideLimits(Precession.JNowToJ2000(targetJNow, DateTime.UtcNow)) is { } outside) throw new InvalidOperationException(outside);
        await SlewAroundTheSunAsync(mount, targetJNow, ct);
        // Then let it settle: everything that follows a GoTo (a solve frame, the next target's first frame) wants it still.
        if (SettleSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(SettleSeconds), ct);
    }

    private async Task SlewAroundTheSunAsync(IMount mount, EquatorialCoordinates targetJNow, CancellationToken ct)
    {
        var limit = SunAvoidance;
        var site = KnownSite();
        if (limit <= 0 || site is null || SunAltitude is { } up && up < SunUpAltitude)
        {
            await SlewResendingSiteAsync(mount, targetJNow, ct);
            return;
        }
        var now = DateTime.UtcNow;
        var route = SunSafeRoute.Plan(SunSafeRoute.At(mount.Coordinates, now, site), SunSafeRoute.At(targetJNow, now, site),
            SunSafeRoute.At(SolarSystem.Sun(now), now, site), limit, site.Latitude)
            ?? throw new InvalidOperationException(
                $"Every way {mount.Name} could slew there passes within {limit:0}° of the Sun. Move it by hand to the far side of the pole first, " +
                "or wait until the Sun has moved on.");
        if (route.Count > 1)
            _log.Info($"{mount.Name}: going round the Sun (by Dec {route[0].DecDegrees:+0;-0}°) to keep {limit:0}° from it");
        for (var i = 0; i < route.Count - 1; i++)
            await SlewResendingSiteAsync(mount, SunSafeRoute.ToJNow(route[i], DateTime.UtcNow, site), ct);
        await SlewResendingSiteAsync(mount, targetJNow, ct);
    }

    /// <summary>
    /// A GoTo that, if the mount refuses it, sends the site and time once more and tries again. ZWO mounts forget
    /// that they've been told where they are when powered off, while their driver still shows the stored location,
    /// and refuse every GoTo until it's set again.
    /// </summary>
    private async Task SlewResendingSiteAsync(IMount mount, EquatorialCoordinates targetJNow, CancellationToken ct)
    {
        try
        {
            await mount.SlewAsync(targetJNow, ct);
        }
        catch (InvalidOperationException) when (!ct.IsCancellationRequested && SiteToSend(mount) is { } where)
        {
            _log.Info($"{mount.Name} refused the GoTo: sending it the site and time again, then trying once more");
            await mount.SetSiteAsync(where, DateTimeOffset.Now, ct);
            lock (_sync) _locationSentUtc = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await mount.SlewAsync(targetJNow, ct);
        }
    }

    /// <summary>The location to give the mount: NOVA's active site, else whatever the mount remembers.</summary>
    private GeoLocation? SiteToSend(IMount mount) =>
        ActiveSite?.Location ?? (mount.Location is { } m && (m.Latitude != 0 || m.Longitude != 0) ? m : null);

    private async Task WatchMountAsync()
    {
        await CheckClockAsync();
        await EnsureMountLocationAsync();
        WarnIfPointingNearSun();
    }

    /// <summary>The distance in force: the chosen degrees, or 0 while Sun protection is switched off.</summary>
    public double SunAvoidance
    {
        get
        {
            lock (_sync) return _settings.SunProtectionOff == true ? 0 : _settings.SunAvoidance ?? InitialSunAvoidance;
        }
    }

    public bool SunProtection
    {
        get
        {
            lock (_sync) return _settings.SunProtectionOff != true;
        }
    }

    /// <summary>Switches Sun protection on or off, keeping its distance.</summary>
    public Task SetSunProtectionAsync(bool on, CancellationToken ct)
    {
        lock (_sync) _settings = _settings with { SunProtectionOff = on ? null : true };
        Save();
        if (on) _log.Info($"Sun protection on: the telescope stays {SunAvoidance:0}° from the Sun while it's up");
        else _log.Warn("Sun protection is OFF: NOVA will point the telescope anywhere, even at the Sun. Turn it back on before using the scope outdoors.");
        return Task.CompletedTask;
    }

    public Task SetSunAvoidanceAsync(double degrees, CancellationToken ct)
    {
        if (degrees is < 0 or > 90 || double.IsNaN(degrees)) throw new ArgumentException("Sun protection must be between 0 (off) and 90 degrees");
        lock (_sync) _settings = _settings with { SunAvoidance = degrees };
        Save();
        if (degrees == 0) _log.Warn("Sun protection is OFF: NOVA will point the telescope anywhere, even at the Sun");
        else _log.Info($"Sun protection: the telescope stays {degrees:0}° from the Sun while it's up");
        return Task.CompletedTask;
    }

    private GeoLocation? KnownSite() =>
        ActiveSite?.Location ?? (_equipment.Mount?.Location is { } m && (m.Latitude != 0 || m.Longitude != 0) ? m : null);

    /// <summary>The Sun's altitude now, or null without a site.</summary>
    public double? SunAltitude => KnownSite() is { } site ? Night.SunAltitude(DateTime.UtcNow, site) : null;

    /// <summary>
    /// Why the telescope mustn't point at this J2000 position now (too close to the Sun while it's up), or null.
    /// Without a site NOVA can't tell whether the Sun is up, so it assumes it is.
    /// </summary>
    public string? NearSun(EquatorialCoordinates j2000, string what = "The target")
    {
        var limit = SunAvoidance;
        if (limit <= 0) return null;
        var now = DateTime.UtcNow;
        var sunAltitude = SunAltitude;
        if (sunAltitude is { } up && up < SunUpAltitude) return null;
        var sun = Precession.JNowToJ2000(SolarSystem.Sun(now), now);
        var distance = Precession.Separation(j2000, sun);
        return distance < limit
            ? $"{what} is only {distance:0}° from the Sun{(sunAltitude is null ? " (and without a site NOVA can't tell if it's up)" : "")}: " +
              $"Sun protection keeps the telescope {limit:0}° away while the Sun is up. Pointing at the Sun can blind you and destroy the camera."
            : null;
    }

    /// <summary>Throws if <see cref="NearSun"/> says no.</summary>
    public void EnsureAwayFromSun(EquatorialCoordinates j2000, string what = "The target")
    {
        if (NearSun(j2000, what) is { } problem) throw new InvalidOperationException(problem);
    }

    private DateTime _sunWarnedUtc = DateTime.MinValue;

    // Pointing near the Sun anyway (moved there by hand, a slew from elsewhere, or the Sun rising onto it): say so.
    private void WarnIfPointingNearSun()
    {
        if (_equipment.Mount is not { IsConnected: true } mount || mount.State == MountState.Parked) return;
        var j2000 = Precession.JNowToJ2000(mount.Coordinates, DateTime.UtcNow);
        if (NearSun(j2000, $"{mount.Name} is pointing at a spot that") is not { } problem) return;
        if (DateTime.UtcNow - _sunWarnedUtc < TimeSpan.FromMinutes(5)) return;
        _sunWarnedUtc = DateTime.UtcNow;
        _log.Warn($"{problem} Move it away or park it.");
    }

    private DateTime _locationSentUtc = DateTime.MinValue;
    private int _locationResends;

    /// <summary>
    /// Makes sure the mount has the active site's location, sending it again if the driver shows anything else.
    /// ZWO's driver, for one, reads the mount's own (empty) location while it finishes connecting and overwrites
    /// what NOVA sent; GoTos (and polar alignment) then fail until the location is set again. Called every 15 s,
    /// shortly after connecting, and before slews.
    /// </summary>
    public async Task EnsureMountLocationAsync(CancellationToken ct = default)
    {
        if (_equipment.Mount is not { IsConnected: true } mount || ActiveSite is not { } site) return;
        if (mount.Location is { } where && where.IsNear(site.Location, 0.01))
        {
            _locationResends = 0;
            return;
        }
        lock (_sync)
        {
            // Not more than every 5 s (a driver may take a moment to show the new values).
            if (DateTime.UtcNow - _locationSentUtc < TimeSpan.FromSeconds(5)) return;
            _locationSentUtc = DateTime.UtcNow;
        }
        // Sites are named, never located, in the log: it's on screen (and in screenshots).
        var was = mount.Location is null ? "none" : "a different one";
        try
        {
            await mount.SetSiteAsync(site.Location, DateTimeOffset.Now, ct);
            // Said a few times; a driver that never takes it would otherwise fill the log.
            if (++_locationResends <= 3)
                _log.Info($"{mount.Name} had location {was}: sent site '{site.Name}' again");
            else if (_locationResends == 4)
                _log.Warn($"{mount.Name} keeps showing location {was} instead of '{site.Name}': check its driver settings (NOVA keeps sending it)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Couldn't send the site to {mount.Name}: {ex.Message}");
        }
    }

    private readonly Timer _clockWatch;
    private DateTime? _lastClockReading;

    /// <summary>
    /// Why a GoTo to this J2000 target can't go ahead right now (below the horizon or the lower limit, or above the
    /// upper limit), or null. Uses the mount's own location if it reports one, else the active site; with neither,
    /// there's nothing to check against.
    /// </summary>
    public string? OutsideLimits(EquatorialCoordinates j2000, string what = "The target")
    {
        if ((_equipment.Mount?.Location ?? ActiveSite?.Location) is not { } site) return null;
        var altitude = Horizon.Altitude(j2000, DateTime.UtcNow, site);
        var (lower, upper) = (LowerLimit, UpperLimit);
        if (altitude < 0 && lower <= 0)
            return $"{what} is below the horizon right now ({altitude:0}° altitude), so the mount can't go there. Pick something higher up.";
        if (altitude < lower)
            return $"{what} is only {altitude:0}° up, below your lower limit of {lower:0}° (Mount panel, Limits). Pick something higher up, or wait for it to rise.";
        if (altitude > upper)
            return $"{what} is {altitude:0}° up, above your upper limit of {upper:0}° (Mount panel, Limits), where the camera can reach the tripod. Wait for it to come down a little.";
        return null;
    }

    public Site? ActiveSite
    {
        get
        {
            lock (_sync) return _settings.Sites?.FirstOrDefault(s => s.Name == _settings.Active);
        }
    }

    public SiteStatus Status
    {
        get
        {
            Settings settings;
            lock (_sync) settings = _settings;
            var mountLocation = _equipment.Mount is { IsConnected: true } mount ? mount.Location : null;
            var active = ActiveSite;
            return new SiteStatus(
                settings.Sites?.ToList() ?? [],
                settings.Active,
                mountLocation,
                active is not null && mountLocation is not null && !mountLocation.IsNear(active.Location),
                DateTime.UtcNow,
                settings.SunAvoidance ?? InitialSunAvoidance,
                SunAltitude is { } sunUp ? Math.Round(sunUp, 1) : null,
                settings.SunProtectionOff != true,
                settings.SettleSeconds ?? InitialSettleSeconds,
                settings.LowerLimit ?? 0,
                settings.UpperLimit ?? 90);
        }
    }

    /// <summary>Adds or updates a site (renaming it if <paramref name="originalName"/> differs) and makes it active.</summary>
    public async Task SaveAsync(Site site, string? originalName, CancellationToken ct)
    {
        var name = site.Name.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("Give the site a name (up to 60 characters)");
        if (site.Latitude is < -90 or > 90) throw new ArgumentException("Latitude must be between -90 and 90");
        if (site.Longitude is < -180 or > 180) throw new ArgumentException("Longitude must be between -180 and 180");
        if (site.Elevation is < -500 or > 10_000) throw new ArgumentException("Elevation must be between -500 and 10000 m");
        site = site with { Name = name };

        lock (_sync)
        {
            var sites = _settings.Sites?.ToList() ?? [];
            var replacing = originalName ?? name;
            if (sites.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && !s.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There's already a site called '{name}'");
            var index = sites.FindIndex(s => s.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) sites[index] = site;
            else sites.Add(site);
            _settings = _settings with { Active = name, Sites = sites };
        }
        Save();
        _log.Info($"Site '{name}' saved");
        await ApplyNowAsync(ct);
    }

    public async Task SelectAsync(string name, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_settings.Sites?.Any(s => s.Name == name) != true) throw new ArgumentException($"No site called '{name}'");
            _settings = _settings with { Active = name };
        }
        Save();
        _log.Info($"Using site '{name}'");
        await ApplyNowAsync(ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        lock (_sync)
        {
            var sites = _settings.Sites?.ToList() ?? [];
            if (sites.RemoveAll(s => s.Name == name) == 0) throw new ArgumentException($"No site called '{name}'");
            var active = _settings.Active == name ? sites.FirstOrDefault()?.Name : _settings.Active;
            _settings = _settings with { Active = active, Sites = sites };
        }
        Save();
        _log.Info($"Deleted site '{name}'");
        return ApplyNowAsync(ct);
    }

    public static string Describe(GeoLocation l) =>
        $"{Math.Abs(l.Latitude):0.0000}°{(l.Latitude >= 0 ? "N" : "S")} {Math.Abs(l.Longitude):0.0000}°{(l.Longitude >= 0 ? "E" : "W")}, {l.Elevation:0} m";

    private Task ApplyNowAsync(CancellationToken ct)
    {
        lock (_sync) _appliedTo = null;
        return _equipment.Mount is { IsConnected: true } mount ? ApplyAsync(mount, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Every so often: if the mount reports a clock reading that's more than a couple of minutes off (it
    /// restarted, or never got the time), set it. Only new readings count — some drivers report the time once
    /// and leave the value as it was, which would otherwise look ever more wrong as time passes.
    /// </summary>
    public async Task CheckClockAsync()
    {
        if (_equipment.Mount is not { IsConnected: true } mount || mount.ClockUtc is not { } clock) return;
        lock (_sync)
        {
            if (clock == _lastClockReading) return;
            _lastClockReading = clock;
        }
        var off = clock - DateTime.UtcNow;
        if (Math.Abs(off.TotalMinutes) < 2) return;
        try
        {
            // A clock that's far out means the mount was powered off: it has forgotten its location too (even if
            // the driver still shows it), so send both.
            if (SiteToSend(mount) is { } where)
            {
                await mount.SetSiteAsync(where, DateTimeOffset.Now, CancellationToken.None);
                lock (_sync) _locationSentUtc = DateTime.UtcNow;
                _log.Warn($"{mount.Name}'s clock said {clock:yyyy-MM-dd HH:mm} UTC ({Describe(off)} off), so it was powered off: time and location sent again");
            }
            else
            {
                await mount.SetClockAsync(DateTimeOffset.Now, CancellationToken.None);
                _log.Warn($"{mount.Name}'s clock said {clock:yyyy-MM-dd HH:mm} UTC ({Describe(off)} off): set it to the right time");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{mount.Name}'s clock is {Describe(off)} off and couldn't be set: {ex.Message}");
        }
    }

    private static string Describe(TimeSpan off)
    {
        var span = off.Duration();
        return span.TotalDays >= 365 ? $"{span.TotalDays / 365.25:0} years"
            : span.TotalDays >= 1 ? $"{span.TotalDays:0} days"
            : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours"
            : $"{span.TotalMinutes:0} minutes";
    }

    private void ApplyOnNewConnection()
    {
        var mount = _equipment.Mount;
        lock (_sync)
        {
            if (mount is not { IsConnected: true })
            {
                _appliedTo = null; // send again on the next connect
                return;
            }
            if (_appliedTo == mount.Name) return;
            _appliedTo = mount.Name;
        }
        _ = ApplyAsync(mount, CancellationToken.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // the driver lists its settings just after connecting
            if (mount.IsConnected) await GiveMountLimitsAsync(mount, CancellationToken.None);
        });
    }

    private async Task ApplyAsync(IMount mount, CancellationToken ct)
    {
        if (ActiveSite is not { } site)
        {
            // No site chosen in NOVA. The time still matters, and if the driver remembers a location, send that
            // back too: mounts like ZWO's forget both at power-on and won't move until they have them, even though
            // the driver still shows the old location.
            try
            {
                if (mount.Location is { } remembered && (remembered.Latitude != 0 || remembered.Longitude != 0))
                {
                    await mount.SetSiteAsync(remembered, DateTimeOffset.Now, ct);
                    _log.Info($"{mount.Name}: time and its remembered location sent. Set your site in NOVA's Mount panel to be sure it's right.");
                }
                else
                {
                    await mount.SetClockAsync(DateTimeOffset.Now, ct);
                    _log.Warn($"{mount.Name}: time sent, but no location: set your site in NOVA's Mount panel (the mount won't GoTo without it)");
                }
                lock (_sync) _appliedTo = mount.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_sync) _appliedTo = null;
                _log.Warn($"Couldn't set {mount.Name}'s clock: {ex.Message}");
            }
            return;
        }
        try
        {
            await mount.SetSiteAsync(site.Location, DateTimeOffset.Now, ct);
            lock (_sync)
            {
                _appliedTo = mount.Name;
                _locationSentUtc = DateTime.UtcNow;
            }
            _log.Info($"{mount.Name}: site '{site.Name}' and time sent");
            // The driver may still be starting up and overwrite it: check again shortly.
            foreach (var wait in new[] { 6, 15 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                await EnsureMountLocationAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _appliedTo = null;
            _log.Warn($"Couldn't send the site to {mount.Name}: {ex.Message}");
        }
    }

    private Settings Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new Settings();
    }

    private void Save()
    {
        try
        {
            Settings snapshot;
            lock (_sync) snapshot = _settings;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }
}
