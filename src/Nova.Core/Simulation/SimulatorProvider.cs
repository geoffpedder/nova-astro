using System.Globalization;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Imaging;

namespace Nova.Core.Simulation;

/// <summary>
/// In-process camera and mount for developing on any OS without INDI or hardware.
/// The camera renders a deterministic star field around wherever the mount points,
/// so slewing visibly moves the field (useful later for testing plate solving).
/// </summary>
public sealed class SimulatorProvider : IEquipmentProvider
{
    private readonly SimulatedMount _mount = new();
    private readonly SimulatedFocuser _focuser = new();
    private readonly SimulatedFilterWheel _wheel = new();
    private readonly SimulatedRotator _rotator = new();
    private readonly SimulatedFlatPanel _panel = new();
    private readonly SimulatedCamera[] _cameras;

    public SimulatorProvider()
    {
        // Two cameras so device selection can be exercised without hardware: a colour main camera and a mono guider.
        _cameras =
        [
            // 3.76 µm at 388 mm ≈ 2"/px; 3.75 µm at 193 mm ≈ 4"/px (a typical 50 mm guide scope).
            // The main camera sits on the rotator: at its starting 90° the frame's top points north, and turning the
            // rotator one way turns the frame the other way on the sky (so NOVA has to learn which way it goes).
            new SimulatedCamera("Simulated Camera", 1280, 960, 3.76, 388, color: true, () => _mount.TrueCoordinates, () => _focuser.DefocusWith(_wheel.FocusShift), LinkedHeaders,
                () => ((90 - _rotator.Angle + _mount.TiltPositionAngleAt(DateTime.UtcNow)) % 360 + 360) % 360, () => _panel.IsConnected ? _panel.Glow : null),
            new SimulatedCamera("Simulated Guide Camera", 640, 480, 3.75, 193, color: false, () => _mount.TrueCoordinates, linkedHeaders: LinkedHeaders),
        ];
    }

    /// <summary>The FITS keywords an INDI camera writes from the devices it's linked to (ACTIVE_DEVICES).</summary>
    private IEnumerable<FitsCard> LinkedHeaders(LinkedDevices links)
    {
        if (links.Mount == _mount.Name)
        {
            var c = _mount.Coordinates;
            yield return new FitsCard("TELESCOP", _mount.Name);
            yield return new FitsCard("OBJCTRA", c.RaText.Replace('h', ' ').Replace('m', ' ').TrimEnd('s'));
            yield return new FitsCard("OBJCTDEC", c.DecText.Replace('°', ' ').Replace('\'', ' ').TrimEnd('"'));
        }
        if (links.FilterWheel == _wheel.Name && _wheel.Position > 0)
            yield return new FitsCard("FILTER", _wheel.Names[_wheel.Position - 1], "Filter");
        if (links.Focuser == _focuser.Name)
        {
            yield return new FitsCard("FOCUSPOS", _focuser.Position, "Focus position in steps");
            if (_focuser.Temperature is { } t) yield return new FitsCard("FOCUSTEM", t, "Focuser temperature in degrees C");
        }
        if (links.Rotator == _rotator.Name)
            yield return new FitsCard("ROTATANG", Math.Round(_rotator.Angle, 2), "Rotator angle in degrees");
    }

    public string Name => "Simulator";
    public bool IsConnected { get; private set; }

    public IReadOnlyList<DeviceInfo> Devices =>
    [
        .. _cameras.Select(c => new DeviceInfo(c.Name, [DeviceKind.Camera], c.IsConnected)),
        new(_mount.Name, [DeviceKind.Mount], _mount.IsConnected),
        new(_focuser.Name, [DeviceKind.Focuser], _focuser.IsConnected),
        new(_wheel.Name, [DeviceKind.FilterWheel], _wheel.IsConnected),
        new(_rotator.Name, [DeviceKind.Rotator], _rotator.IsConnected),
        new(_panel.Name, [DeviceKind.FlatPanel], _panel.IsConnected),
    ];

    public ICamera? GetCamera(string name) => _cameras.FirstOrDefault(c => c.Name == name);
    public IMount? GetMount(string name) => _mount.Name == name ? _mount : null;
    public IFocuser? GetFocuser(string name) => _focuser.Name == name ? _focuser : null;
    public IFilterWheel? GetFilterWheel(string name) => _wheel.Name == name ? _wheel : null;
    public IRotator? GetRotator(string name) => _rotator.Name == name ? _rotator : null;
    public IFlatPanel? GetFlatPanel(string name) => _panel.Name == name ? _panel : null;

    // The built-in simulator has no raw driver properties; run the INDI simulators to try that panel.
    public IDeviceSettings? Settings => null;
    public IDriverControl? Drivers => null;

    public event EventHandler? Changed;
    public event EventHandler<string>? Message;

    public Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        Message?.Invoke(this, "Simulator connected");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        foreach (var camera in _cameras) await camera.DisconnectAsync(ct);
        await _mount.DisconnectAsync(ct);
        await _focuser.DisconnectAsync(ct);
        await _panel.DisconnectAsync(ct);
        await _wheel.DisconnectAsync(ct);
        await _rotator.DisconnectAsync(ct);
        IsConnected = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A simulated mount's polar alignment: how far its RA axis is off the pole, and "knobs" to fix it.
/// Real mounts don't have this — it's for trying polar alignment without a sky.
/// </summary>
public interface IPolarAlignmentKnobs
{
    /// <summary>Positive: the axis points above the pole.</summary>
    double AltitudeErrorArcmin { get; }
    /// <summary>Positive: the axis points to the right of the pole as you face it; on-sky arcminutes.</summary>
    double AzimuthErrorArcmin { get; }
    /// <summary>Turns the knobs: + raises the axis / moves it right (as seen facing the pole).</summary>
    void Turn(double altitudeArcmin, double azimuthArcmin);
}

internal sealed class SimulatedMount : IMount, IPolarAlignmentKnobs
{
    // Where the simulator pretends to be until a site is set.
    private static readonly GeoLocation DefaultSite = new(51.48, 0, 0);
    private const double SlewRateDegreesPerSecond = 4;
    private CancellationTokenSource? _slewCts;

    public string Name => "Simulated Mount";

    // Like a real mount before its first sync, the simulated one points a little off where it reports:
    // the camera sees the sky at TrueCoordinates. Plate solve + sync removes the error.
    private EquatorialCoordinates _pointingError = new(0.01, -0.2); // 0.01 h (9′ of RA) and −12′ in Dec

    // And like most mounts it isn't perfectly polar aligned: its RA axis is tilted off the pole, so the
    // view turns about the wrong point as it tracks. Polar alignment measures this; Turn() fixes it.
    private double _polarAltitude = 12;
    private double _polarAzimuth = -9;
    public double AltitudeErrorArcmin => Math.Round(_polarAltitude, 3);
    public double AzimuthErrorArcmin => Math.Round(_polarAzimuth, 3);
    public void Turn(double altitudeArcmin, double azimuthArcmin)
    {
        _polarAltitude += altitudeArcmin;
        _polarAzimuth += azimuthArcmin;
    }

    /// <summary>Where the mount's own coordinates (after its pointing error) aim, before polar misalignment.</summary>
    private EquatorialCoordinates MountPointing => new(
        (Coordinates.RaHours + _pointingError.RaHours + 24) % 24, Math.Clamp(Coordinates.DecDegrees + _pointingError.DecDegrees, -90, 90));

    public EquatorialCoordinates TrueCoordinates => TrueCoordinatesAt(DateTime.UtcNow);

    /// <summary>
    /// Where the camera really points (JNow): the mount's aim, carried by the tilt of its axis. Tracking holds
    /// the mount's RA, so the view turns about that tilted axis over time — just as with a real mount.
    /// </summary>
    public EquatorialCoordinates TrueCoordinatesAt(DateTime utc)
    {
        var aim = MountPointing;
        if (_polarAltitude == 0 && _polarAzimuth == 0) return aim;
        var site = Location ?? DefaultSite;
        var pole = Horizon.AltAz(Horizon.VisiblePole(site));
        var turn = _polarAzimuth / 60 / Math.Cos(pole.Altitude * Math.PI / 180) * Math.PI / 180;
        var tilted = Horizon.KnobRotation(Horizon.ToVector(aim, utc, site), pole.Azimuth, _polarAltitude / 60 * Math.PI / 180, turn);
        return Horizon.FromVector(tilted, utc, site);
    }

    /// <summary>
    /// How much the tilted axis turns the camera's frame (degrees of position angle): the knobs tilt the whole mount,
    /// camera included, so a frame whose top would point north turns a little with it. Lets the simulated camera
    /// turn about the real axis, as a camera on a real mount does (which pole-centred polar alignment relies on).
    /// </summary>
    public double TiltPositionAngleAt(DateTime utc)
    {
        if (_polarAltitude == 0 && _polarAzimuth == 0) return 0;
        var aim = MountPointing;
        var north = aim.DecDegrees < 89.8 ? aim with { DecDegrees = aim.DecDegrees + 0.1 } : aim with { DecDegrees = aim.DecDegrees - 0.1 };
        var site = Location ?? DefaultSite;
        var pole = Horizon.AltAz(Horizon.VisiblePole(site));
        var turn = _polarAzimuth / 60 / Math.Cos(pole.Altitude * Math.PI / 180) * Math.PI / 180;
        var tilt = _polarAltitude / 60 * Math.PI / 180;
        var c = Horizon.FromVector(Horizon.KnobRotation(Horizon.ToVector(aim, utc, site), pole.Azimuth, tilt, turn), utc, site);
        var u = Horizon.FromVector(Horizon.KnobRotation(Horizon.ToVector(north, utc, site), pole.Azimuth, tilt, turn), utc, site);
        var pa = PositionAngle(c, u) - (aim.DecDegrees < 89.8 ? 0 : 180);
        return ((pa + 540) % 360) - 180;
    }

    /// <summary>Position angle (degrees, north through east) of <paramref name="to"/> as seen from <paramref name="from"/>.</summary>
    private static double PositionAngle(EquatorialCoordinates from, EquatorialCoordinates to)
    {
        var d1 = from.DecDegrees * Math.PI / 180;
        var d2 = to.DecDegrees * Math.PI / 180;
        var da = (to.RaHours - from.RaHours) * 15 * Math.PI / 180;
        return Math.Atan2(Math.Sin(da) * Math.Cos(d2), Math.Cos(d1) * Math.Sin(d2) - Math.Sin(d1) * Math.Cos(d2) * Math.Cos(da)) * 180 / Math.PI;
    }

    public Task SyncAsync(EquatorialCoordinates actual, CancellationToken ct)
    {
        EnsureConnected();
        // Keep pointing where it physically is, but report the synced position from now on.
        var aim = MountPointing;
        Coordinates = actual;
        _pointingError = new EquatorialCoordinates(WrapHours(aim.RaHours - actual.RaHours), aim.DecDegrees - actual.DecDegrees);
        return Task.CompletedTask;
    }
    public GeoLocation? Location { get; private set; }

    // Like a German equatorial mount: a GoTo puts the scope on the normal side for where the target is
    // (West while it's east of the meridian), and tracking keeps that side, past the meridian too, until the
    // next GoTo flips it. The two sides point slightly differently, so a flip needs re-centring.
    public PierSide? SideOfPier { get; private set; } = PierSide.West;

    private PierSide NormalSideFor(EquatorialCoordinates target)
    {
        var site = Location ?? DefaultSite;
        return Horizon.HourAngle(target, DateTime.UtcNow, site.Longitude) < 0 ? PierSide.West : PierSide.East;
    }

    public Task SetSiteAsync(GeoLocation location, DateTimeOffset now, CancellationToken ct)
    {
        Location = location;
        return SetClockAsync(now, ct);
    }

    // Like a ZWO mount: no clock battery, so it starts at 2000-01-01 00:00 UTC until told the time.
    private TimeSpan _clockOffset = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) - DateTime.UtcNow;
    public DateTime? ClockUtc => DateTime.UtcNow + _clockOffset;

    public Task SetClockAsync(DateTimeOffset now, CancellationToken ct)
    {
        _clockOffset = now.UtcDateTime - DateTime.UtcNow;
        return Task.CompletedTask;
    }
    public bool IsConnected { get; private set; }
    public MountState State { get; private set; } = MountState.Parked;
    // Start near M42 so the first frame has something to look at.
    public EquatorialCoordinates Coordinates { get; private set; } = new(5.588, -5.39);

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { _slewCts?.Cancel(); IsConnected = false; return Task.CompletedTask; }

    public async Task SlewAsync(EquatorialCoordinates target, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        var side = NormalSideFor(target);
        await MoveToAsync(target, MountState.Slewing, MountState.Tracking, ct);
        if (side != SideOfPier)
        {
            SideOfPier = side;
            _pointingError = new EquatorialCoordinates(_pointingError.RaHours + 0.004, _pointingError.DecDegrees - 0.06);
        }
    }

    public Task AbortAsync(CancellationToken ct)
    {
        _slewCts?.Cancel();
        return Task.CompletedTask;
    }

    // Manual motion, like a ZWO hand controller's speeds (× sidereal; 1440× ≈ 6°/s).
    private static readonly (SlewRate Rate, double DegreesPerSecond)[] Rates =
    [
        (new SlewRate("1", "1x"), 15.0 / 3600), (new SlewRate("2", "8x"), 8 * 15.0 / 3600),
        (new SlewRate("3", "64x"), 64 * 15.0 / 3600), (new SlewRate("4", "720x"), 3.0), (new SlewRate("5", "1440x"), 6.0),
    ];
    private string _rate = "4";
    private readonly object _motionSync = new();
    private double _raRate, _decRate; // degrees per second while moving by hand
    private CancellationTokenSource? _motion;

    public IReadOnlyList<SlewRate> SlewRates => Rates.Select(r => r.Rate).ToList();
    public string? CurrentSlewRate => _rate;

    public Task SetSlewRateAsync(string name, CancellationToken ct)
    {
        if (Rates.All(r => r.Rate.Name != name)) throw new ArgumentException($"No slew rate '{name}'");
        _rate = name;
        return Task.CompletedTask;
    }

    public Task StartMotionAsync(MotionDirection direction, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        var speed = Rates.First(r => r.Rate.Name == _rate).DegreesPerSecond;
        lock (_motionSync)
        {
            switch (direction)
            {
                case MotionDirection.North: _decRate = speed; break;
                case MotionDirection.South: _decRate = -speed; break;
                case MotionDirection.East: _raRate = speed; break;   // east = increasing RA
                default: _raRate = -speed; break;
            }
            if (_motion is null)
            {
                var cts = _motion = new CancellationTokenSource();
                _ = Task.Run(() => MoveByHandAsync(cts.Token));
            }
        }
        return Task.CompletedTask;
    }

    public Task StopMotionAsync(MotionDirection? direction, CancellationToken ct)
    {
        lock (_motionSync)
        {
            if (direction is null or MotionDirection.North or MotionDirection.South) _decRate = 0;
            if (direction is null or MotionDirection.East or MotionDirection.West) _raRate = 0;
            if (_raRate == 0 && _decRate == 0)
            {
                _motion?.Cancel();
                _motion = null;
            }
        }
        return Task.CompletedTask;
    }

    private async Task MoveByHandAsync(CancellationToken ct)
    {
        State = MountState.Slewing;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                double ra, dec;
                lock (_motionSync) (ra, dec) = (_raRate, _decRate);
                Coordinates = new EquatorialCoordinates(
                    ((Coordinates.RaHours + ra * 0.1 / 15) % 24 + 24) % 24,
                    Math.Clamp(Coordinates.DecDegrees + dec * 0.1, -90, 90));
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            if (State == MountState.Slewing) State = MountState.Tracking;
        }
    }

    public bool CanMoveRaAxis => true;

    /// <summary>Drives RA at the slew rate; unlike a GoTo, the pier side stays as it is.</summary>
    public async Task MoveRaAxisAsync(double degrees, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        var target = Coordinates with { RaHours = ((Coordinates.RaHours - degrees / 15) % 24 + 24) % 24 };
        State = MountState.Slewing;
        try
        {
            var remaining = Math.Abs(degrees);
            while (remaining > 0)
            {
                await Task.Delay(100, ct);
                var step = Math.Min(remaining, SlewRateDegreesPerSecond * 0.1);
                remaining -= step;
                Coordinates = Coordinates with { RaHours = (Coordinates.RaHours - Math.Sign(degrees) * step / 15 + 24) % 24 };
            }
            Coordinates = target;
            State = MountState.Tracking;
        }
        catch (OperationCanceledException)
        {
            State = MountState.Tracking;
            throw;
        }
    }

    public async Task ParkAsync(CancellationToken ct)
    {
        EnsureConnected();
        // Park position: pointing at the celestial pole.
        await MoveToAsync(Coordinates with { DecDegrees = 90 }, MountState.Parking, MountState.Parked, ct);
    }

    public Task UnparkAsync(CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) State = MountState.Tracking;
        return Task.CompletedTask;
    }

    private async Task MoveToAsync(EquatorialCoordinates target, MountState during, MountState after, CancellationToken ct)
    {
        _slewCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _slewCts = cts;
        State = during;
        try
        {
            while (true)
            {
                await Task.Delay(100, cts.Token);
                var raDelta = WrapHours(target.RaHours - Coordinates.RaHours) * 15;
                var decDelta = target.DecDegrees - Coordinates.DecDegrees;
                var distance = Math.Sqrt(raDelta * raDelta + decDelta * decDelta);
                // Long slews speed up (a tenth of the remaining distance per tick) so crossing the sky takes
                // seconds, not a minute; short ones run at the steady rate.
                var step = Math.Max(SlewRateDegreesPerSecond * 0.1, distance * 0.1);
                if (distance <= step)
                {
                    Coordinates = target;
                    break;
                }
                var f = step / distance;
                var ra = (Coordinates.RaHours + raDelta * f / 15 + 24) % 24;
                Coordinates = new EquatorialCoordinates(ra, Coordinates.DecDegrees + decDelta * f);
            }
            State = after;
        }
        catch (OperationCanceledException)
        {
            State = MountState.Idle;
            throw;
        }
        finally
        {
            if (_slewCts == cts) _slewCts = null;
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Mount is not connected");
    }

    private static double WrapHours(double h) => h > 12 ? h - 24 : h < -12 ? h + 24 : h;
}

