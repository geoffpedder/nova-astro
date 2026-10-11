using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>
/// Moving the mount by hand from the browser, with a dead-man's switch: a held button keeps saying "still
/// moving" (<see cref="Move"/> several times a second), and any direction not renewed within
/// <see cref="Timeout"/> is stopped here. So a dropped Wi-Fi link or a closed tab can't leave the mount
/// driving into its limits.
/// <para>
/// The speed chosen for each mount is remembered (~/.config/nova/manual-speeds.json) and applied before moving,
/// and when the mount connects. Until one is chosen it's the fastest up to 720×: ZWO's driver also lists the AM5's
/// 1440×, which an AM3 silently ignores, and starts at a slow 60×.
/// </para>
/// </summary>
public sealed class MountMotionService : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.2);

    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    private readonly Dictionary<MotionDirection, DateTime> _until = new();
    private readonly Timer _watch;
    private readonly string _file;
    private readonly Dictionary<string, string> _speeds; // mount name → slew rate name
    private readonly SiteService? _sites;
    private double? _lastSunDistance;

    public MountMotionService(EquipmentService equipment, ActivityLog log, IOptions<NovaOptions> options, SiteService? sites = null)
    {
        _equipment = equipment;
        _sites = sites;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "manual-speeds.json");
        _speeds = Load();
        equipment.DevicesChanged += (_, _) => _ = ApplySpeedOnConnectAsync();
        _watch = new Timer(_ => _ = StopExpiredAsync(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    /// <summary>The directions being moved in right now.</summary>
    public IReadOnlyList<MotionDirection> Active
    {
        get { lock (_sync) return _until.Keys.ToList(); }
    }

    /// <summary>Starts moving (or keeps moving, if already) in a direction for another <see cref="Timeout"/>.</summary>
    public async Task MoveAsync(MotionDirection direction, CancellationToken ct)
    {
        var mount = _equipment.Mount is { IsConnected: true } m ? m : throw new InvalidOperationException("No mount connected");
        if (mount.State == MountState.Parked) throw new InvalidOperationException("Unpark the mount first");
        bool starting;
        lock (_sync)
        {
            starting = !_until.ContainsKey(direction);
            _until.Remove(Opposite(direction)); // one direction per axis
            _until[direction] = DateTime.UtcNow + Timeout;
        }
        if (starting)
        {
            await UseChosenSpeedAsync(mount, ct);
            await mount.StartMotionAsync(direction, ct);
        }
    }

    /// <summary>The speed this mount should move at by hand: the one chosen for it, else one below the fastest.</summary>
    public string? SpeedFor(IMount mount)
    {
        var rates = mount.SlewRates;
        if (rates.Count == 0) return null;
        lock (_sync)
        {
            if (_speeds.TryGetValue(mount.Name, out var chosen))
            {
                if (rates.Any(r => r.Name == chosen)) return chosen;
                // The chosen speed isn't offered now (e.g. a ZWO mount in Heavy Duty Mode tops out at 720x): the fastest that is.
                return rates[^1].Name;
            }
        }
        return DefaultSpeed(rates);
    }

    /// <summary>The fastest speed that's a multiple of sidereal up to 720×; without such labels, one below the fastest.</summary>
    public static string DefaultSpeed(IReadOnlyList<SlewRate> rates)
    {
        var upTo720 = rates.Where(r => Multiple(r.Label) is { } x && x <= MaxDefaultRate).ToList();
        if (upTo720.Count > 0) return upTo720.MaxBy(r => Multiple(r.Label))!.Name;
        return rates.Count > 1 ? rates[^2].Name : rates[0].Name;
    }

    private const double MaxDefaultRate = 720;

    /// <summary>"720x" → 720; null when the label isn't a multiple of sidereal.</summary>
    private static double? Multiple(string label)
    {
        var text = label.Trim().TrimEnd('x', 'X', '×');
        return text.Length < label.Trim().Length && double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    private string? _speedSetFor; // the mount whose connection already got its speed

    /// <summary>Gives a mount that just connected its manual speed, so the Mount panel shows it from the start.</summary>
    private async Task ApplySpeedOnConnectAsync()
    {
        try
        {
            if (_equipment.Mount is not { IsConnected: true } mount)
            {
                lock (_sync) _speedSetFor = null;
                return;
            }
            lock (_sync)
            {
                if (_speedSetFor == mount.Name) return;
                _speedSetFor = mount.Name;
            }
            // Drivers list their speeds just after connecting.
            for (var i = 0; i < 50 && mount.SlewRates.Count == 0 && mount.IsConnected; i++) await Task.Delay(200);
            await UseChosenSpeedAsync(mount, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _speedSetFor = null;
            _log.Warn($"Couldn't set the mount's speed: {ex.Message}");
        }
    }

    private async Task UseChosenSpeedAsync(IMount mount, CancellationToken ct)
    {
        if (SpeedFor(mount) is { } speed && mount.CurrentSlewRate != speed)
            await mount.SetSlewRateAsync(speed, ct);
    }

    /// <summary>Stops one direction's axis (the button was released), or everything when null.</summary>
    public async Task StopAsync(MotionDirection? direction, CancellationToken ct)
    {
        lock (_sync)
        {
            if (direction is { } d)
            {
                _until.Remove(d);
                _until.Remove(Opposite(d));
            }
            else
            {
                _until.Clear();
            }
        }
        if (_equipment.Mount is { IsConnected: true } mount) await mount.StopMotionAsync(direction, ct);
    }

    public async Task SetRateAsync(string name, CancellationToken ct)
    {
        var mount = _equipment.Mount is { IsConnected: true } m ? m : throw new InvalidOperationException("No mount connected");
        await mount.SetSlewRateAsync(name, ct);
        lock (_sync) _speeds[mount.Name] = name;
        Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_speeds);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }

    /// <summary>
    /// Sun protection for moving by hand: stops the mount when it's inside the Sun's zone and getting closer
    /// (moving out of it is allowed, so a scope left pointing near the Sun can be moved away).
    /// </summary>
    private async Task StopIfHeadingIntoSunAsync()
    {
        if (_sites is null) return;
        bool moving;
        lock (_sync) moving = _until.Count > 0;
        if (!moving || _equipment.Mount is not { IsConnected: true } mount)
        {
            _lastSunDistance = null;
            return;
        }
        var limit = _sites.SunAvoidance;
        if (limit <= 0 || _sites.SunAltitude is { } up && up < SiteService.SunUpAltitude) return;
        var now = DateTime.UtcNow;
        var sun = Precession.JNowToJ2000(SolarSystem.Sun(now), now);
        var distance = Precession.Separation(Precession.JNowToJ2000(mount.Coordinates, now), sun);
        var closer = _lastSunDistance is { } last && distance < last - 0.01;
        _lastSunDistance = distance;
        if (distance >= limit || !closer) return;
        lock (_sync) _until.Clear();
        _lastSunDistance = null;
        await mount.StopMotionAsync(null, CancellationToken.None);
        _log.Warn($"Stopped {mount.Name}: it was heading towards the Sun ({distance:0}° away; Sun protection keeps it {limit:0}° away)");
    }

    private async Task StopExpiredAsync()
    {
        try
        {
            await StopIfHeadingIntoSunAsync();
        }
        catch (Exception ex)
        {
            _log.Warn($"Sun protection check failed: {ex.Message}");
        }
        List<MotionDirection> expired;
        lock (_sync)
        {
            expired = _until.Where(p => p.Value < DateTime.UtcNow).Select(p => p.Key).ToList();
            foreach (var d in expired) _until.Remove(d);
        }
        foreach (var direction in expired)
        {
            try
            {
                if (_equipment.Mount is { IsConnected: true } mount) await mount.StopMotionAsync(direction, CancellationToken.None);
                _log.Warn($"Stopped moving {direction.ToString().ToLowerInvariant()}: the browser stopped holding the button (connection lost?)");
            }
            catch (Exception ex)
            {
                _log.Error($"Couldn't stop moving {direction.ToString().ToLowerInvariant()}", ex);
            }
        }
    }

    private static MotionDirection Opposite(MotionDirection d) => d switch
    {
        MotionDirection.North => MotionDirection.South,
        MotionDirection.South => MotionDirection.North,
        MotionDirection.East => MotionDirection.West,
        _ => MotionDirection.East,
    };

    public void Dispose() => _watch.Dispose();
}
