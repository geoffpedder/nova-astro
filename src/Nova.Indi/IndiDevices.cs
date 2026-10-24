using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Imaging;

namespace Nova.Indi;

/// <summary>
/// Common behaviour for INDI devices: every driver has the standard CONNECTION switch.
/// Property and element names below are INDI's standard ones:
/// https://www.indilib.org/develop/developer-manual/101-standard-properties.html
/// </summary>
public abstract class IndiDevice(IndiClient client, string name) : IDevice
{
    protected static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    protected IndiClient Client { get; } = client;
    public string Name { get; } = name;

    public bool IsConnected => Client.GetProperty(Name, "CONNECTION")?["CONNECT"]?.IsOn == true;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (!IsConnected && !await TryConnectAsync(checkCurrent: true, ct) && !await TryOtherPortsAsync(ct))
            throw new InvalidOperationException($"{Name} failed to connect");
        await OnConnectedAsync(ct);
    }

    private async Task<bool> TryConnectAsync(bool checkCurrent, CancellationToken ct)
    {
        await Client.SetSwitchesAsync(Name, "CONNECTION", ct, ("CONNECT", true));
        var result = await Client.WaitForAsync(Name, "CONNECTION",
            p => p.State == IndiState.Alert || (p.State == IndiState.Ok && p["CONNECT"]?.IsOn == true),
            ConnectTimeout, ct, checkCurrent);
        return result.State != IndiState.Alert;
    }

    /// <summary>
    /// A serial device that isn't on the port its driver expects (plugged in after the driver started, or on another
    /// /dev/tty…) fails to connect until someone presses the driver's Scan Ports and picks the right port. This does
    /// the same: rescans, then tries each port the driver lists. The driver keeps the one that works.
    /// </summary>
    private async Task<bool> TryOtherPortsAsync(CancellationToken ct)
    {
        if (Client.GetProperty(Name, "DEVICE_PORT_SCAN") is not { } scan || Client.GetProperty(Name, "DEVICE_PORT") is null)
            return false;
        var first = scan.Elements.Keys.FirstOrDefault() ?? "SCAN_PORT";
        await Client.SetSwitchesAsync(Name, "DEVICE_PORT_SCAN", ct, (first, true));
        try
        {
            // The driver answers with a fresh SYSTEM_PORTS list.
            await Client.WaitForAsync(Name, "SYSTEM_PORTS", _ => true, TimeSpan.FromSeconds(5), ct, checkCurrent: false);
        }
        catch (TimeoutException)
        {
            // An unchanged list may not be sent again: use what's there.
        }

        var tried = Client.GetProperty(Name, "DEVICE_PORT")?["PORT"]?.Text;
        var ports = Client.GetProperty(Name, "SYSTEM_PORTS")?.Elements.Keys.Where(p => p != tried).ToList() ?? [];
        foreach (var port in ports)
        {
            await Client.SetTextsAsync(Name, "DEVICE_PORT", ct, ("PORT", port));
            if (await TryConnectAsync(checkCurrent: false, ct))
            {
                PortFound?.Invoke(this, port);
                return true;
            }
        }
        return false;
    }

    /// <summary>Raised when connecting only worked on another serial port than the driver's (which one).</summary>
    public event EventHandler<string>? PortFound;

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (!IsConnected) return;
        await Client.SetSwitchesAsync(Name, "CONNECTION", ct, ("DISCONNECT", true));
        await Client.WaitForAsync(Name, "CONNECTION", p => p["CONNECT"]?.IsOn != true, ConnectTimeout, ct);
    }

    /// <summary>Runs after CONNECTION is on; drivers define most properties only at this point.</summary>
    protected virtual Task OnConnectedAsync(CancellationToken ct) => Task.CompletedTask;

    protected bool HasProperty(string property) => Client.GetProperty(Name, property) is not null;

    protected void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException($"{Name} is not connected");
    }

    // ---- Pulse guiding (INDI's guider interface: mounts, and cameras with an ST4 port) ----
    private const string GuideNs = "TELESCOPE_TIMED_GUIDE_NS";
    private const string GuideWe = "TELESCOPE_TIMED_GUIDE_WE";

    /// <summary>Whether the driver has the timed-guide commands.</summary>
    public bool CanPulseGuide => HasProperty(GuideNs) && HasProperty(GuideWe);

    /// <summary>GUIDE_RATE (fraction of sidereal), when the driver has it: RA's, else Dec's.</summary>
    public double? GuideRateSidereal =>
        Client.GetProperty(Name, "GUIDE_RATE") is { } p
            ? (p["GUIDE_RATE_WE"] ?? p["GUIDE_RATE_NS"])?.Number is { } rate && rate > 0 ? rate : null
            : null;

    /// <summary>One guide pulse: TIMED_GUIDE_N/S/E/W for that many milliseconds, waiting until the driver has done it.</summary>
    public Task PulseAsync(GuideDirection direction, TimeSpan duration, CancellationToken ct)
    {
        EnsureConnected();
        if (!CanPulseGuide) throw new NotSupportedException($"{Name} can't pulse guide");
        var ms = Math.Round(Math.Max(0, duration.TotalMilliseconds));
        var (property, on, off) = direction switch
        {
            GuideDirection.North => (GuideNs, "TIMED_GUIDE_N", "TIMED_GUIDE_S"),
            GuideDirection.South => (GuideNs, "TIMED_GUIDE_S", "TIMED_GUIDE_N"),
            GuideDirection.East => (GuideWe, "TIMED_GUIDE_E", "TIMED_GUIDE_W"),
            _ => (GuideWe, "TIMED_GUIDE_W", "TIMED_GUIDE_E"),
        };
        return SendAndWaitAsync(property,
            () => Client.SetNumbersAsync(Name, property, ct, (on, ms), (off, 0)),
            () => Client.SetNumbersAsync(Name, property, CancellationToken.None, (on, 0), (off, 0)),
            duration + TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Sends a command and waits for the property to go Busy and then settle. Drivers stream periodic
    /// "Ok" updates, so waiting only for "Ok" could match an update sent before the command arrived.
    /// If <paramref name="ct"/> is cancelled, <paramref name="abort"/> stops the motion.
    /// </summary>
    protected async Task SendAndWaitAsync(string property, Func<Task> send, Func<Task> abort, TimeSpan timeout, CancellationToken ct)
    {
        var busy = Client.WaitForAsync(Name, property, p => p.State is IndiState.Busy or IndiState.Alert,
            TimeSpan.FromSeconds(5), ct, checkCurrent: false);
        await send();
        try
        {
            await busy;
        }
        catch (TimeoutException)
        {
            // Never went busy: the driver finished instantly (already there, already parked).
        }

        try
        {
            var done = await Client.WaitForAsync(Name, property, p => p.State != IndiState.Busy, timeout, ct);
            if (done.State == IndiState.Alert) throw new InvalidOperationException($"{Name} reported an error for {property}");
        }
        catch (OperationCanceledException)
        {
            await abort();
            throw;
        }
    }
}

public sealed class IndiCamera(IndiClient client, string name) : IndiDevice(client, name), ICamera, IGuideOutput
{
    private const string ImageBlob = "CCD1";

    public CameraState State { get; private set; } = CameraState.Idle;

    // Drivers disagree on where gain/offset live: the standard CCD_GAIN/CCD_OFFSET (simulator, QHY…)
    // or elements of CCD_CONTROLS (ZWO ASI). Try each in order.
    private static readonly (string Property, string Element)[] GainSources = [("CCD_GAIN", "GAIN"), ("CCD_CONTROLS", "Gain")];
    private static readonly (string Property, string Element)[] OffsetSources = [("CCD_OFFSET", "OFFSET"), ("CCD_CONTROLS", "Offset")];

    // ZWO's anti-dew heater: an on/off (0/1) element of CCD_CONTROLS, named as the SDK names it ("AntiDewHeater").
    private IndiElement? DewHeaterElement =>
        Client.GetProperty(Name, "CCD_CONTROLS")?.Elements.Values
            .FirstOrDefault(e => e.Name.Contains("DewHeater", StringComparison.OrdinalIgnoreCase));

    // INDI reports the current temperature but not the requested one, so remember what we asked for.
    private double? _targetTemperature;

    public double? PixelSize =>
        Client.GetProperty(Name, "CCD_INFO")?["CCD_PIXEL_SIZE"]?.TryNumber is > 0 and var size ? size : null;

    public (int Width, int Height)? SensorSize =>
        Client.GetProperty(Name, "CCD_INFO") is { } info
        && info["CCD_MAX_X"]?.TryNumber is > 0 and var x && info["CCD_MAX_Y"]?.TryNumber is > 0 and var y
            ? ((int)x, (int)y)
            : null;

    public async Task LinkDevicesAsync(LinkedDevices devices, CancellationToken ct)
    {
        // Defined even before connecting; defaults point at INDI's simulators, so always set every element.
        var property = await Client.WaitForAsync(Name, "ACTIVE_DEVICES", _ => true, TimeSpan.FromSeconds(10), ct);
        var values = new (string Element, string? Device)[]
            {
                ("ACTIVE_TELESCOPE", devices.Mount),
                ("ACTIVE_FILTER", devices.FilterWheel),
                ("ACTIVE_FOCUSER", devices.Focuser),
                ("ACTIVE_ROTATOR", devices.Rotator),
            }
            .Where(v => property.Elements.ContainsKey(v.Element))
            .Select(v => (v.Element, v.Device ?? ""))
            .ToArray();
        await Client.SetTextsAsync(Name, "ACTIVE_DEVICES", ct, values);
    }

    public async Task SetOpticsAsync(Optics optics, CancellationToken ct)
    {
        // Since INDI 2.0 every camera has its own SCOPE_INFO, defined once the camera is connected.
        await Client.WaitForAsync(Name, "SCOPE_INFO", _ => true, TimeSpan.FromSeconds(10), ct);
        await Client.SetNumbersAsync(Name, "SCOPE_INFO", ct,
            ("FOCAL_LENGTH", optics.FocalLength), ("APERTURE", optics.Aperture));
    }

    public CameraControls Controls
    {
        get
        {
            var temperature = Client.GetProperty(Name, "CCD_TEMPERATURE");
            var cooler = Client.GetProperty(Name, "CCD_COOLER");
            var coolerStatus = temperature is null && cooler is null
                ? null
                : new CoolerStatus(
                    CanCool: cooler is not null || temperature?.Permission is "rw" or "wo",
                    On: cooler?["COOLER_ON"]?.IsOn ?? false,
                    Temperature: temperature?["CCD_TEMPERATURE_VALUE"]?.TryNumber,
                    TargetTemperature: _targetTemperature,
                    PowerPercent: Client.GetProperty(Name, "CCD_COOLER_POWER")?["CCD_COOLER_VALUE"]?.TryNumber);

            var frame = Client.GetProperty(Name, "CCD_FRAME_TYPE");
            FrameType? frameType = frame?.Elements.Values.FirstOrDefault(e => e.IsOn)?.Name switch
            {
                "FRAME_LIGHT" => FrameType.Light,
                "FRAME_BIAS" => FrameType.Bias,
                "FRAME_DARK" => FrameType.Dark,
                "FRAME_FLAT" => FrameType.Flat,
                _ => null,
            };

            return new CameraControls(
                coolerStatus,
                ToSetting(FindElement(GainSources)),
                ToSetting(FindElement(OffsetSources)),
                ToSetting(Client.GetProperty(Name, "CCD_BINNING")?["HOR_BIN"]),
                frameType,
                DewHeaterElement?.TryNumber is { } heater ? heater > 0 : null);
        }
    }

    public async Task ApplyAsync(CameraSettingsUpdate update, CancellationToken ct)
    {
        EnsureConnected();

        if (update.FrameType is { } frameType)
        {
            RequireProperty("CCD_FRAME_TYPE", "frame types");
            await Client.SetSwitchesAsync(Name, "CCD_FRAME_TYPE", ct, ("FRAME_" + frameType.ToString().ToUpperInvariant(), true));
        }
        if (update.Binning is { } binning)
        {
            var element = RequireProperty("CCD_BINNING", "binning")["HOR_BIN"];
            Validate("Binning", binning, element);
            await Client.SetNumbersAsync(Name, "CCD_BINNING", ct, ("HOR_BIN", binning), ("VER_BIN", binning));
        }
        if (update.Gain is { } gain) await SetFirstAsync(GainSources, "Gain", gain, ct);
        if (update.Offset is { } offset) await SetFirstAsync(OffsetSources, "Offset", offset, ct);
        if (update.TargetTemperature is { } target)
        {
            var temperature = RequireProperty("CCD_TEMPERATURE", "temperature control");
            if (temperature.Permission == "ro") throw new ArgumentException($"{Name} has no cooler");
            Validate("Target temperature", target, temperature["CCD_TEMPERATURE_VALUE"]);
            await Client.SetNumbersAsync(Name, "CCD_TEMPERATURE", ct, ("CCD_TEMPERATURE_VALUE", target));
            _targetTemperature = target;
        }
        if (update.DewHeater is { } heat)
        {
            if (DewHeaterElement is not { } heater) throw new ArgumentException($"{Name} has no dew heater");
            await Client.SetNumbersAsync(Name, "CCD_CONTROLS", ct, (heater.Name, heat ? 1 : 0));
        }
        if (update.CoolerOn is { } on)
        {
            // Some drivers have no cooler switch and cool whenever a temperature is set.
            if (HasProperty("CCD_COOLER"))
                await Client.SetSwitchesAsync(Name, "CCD_COOLER", ct, (on ? "COOLER_ON" : "COOLER_OFF", true));
            else if (on && update.TargetTemperature is null)
                throw new ArgumentException($"{Name} has no cooler switch; set a target temperature instead");
        }
    }

    private IndiElement? FindElement((string Property, string Element)[] sources) =>
        sources.Select(s => Client.GetProperty(Name, s.Property)?[s.Element]).FirstOrDefault(e => e is not null);

    private async Task SetFirstAsync((string Property, string Element)[] sources, string label, double value, CancellationToken ct)
    {
        foreach (var (property, element) in sources)
        {
            if (Client.GetProperty(Name, property)?[element] is not { } found) continue;
            Validate(label, value, found);
            await Client.SetNumbersAsync(Name, property, ct, (element, value));
            return;
        }
        throw new ArgumentException($"{Name} has no {label.ToLowerInvariant()} setting");
    }

    private IndiProperty RequireProperty(string property, string what) =>
        Client.GetProperty(Name, property) ?? throw new ArgumentException($"{Name} doesn't support {what}");

    private static void Validate(string label, double value, IndiElement? element)
    {
        if (element is { Max: var max, Min: var min } && max > min && (value < min || value > max))
            throw new ArgumentException($"{label} must be between {min:G} and {max:G}");
    }

    private static NumberSetting? ToSetting(IndiElement? element) =>
        element?.TryNumber is { } value ? new NumberSetting(value, element.Min, element.Max, element.Step) : null;

    protected override async Task OnConnectedAsync(CancellationToken ct)
    {
        State = CameraState.Idle; // a fresh connection clears any error from before (e.g. unplugged mid-exposure)
        // Wait briefly for the driver to define its post-connect properties.
        await Client.WaitForAsync(Name, "CCD_EXPOSURE", _ => true, TimeSpan.FromSeconds(10), ct);
        await EnsureImagesComeToUsAsync(ct);
    }

    /// <summary>
    /// Makes sure finished frames are sent to NOVA: images (BLOBs) are enabled for this client connection,
    /// the driver uploads to the client rather than only saving locally, and the format is FITS.
    /// Done before every exposure, not just on connect: BLOB delivery is per client connection, so it's
    /// lost if NOVA reconnects to a driver that stayed connected, and settings can be changed elsewhere.
    /// </summary>
    private async Task EnsureImagesComeToUsAsync(CancellationToken ct)
    {
        await Client.EnableBlobAsync(Name, ct);
        // Client or Both are fine; only Local (save on the server, send nothing) needs changing.
        if (Client.GetProperty(Name, "UPLOAD_MODE") is { } upload && upload["UPLOAD_CLIENT"]?.IsOn != true && upload["UPLOAD_BOTH"]?.IsOn != true)
            await Client.SetSwitchesAsync(Name, "UPLOAD_MODE", ct, ("UPLOAD_CLIENT", true));
        if (Client.GetProperty(Name, "CCD_TRANSFER_FORMAT") is { } format && format["FORMAT_FITS"]?.IsOn != true)
            await Client.SetSwitchesAsync(Name, "CCD_TRANSFER_FORMAT", ct, ("FORMAT_FITS", true));
    }

    public async Task<CapturedImage> ExposeAsync(ExposureRequest request, IProgress<ExposureProgress>? progress, CancellationToken ct)
    {
        EnsureConnected();
        if (State is CameraState.Exposing or CameraState.Downloading) throw new InvalidOperationException("Camera is busy");

        var duration = request.DurationSeconds;
        var image = new TaskCompletionSource<(byte[] Data, string Format)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnUpdate(object? sender, IndiProperty p)
        {
            if (p.Device != Name) return;
            if (p.Name == "CCD_EXPOSURE")
            {
                if (p.State == IndiState.Alert)
                {
                    image.TrySetException(new InvalidOperationException("The camera driver reported the exposure failed"));
                    return;
                }
                var remaining = p["CCD_EXPOSURE_VALUE"]?.Number ?? 0;
                if (p.State == IndiState.Ok && remaining <= 0) State = CameraState.Downloading;
                progress?.Report(new ExposureProgress(Math.Max(0, duration - remaining), duration, State));
            }
            else if (p.Name == ImageBlob && p[ImageBlob] is { BlobData: { } data } element)
            {
                element.BlobData = null; // don't keep the frame alive inside the property mirror
                image.TrySetResult((data, element.BlobFormat ?? ".fits"));
            }
        }

        // Unplugging the camera makes the driver delete the whole device: fail now rather than at the timeout.
        void OnDeleted(object? sender, IndiDeleteMessage deleted)
        {
            if (deleted.Device == Name && deleted.Name is null)
                image.TrySetException(new IOException($"{Name} disappeared during the exposure (unplugged?)"));
        }

        var started = DateTime.UtcNow;
        Client.PropertyUpdated += OnUpdate;
        Client.PropertyDeleted += OnDeleted;
        try
        {
            State = CameraState.Exposing;
            await EnsureImagesComeToUsAsync(ct);
            await Client.SetNumbersAsync(Name, "CCD_EXPOSURE", ct, ("CCD_EXPOSURE_VALUE", duration));
            (byte[] Data, string Format) received;
            try
            {
                received = await image.Task.WaitAsync(TimeSpan.FromSeconds(duration + 120), ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"No image arrived from {Name}. Check its Upload mode is Client in the driver settings (⚙), then try again.");
            }
            var (data, format) = received;

            if (!format.Contains("fits", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Camera sent '{format}'; only FITS is supported so far");

            var raw = Fits.Read(data, format);
            State = CameraState.Idle;
            return new CapturedImage { Image = raw, FitsBytes = data, StartedUtc = started, DurationSeconds = duration };
        }
        catch (OperationCanceledException)
        {
            if (HasProperty("CCD_ABORT_EXPOSURE"))
                await Client.SetSwitchesAsync(Name, "CCD_ABORT_EXPOSURE", CancellationToken.None, ("ABORT", true));
            State = CameraState.Idle;
            throw;
        }
        catch
        {
            State = CameraState.Error;
            throw;
        }
        finally
        {
            Client.PropertyUpdated -= OnUpdate;
            Client.PropertyDeleted -= OnDeleted;
        }
    }
}

public sealed class IndiMount(IndiClient client, string name) : IndiDevice(client, name), IMount, IGuideOutput
{
    private const string Coords = "EQUATORIAL_EOD_COORD"; // JNow

    public GeoLocation? Location
    {
        get
        {
            var p = Client.GetProperty(Name, "GEOGRAPHIC_COORD");
            if (p?["LAT"]?.TryNumber is not { } lat || p["LONG"]?.TryNumber is not { } lon) return null;
            // INDI measures longitude 0-360 eastwards; NOVA uses -180..180.
            return new GeoLocation(lat, lon > 180 ? lon - 360 : lon, p["ELEV"]?.TryNumber ?? 0);
        }
    }

    /// <summary>
    /// ZWO's driver (and any other with the same properties): After Meridian = Track, Meridian Limit ≥ the given
    /// degrees, Meridian Flip disabled. With "Stop" the mount stops tracking at the meridian, and with the mount's
    /// own flip on it could flip in the middle of an exposure.
    /// </summary>
    public async Task<string?> PrepareForMeridianFlipsAsync(double limitDegrees, CancellationToken ct)
    {
        if (!HasProperty("POST_MERIDIAN_TRACK")) return null;
        var changes = new List<string>();
        // The limit first: the driver sends all three settings together, and the mount may refuse "track after
        // the meridian" while the limit is still 0°.
        if (Client.GetProperty(Name, "MERIDIAN_LIMIT")?["LIMIT"]?.TryNumber is { } limit && limit < limitDegrees)
        {
            await Client.SetNumbersAsync(Name, "MERIDIAN_LIMIT", ct, ("LIMIT", Math.Ceiling(limitDegrees)));
            await SettledAsync("MERIDIAN_LIMIT", ct);
            changes.Add($"Meridian Limit: {limit:0}° → {Math.Ceiling(limitDegrees):0}°");
        }
        if (Client.GetProperty(Name, "POST_MERIDIAN_TRACK")?["TRACK"]?.IsOn != true)
        {
            await Client.SetSwitchesAsync(Name, "POST_MERIDIAN_TRACK", ct, ("TRACK", true));
            var after = await SettledAsync("POST_MERIDIAN_TRACK", ct);
            changes.Add(after?["TRACK"]?.IsOn == true && after.State != IndiState.Alert
                ? "After Meridian: Stop → Track"
                : "After Meridian: the mount refused Track (it will stop tracking at the limit, after NOVA's flip time)");
        }
        if (Client.GetProperty(Name, "MERIDIAN_FLIP")?["INDI_ENABLED"]?.IsOn == true)
        {
            await Client.SetSwitchesAsync(Name, "MERIDIAN_FLIP", ct, ("INDI_DISABLED", true));
            changes.Add("Meridian Flip (by the mount): Enabled → Disabled");
        }
        return changes.Count == 0 ? null : string.Join(", ", changes);
    }

    /// <summary>A property once the driver has answered a change (not Busy), or null if it didn't within 5 s.</summary>
    private async Task<IndiProperty?> SettledAsync(string property, CancellationToken ct)
    {
        try
        {
            return await Client.WaitForAsync(Name, property, p => p.State != IndiState.Busy, TimeSpan.FromSeconds(5), ct, checkCurrent: false);
        }
        catch (TimeoutException)
        {
            return Client.GetProperty(Name, property);
        }
    }

    public async Task StartTrackingAsync(CancellationToken ct)
    {
        if (HasProperty("TELESCOPE_TRACK_STATE"))
            await Client.SetSwitchesAsync(Name, "TELESCOPE_TRACK_STATE", ct, ("TRACK_ON", true));
    }

    public async Task SetSiteAsync(GeoLocation location, DateTimeOffset now, CancellationToken ct)
    {
        // Time first, then the location: ZWO mounts only accept a GoTo once the location has been set after
        // power-on (the driver reading back the stored one isn't enough), and setting it last is what works by hand.
        await SetClockAsync(now, ct);
        if (HasProperty("GEOGRAPHIC_COORD"))
            await Client.SetNumbersAsync(Name, "GEOGRAPHIC_COORD", ct,
                ("LAT", location.Latitude), ("LONG", (location.Longitude % 360 + 360) % 360), ("ELEV", location.Elevation));
    }

    public DateTime? ClockUtc =>
        Client.GetProperty(Name, "TIME_UTC")?["UTC"]?.Text is { } text
        && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc
            : null;

    public async Task SetClockAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!HasProperty("TIME_UTC")) return;
        await Client.SetTextsAsync(Name, "TIME_UTC", ct,
            ("UTC", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
            ("OFFSET", now.Offset.TotalHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
    }
    private static readonly TimeSpan MotionTimeout = TimeSpan.FromMinutes(10);

    public EquatorialCoordinates Coordinates
    {
        get
        {
            var p = Client.GetProperty(Name, Coords);
            return p is null ? default : new EquatorialCoordinates(p["RA"]?.Number ?? 0, p["DEC"]?.Number ?? 0);
        }
    }

    public PierSide? SideOfPier
    {
        get
        {
            var p = Client.GetProperty(Name, "TELESCOPE_PIER_SIDE");
            return p?["PIER_WEST"]?.IsOn == true ? PierSide.West : p?["PIER_EAST"]?.IsOn == true ? PierSide.East : null;
        }
    }

    public MountState State
    {
        get
        {
            var park = Client.GetProperty(Name, "TELESCOPE_PARK");
            var coords = Client.GetProperty(Name, Coords);
            if (park?.State == IndiState.Busy) return MountState.Parking;
            if (park?["PARK"]?.IsOn == true) return MountState.Parked;
            if (coords?.State == IndiState.Busy) return MountState.Slewing;
            if (coords?.State == IndiState.Alert) return MountState.Error;
            var tracking = Client.GetProperty(Name, "TELESCOPE_TRACK_STATE")?["TRACK_ON"]?.IsOn == true;
            return tracking ? MountState.Tracking : MountState.Idle;
        }
    }

    public async Task SlewAsync(EquatorialCoordinates target, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        // A mount whose clock has reset (ZWO mounts restart at 2000-01-01) refuses or misplaces every GoTo.
        if (ClockUtc is { Year: < 2021 }) await SetClockAsync(DateTimeOffset.Now, ct);

        await Client.SetSwitchesAsync(Name, "ON_COORD_SET", ct, ("TRACK", true)); // slew, then track
        // What the driver says while slewing: it sometimes names the reason for a refusal.
        var said = new List<string>();
        void Heard(object? sender, IndiMessage m)
        {
            if (m.Device == Name && m.Message is { } text) lock (said) said.Add(text.Trim());
        }
        Client.MessageReceived += Heard;
        try
        {
            await SendAndWaitAsync(Coords, () => Client.SetNumbersAsync(Name, Coords, ct,
                ("RA", target.RaHours), ("DEC", target.DecDegrees)), () => AbortAsync(CancellationToken.None), MotionTimeout, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("reported an error"))
        {
            string[] messages;
            lock (said) messages = [.. said];
            throw new InvalidOperationException(RefusalReason(target, messages, DateTime.UtcNow), ex);
        }
        finally
        {
            Client.MessageReceived -= Heard;
        }
    }

    /// <summary>
    /// Why the mount most likely refused a GoTo, from what can be checked: the target's altitude where the mount thinks
    /// it is, its clock, whether it has a location; plus whatever the driver said.
    /// </summary>
    internal string RefusalReason(EquatorialCoordinates targetJNow, IReadOnlyList<string> driverSaid, DateTime nowUtc)
    {
        var location = Location;
        var clock = ClockUtc;
        string reason;
        if (location is null || (location.Latitude == 0 && location.Longitude == 0))
            reason = "it doesn't know where it is: set your site in NOVA's Mount panel and reconnect";
        else if (clock is { } c && Math.Abs((c - nowUtc).TotalMinutes) > 5)
            reason = $"its clock says {c:yyyy-MM-dd HH:mm} UTC, {Describe(c - nowUtc)} out: reconnect it so NOVA sets the time";
        else
        {
            var utc = clock ?? nowUtc;
            var altitude = Horizon.AltAz(Horizon.ToVector(targetJNow, utc, location)).Altitude;
            var hourAngle = Horizon.HourAngle(targetJNow, utc, location.Longitude);
            // The driver's own altitude limits (ZWO's "Altitude Limits" tab), when it has them.
            var limitOn = Client.GetProperty(Name, "ALTITUDE_LIMIT_CONTROL")?["ENABLE"]?.IsOn == true;
            var lower = limitOn ? Client.GetProperty(Name, "ALTITUDE_LOWER_LIMIT")?["LOWER_LIMIT"]?.TryNumber : null;
            var upper = limitOn ? Client.GetProperty(Name, "ALTITUDE_UPPER_LIMIT")?["UPPER_LIMIT"]?.TryNumber : null;
            reason = altitude < 0
                ? $"the target is below the horizon ({altitude:0}° altitude)"
                : lower is { } low && altitude < low
                    ? $"the target is {altitude:0}° up, below the mount's lower altitude limit of {low:0}° (driver settings → Altitude Limits)"
                : upper is { } high && altitude > high
                    ? $"the target is {altitude:0}° up, above the mount's upper altitude limit of {high:0}° (driver settings → Altitude Limits)"
                : Math.Abs(hourAngle) > 6
                    ? $"the target is under the pole ({Math.Abs(hourAngle):0.#} h {(hourAngle > 0 ? "past" : "before")} the meridian, {altitude:0}° up): " +
                      "the mount would have to point with its counterweight up, which ZWO mounts refuse. Wait until it's nearer the meridian"
                : altitude < 25
                    ? $"the target is only {altitude:0}° up, probably below the mount's own altitude limit; pick something higher"
                    : $"the target is {altitude:0}° up, the clock and location look right, and the mount gave no reason; check it isn't still moving, and try again";
        }
        var driver = driverSaid.Count > 0 ? $" The driver said: \"{string.Join(" / ", driverSaid.TakeLast(3))}\"." : "";
        return $"{Name} refused the slew: {reason}.{driver}";
    }

    private static string Describe(TimeSpan off)
    {
        var abs = off.Duration();
        return abs.TotalDays >= 2 ? $"{abs.TotalDays:0} days" : abs.TotalHours >= 2 ? $"{abs.TotalHours:0} hours" : $"{abs.TotalMinutes:0} minutes";
    }

    public IReadOnlyList<SlewRate> SlewRates
    {
        get
        {
            if (Client.GetProperty(Name, "TELESCOPE_SLEW_RATE") is not { } p) return [];
            var rates = p.Elements.Values.Select(e => new SlewRate(e.Name, string.IsNullOrWhiteSpace(e.Label) ? e.Name : e.Label));
            // ZWO mounts in Heavy Duty Mode go no faster than 720x: leave out the speeds they can't do.
            if (HeavyDutyMode) rates = rates.Where(r => Multiple(r.Label) is not { } x || x <= HeavyDutyMaxRate);
            return rates.ToList();
        }
    }

    private const double HeavyDutyMaxRate = 720;

    /// <summary>ZWO's Heavy Duty Mode (driver's Motion tab) is on.</summary>
    public bool HeavyDutyMode => Client.GetProperty(Name, "HEAVY_DUTY_MODE")?["INDI_ENABLED"]?.IsOn == true;

    /// <summary>"1440x" → 1440; null when the label isn't a multiple of sidereal.</summary>
    private static double? Multiple(string label)
    {
        var text = label.Trim();
        if (!text.EndsWith('x') && !text.EndsWith('X')) return null;
        return double.TryParse(text[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            ? x
            : null;
    }

    public string? CurrentSlewRate =>
        Client.GetProperty(Name, "TELESCOPE_SLEW_RATE")?.Elements.Values.FirstOrDefault(e => e.IsOn)?.Name;

    public Task SetSlewRateAsync(string name, CancellationToken ct)
    {
        if (!SlewRates.Any(r => r.Name == name)) throw new ArgumentException($"{Name} has no slew rate '{name}'");
        return Client.SetSwitchesAsync(Name, "TELESCOPE_SLEW_RATE", ct, (name, true));
    }

    public Task StartMotionAsync(MotionDirection direction, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        return direction switch
        {
            MotionDirection.North => Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_NS", ct, ("MOTION_NORTH", true)),
            MotionDirection.South => Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_NS", ct, ("MOTION_SOUTH", true)),
            MotionDirection.East => Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_WE", ct, ("MOTION_EAST", true)),
            _ => Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_WE", ct, ("MOTION_WEST", true)),
        };
    }

    /// <summary>
    /// Stops a hand move and makes sure it stopped. Turning the motion switch off isn't always enough (a ZWO AM3 was
    /// seen carrying on through it), so a full abort follows, which mounts obey; then the position is watched for a
    /// moment, and if it's still changing, abort again and say so. Tracking is put back as it was.
    /// </summary>
    public async Task StopMotionAsync(MotionDirection? direction, CancellationToken ct)
    {
        var tracking = Client.GetProperty(Name, "TELESCOPE_TRACK_STATE")?["TRACK_ON"]?.IsOn;
        // Still moving on the other axis (two buttons held, one let go): stop just this one, no abort.
        bool On(string property) => Client.GetProperty(Name, property)?.Elements.Values.Any(e => e.IsOn) == true;
        if (direction is MotionDirection.North or MotionDirection.South && On("TELESCOPE_MOTION_WE")
            || direction is MotionDirection.East or MotionDirection.West && On("TELESCOPE_MOTION_NS"))
        {
            await Client.SetSwitchesAsync(Name, direction is MotionDirection.North or MotionDirection.South ? "TELESCOPE_MOTION_NS" : "TELESCOPE_MOTION_WE", ct,
                direction is MotionDirection.North or MotionDirection.South ? [("MOTION_NORTH", false), ("MOTION_SOUTH", false)] : [("MOTION_WEST", false), ("MOTION_EAST", false)]);
            return;
        }
        try
        {
            if (direction is null or MotionDirection.North or MotionDirection.South)
                await Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_NS", CancellationToken.None, ("MOTION_NORTH", false), ("MOTION_SOUTH", false));
            if (direction is null or MotionDirection.East or MotionDirection.West)
                await Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_WE", CancellationToken.None, ("MOTION_WEST", false), ("MOTION_EAST", false));
        }
        finally
        {
            if (HasProperty("TELESCOPE_ABORT_MOTION")) await AbortAsync(CancellationToken.None);
        }

        // Stopped? The reading lags (drivers read the position about once a second), so first let one arrive from after
        // the stop, then watch. (A mount that's tracking keeps its RA and Dec; one that isn't drifts 15″ a second.)
        await Task.Delay(ReadingLag, CancellationToken.None);
        var before = Coordinates;
        await Task.Delay(StopCheckTime, CancellationToken.None);
        // Moving again on purpose meanwhile (a held button renewed after a hiccup): that's not a failed stop.
        if (On("TELESCOPE_MOTION_NS") || On("TELESCOPE_MOTION_WE")) return;
        if (Precession.Separation(before, Coordinates) > StillMovingDegrees)
        {
            if (HasProperty("TELESCOPE_ABORT_MOTION")) await AbortAsync(CancellationToken.None);
            throw new InvalidOperationException($"{Name} didn't stop when told to (it was still moving {StopCheckTime.TotalSeconds:0.#} s later): aborted it. Check it has stopped");
        }
        if (tracking == true && Client.GetProperty(Name, "TELESCOPE_TRACK_STATE")?["TRACK_ON"] is { IsOn: false })
            await Client.SetSwitchesAsync(Name, "TELESCOPE_TRACK_STATE", ct, ("TRACK_ON", true));
    }

    /// <summary>How long a driver can take to report a new position (it polls the mount).</summary>
    private static readonly TimeSpan ReadingLag = TimeSpan.FromSeconds(1.2);

    /// <summary>How long after a stop the position is watched to make sure it stopped.</summary>
    private static readonly TimeSpan StopCheckTime = TimeSpan.FromSeconds(1.5);

    /// <summary>Moving this far (degrees) in <see cref="StopCheckTime"/> after a stop means it didn't stop.</summary>
    private const double StillMovingDegrees = 0.1;

    public bool CanMoveRaAxis => HasProperty("TELESCOPE_MOTION_WE");

    /// <summary>Beyond this Dec the reported RA can't measure an RA turn.</summary>
    public const double MaxDecForRaTurn = 89.8;

    /// <summary>A move whose reading hasn't changed for this long is stopped.</summary>
    private static readonly TimeSpan StallTime = TimeSpan.FromSeconds(2);

    /// <summary>Faster than any mount slews (degrees a second): a reading that jumps more is wrong.</summary>
    private const double MaxPlausibleSpeed = 10;

    /// <summary>Roughly how long a mount takes to stop once told to (seconds): stopping is started that early.</summary>
    private const double StopSeconds = 0.3;

    public async Task MoveRaAxisAsync(double degrees, CancellationToken ct)
    {
        EnsureConnected();
        if (State == MountState.Parked) throw new InvalidOperationException("Mount is parked");
        if (!CanMoveRaAxis) throw new NotSupportedException($"{Name} can't drive its RA axis directly");

        // Brisk but safe: the second-fastest rate the driver lists (rates are names like "720x", not °/s). The fastest
        // can be one the mount doesn't do — ZWO's driver lists the AM5's 1440x, which an AM3 ignores.
        if (Client.GetProperty(Name, "TELESCOPE_SLEW_RATE") is { Elements.Count: > 0 } rates)
        {
            var names = rates.Elements.Keys.ToList();
            await Client.SetSwitchesAsync(Name, "TELESCOPE_SLEW_RATE", ct, (names[Math.Max(0, names.Count - 2)], true));
        }

        // The turn is measured by the RA the mount reports, which means nothing right at the pole (a mount at home
        // reports Dec 90° and an RA that hardly follows the axis): there the caller must step off it first.
        if (Math.Abs(Coordinates.DecDegrees) > MaxDecForRaTurn)
            throw new InvalidOperationException($"{Name} points at the pole, where its RA can't measure a turn: move it a little off the pole first");

        // The driver's motion runs until it's stopped, and the reported RA lags the mount (drivers poll it about once a
        // second), so the turn is watched closely:
        //  - the reading must start changing, keep going one way and never jump further than any mount moves;
        //  - the real speed is measured from the readings, and the mount is stopped early by however far it will have
        //    moved since the last reading, plus the time it takes to stop, so it lands on the angle, not past it;
        //  - if the reading stops changing, stop at once.
        var target = Math.Abs(degrees);
        var start = Coordinates.RaHours;
        double Signed() => (((Coordinates.RaHours - start) % 24 + 36) % 24 - 12) * 15;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = (Turned: 0.0, At: TimeSpan.Zero);  // the latest reading that changed
        double? speed = null;                           // degrees per second, measured
        var sign = 0;
        await Client.SetSwitchesAsync(Name, "TELESCOPE_MOTION_WE", ct, (degrees > 0 ? "MOTION_WEST" : "MOTION_EAST", true));
        try
        {
            while (true)
            {
                await Task.Delay(50, ct);
                var now = clock.Elapsed;
                var signed = Signed();
                var turned = Math.Abs(signed);
                if (Math.Abs(turned - last.Turned) > 0.01)
                {
                    // A new reading: check it's plausible, then update the measured speed.
                    if (sign == 0 && turned > 0.2) sign = Math.Sign(signed);
                    if (sign != 0 && Math.Sign(signed) != sign && turned > 0.5)
                        throw new InvalidOperationException($"{Name}'s RA went back the other way while turning: stopped it");
                    if (turned < last.Turned - 0.5)
                        throw new InvalidOperationException($"{Name}'s RA went backwards ({last.Turned:0.#}° to {turned:0.#}°) while turning: stopped it");
                    var step = (turned - last.Turned) / Math.Max((now - last.At).TotalSeconds, 0.05);
                    if (turned - last.Turned > 1 && step > MaxPlausibleSpeed)
                        throw new InvalidOperationException($"{Name}'s RA jumped {turned - last.Turned:0.#}° in {(now - last.At).TotalSeconds:0.0} s, faster than any mount turns: stopped it");
                    if (last.Turned > 0) speed = speed is { } v ? 0.5 * v + 0.5 * step : step;
                    last = (turned, now);
                }

                // Where it is now: the last reading plus the time since, at the measured speed.
                var predicted = last.Turned + (speed ?? 0) * (now - last.At).TotalSeconds;
                if (predicted + (speed ?? 0) * StopSeconds >= target || turned >= target) break;

                if (now - last.At > StallTime)
                    throw new InvalidOperationException(last.Turned < 0.2
                        ? $"{Name} didn't move when driven in RA (or its RA didn't change): stopped it"
                        : $"{Name}'s RA stopped changing after {last.Turned:0.#}° of {target:0}°: stopped it");
                if (now > TimeSpan.FromSeconds(30 + target * 10))
                    throw new TimeoutException($"{Name} only turned {turned:0.0}° of {target:0}° in RA: stopped it");
            }
        }
        finally
        {
            await StopMotionAsync(MotionDirection.West, CancellationToken.None); // and makes sure it stopped
        }

        // Make sure it tracks again (some drivers stop tracking after manual motion).
        if (Client.GetProperty(Name, "TELESCOPE_TRACK_STATE")?["TRACK_ON"] is { IsOn: false })
            await Client.SetSwitchesAsync(Name, "TELESCOPE_TRACK_STATE", ct, ("TRACK_ON", true));
    }

    public async Task SyncAsync(EquatorialCoordinates actual, CancellationToken ct)
    {
        EnsureConnected();
        // ON_COORD_SET=SYNC makes the next coordinates a sync instead of a slew; put it back to TRACK afterwards.
        await Client.SetSwitchesAsync(Name, "ON_COORD_SET", ct, ("SYNC", true));
        try
        {
            await SendAndWaitAsync(Coords, () => Client.SetNumbersAsync(Name, Coords, ct,
                ("RA", actual.RaHours), ("DEC", actual.DecDegrees)), () => Task.CompletedTask, TimeSpan.FromSeconds(30), ct);
        }
        finally
        {
            await Client.SetSwitchesAsync(Name, "ON_COORD_SET", CancellationToken.None, ("TRACK", true));
        }
    }

    public Task AbortAsync(CancellationToken ct) =>
        Client.SetSwitchesAsync(Name, "TELESCOPE_ABORT_MOTION", ct, ("ABORT", true));

    /// <summary>ZWO's driver (and any with the same properties): its Altitude Limits, set and switched on.</summary>
    public async Task<bool> SetAltitudeLimitsAsync(double lower, double upper, CancellationToken ct)
    {
        if (!HasProperty("ALTITUDE_LIMIT_CONTROL") || !HasProperty("ALTITUDE_LOWER_LIMIT") || !HasProperty("ALTITUDE_UPPER_LIMIT"))
            return false;
        // Within what the driver accepts.
        double Fit(string property, string element, double value) =>
            Client.GetProperty(Name, property)?[element] is { } e && e.Max > e.Min
                ? Math.Clamp(value, e.Min, e.Max) : value;
        await Client.SetNumbersAsync(Name, "ALTITUDE_LOWER_LIMIT", ct, ("LOWER_LIMIT", Fit("ALTITUDE_LOWER_LIMIT", "LOWER_LIMIT", lower)));
        await Client.SetNumbersAsync(Name, "ALTITUDE_UPPER_LIMIT", ct, ("UPPER_LIMIT", Fit("ALTITUDE_UPPER_LIMIT", "UPPER_LIMIT", upper)));
        await Client.SetSwitchesAsync(Name, "ALTITUDE_LIMIT_CONTROL", ct, ("ENABLE", true));
        return true;
    }

    public Task ParkAsync(CancellationToken ct)
    {
        EnsureConnected();
        return SendAndWaitAsync("TELESCOPE_PARK", () => Client.SetSwitchesAsync(Name, "TELESCOPE_PARK", ct, ("PARK", true)),
            () => AbortAsync(CancellationToken.None), MotionTimeout, ct);
    }

    public Task UnparkAsync(CancellationToken ct)
    {
        EnsureConnected();
        return SendAndWaitAsync("TELESCOPE_PARK", () => Client.SetSwitchesAsync(Name, "TELESCOPE_PARK", ct, ("UNPARK", true)),
            () => AbortAsync(CancellationToken.None), MotionTimeout, ct);
    }

}

/// <summary>
/// INDI focuser using the standard properties: ABS_FOCUS_POSITION, REL_FOCUS_POSITION + FOCUS_MOTION,
/// FOCUS_ABORT_MOTION, FOCUS_MAX and FOCUS_TEMPERATURE (the ZWO EAF supports all of them).
/// </summary>
public sealed class IndiFocuser(IndiClient client, string name) : IndiDevice(client, name), IFocuser
{
    private const string Absolute = "ABS_FOCUS_POSITION";
    private const string Relative = "REL_FOCUS_POSITION";
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(3);

    private IndiElement? AbsoluteElement => Client.GetProperty(Name, Absolute)?["FOCUS_ABSOLUTE_POSITION"];

    public bool CanMoveAbsolute => AbsoluteElement is not null;
    public int Position => (int)Math.Round(AbsoluteElement?.TryNumber ?? 0);
    public int MinPosition => (int)Math.Round(AbsoluteElement?.Min ?? 0);

    public int MaxPosition
    {
        get
        {
            // FOCUS_MAX is the user-configurable travel limit; fall back to the property's range.
            var max = Client.GetProperty(Name, "FOCUS_MAX")?["FOCUS_MAX_VALUE"]?.TryNumber ?? AbsoluteElement?.Max ?? 0;
            return (int)Math.Round(max);
        }
    }

    public bool IsMoving =>
        Client.GetProperty(Name, Absolute)?.State == IndiState.Busy
        || Client.GetProperty(Name, Relative)?.State == IndiState.Busy;

    public double? Temperature => Client.GetProperty(Name, "FOCUS_TEMPERATURE")?["TEMPERATURE"]?.TryNumber;

    public Task MoveToAsync(int position, CancellationToken ct)
    {
        EnsureConnected();
        if (!CanMoveAbsolute) throw new ArgumentException($"{Name} can only move relative steps");
        if (MaxPosition > MinPosition && (position < MinPosition || position > MaxPosition))
            throw new ArgumentException($"Position must be between {MinPosition} and {MaxPosition}");

        return SendAndWaitAsync(Absolute,
            () => Client.SetNumbersAsync(Name, Absolute, ct, ("FOCUS_ABSOLUTE_POSITION", position)),
            () => HaltAsync(CancellationToken.None), MoveTimeout, ct);
    }

    public async Task MoveRelativeAsync(int steps, CancellationToken ct)
    {
        EnsureConnected();
        if (steps == 0) return;

        if (CanMoveAbsolute)
        {
            // Clamp to the travel limits rather than refusing a step that would overshoot.
            var target = Position + steps;
            if (MaxPosition > MinPosition) target = Math.Clamp(target, MinPosition, MaxPosition);
            await MoveToAsync(target, ct);
            return;
        }

        if (Client.GetProperty(Name, Relative) is null) throw new ArgumentException($"{Name} can't move");
        await Client.SetSwitchesAsync(Name, "FOCUS_MOTION", ct, (steps < 0 ? "FOCUS_INWARD" : "FOCUS_OUTWARD", true));
        await SendAndWaitAsync(Relative,
            () => Client.SetNumbersAsync(Name, Relative, ct, ("FOCUS_RELATIVE_POSITION", Math.Abs(steps))),
            () => HaltAsync(CancellationToken.None), MoveTimeout, ct);
    }

    public Task HaltAsync(CancellationToken ct) =>
        HasProperty("FOCUS_ABORT_MOTION")
            ? Client.SetSwitchesAsync(Name, "FOCUS_ABORT_MOTION", ct, ("ABORT", true))
            : Task.CompletedTask;
}

/// <summary>INDI filter wheel: FILTER_SLOT (1-based) and FILTER_NAME (FILTER_SLOT_NAME_n). Covers the ZWO EFW.</summary>
public sealed class IndiFilterWheel(IndiClient client, string name) : IndiDevice(client, name), IFilterWheel
{
    private const string Slot = "FILTER_SLOT";
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(1);

    private IndiElement? SlotElement => Client.GetProperty(Name, Slot)?["FILTER_SLOT_VALUE"];

    public int Position => (int)Math.Round(SlotElement?.TryNumber ?? 0);

    public int SlotCount
    {
        get
        {
            var max = (int)Math.Round(SlotElement?.Max ?? 0);
            return max > 0 ? max : Client.GetProperty(Name, "FILTER_NAME")?.Elements.Count ?? 0;
        }
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            var names = Client.GetProperty(Name, "FILTER_NAME");
            return Enumerable.Range(1, SlotCount)
                .Select(i => names?[$"FILTER_SLOT_NAME_{i}"]?.Text is { Length: > 0 } text ? text : $"Slot {i}")
                .ToList();
        }
    }

    public bool IsMoving => Client.GetProperty(Name, Slot)?.State == IndiState.Busy;

    public Task MoveToAsync(int slot, CancellationToken ct)
    {
        EnsureConnected();
        if (slot < 1 || slot > SlotCount) throw new ArgumentException($"Slot must be between 1 and {SlotCount}");
        // Wheels can't be stopped mid-move, so cancelling just stops waiting.
        return SendAndWaitAsync(Slot, () => Client.SetNumbersAsync(Name, Slot, ct, ("FILTER_SLOT_VALUE", slot)),
            () => Task.CompletedTask, MoveTimeout, ct);
    }
}

/// <summary>
/// INDI light box (FLAT_LIGHT_CONTROL, FLAT_LIGHT_INTENSITY) and dust cap (CAP_PARK): flat panels, flip-flats and the
/// Light Panel Simulator. Either half may be missing (a plain panel has no cover).
/// </summary>
public sealed class IndiFlatPanel(IndiClient client, string name) : IndiDevice(client, name), IFlatPanel
{
    private const string Light = "FLAT_LIGHT_CONTROL";
    private const string Intensity = "FLAT_LIGHT_INTENSITY";
    private const string Cap = "CAP_PARK";
    private static readonly TimeSpan CoverTimeout = TimeSpan.FromMinutes(1);

    public bool HasLight => HasProperty(Light);
    public bool LightOn => Client.GetProperty(Name, Light)?["FLAT_LIGHT_ON"]?.IsOn == true;

    public NumberSetting? Brightness =>
        Client.GetProperty(Name, Intensity)?["FLAT_LIGHT_INTENSITY_VALUE"] is { TryNumber: { } value } e
            ? new NumberSetting(value, e.Min, e.Max, e.Step)
            : null;

    public bool HasCover => HasProperty(Cap);
    public bool? CoverClosed => HasCover ? Client.GetProperty(Name, Cap)?["PARK"]?.IsOn == true : null;
    public bool CoverMoving => Client.GetProperty(Name, Cap)?.State == IndiState.Busy;

    public async Task SetLightAsync(bool on, double? brightness, CancellationToken ct)
    {
        EnsureConnected();
        if (brightness is { } b && HasProperty(Intensity))
            await SendAndSettleAsync(Intensity, () => Client.SetNumbersAsync(Name, Intensity, ct, ("FLAT_LIGHT_INTENSITY_VALUE", b)), ct);
        if (HasLight && LightOn != on)
            await SendAndSettleAsync(Light, () => Client.SetSwitchesAsync(Name, Light, ct, (on ? "FLAT_LIGHT_ON" : "FLAT_LIGHT_OFF", true)), ct);
    }

    public Task SetCoverAsync(bool closed, CancellationToken ct)
    {
        EnsureConnected();
        if (!HasCover) throw new InvalidOperationException($"{Name} has no cover");
        if (CoverClosed == closed && !CoverMoving) return Task.CompletedTask;
        return SendAndWaitAsync(Cap, () => Client.SetSwitchesAsync(Name, Cap, ct, (closed ? "PARK" : "UNPARK", true)),
            () => Task.CompletedTask, CoverTimeout, ct);
    }

    /// <summary>Lights usually answer at once without going Busy: wait for the reply rather than for Busy.</summary>
    private async Task SendAndSettleAsync(string property, Func<Task> send, CancellationToken ct)
    {
        var reply = Client.WaitForAsync(Name, property, p => p.State != IndiState.Busy, TimeSpan.FromSeconds(10), ct, checkCurrent: false);
        await send();
        var settled = await reply;
        if (settled.State == IndiState.Alert) throw new InvalidOperationException($"{Name} reported an error for {property}");
    }
}

/// <summary>INDI rotator: ABS_ROTATOR_ANGLE and ROTATOR_ABORT_MOTION. Covers the ZWO CAA.</summary>
public sealed class IndiRotator(IndiClient client, string name) : IndiDevice(client, name), IRotator
{
    private const string AngleProperty = "ABS_ROTATOR_ANGLE";
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(3);

    public double Angle => Client.GetProperty(Name, AngleProperty)?["ANGLE"]?.TryNumber ?? 0;
    public bool IsMoving => Client.GetProperty(Name, AngleProperty)?.State == IndiState.Busy;

    public Task MoveToAsync(double angle, CancellationToken ct)
    {
        EnsureConnected();
        angle = (angle % 360 + 360) % 360;
        return SendAndWaitAsync(AngleProperty, () => Client.SetNumbersAsync(Name, AngleProperty, ct, ("ANGLE", angle)),
            () => HaltAsync(CancellationToken.None), MoveTimeout, ct);
    }

    public Task HaltAsync(CancellationToken ct) =>
        HasProperty("ROTATOR_ABORT_MOTION")
            ? Client.SetSwitchesAsync(Name, "ROTATOR_ABORT_MOTION", ct, ("ABORT", true))
            : Task.CompletedTask;
}
