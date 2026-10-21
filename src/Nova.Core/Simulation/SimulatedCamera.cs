using System.Globalization;
using Nova.Core.Astronomy;
using Nova.Core.Catalogs;
using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Core.PlateSolving;

namespace Nova.Core.Simulation;

/// <summary>
/// A cooled camera that renders a deterministic star field around the mount's position.
/// Gain, offset, binning, frame type and sensor temperature all affect the frames it produces.
/// A colour camera produces an RGGB Bayer mosaic (when unbinned) with coloured stars and a
/// light-polluted sky, so debayering has something to show.
/// </summary>
internal sealed class SimulatedCamera(
    string name, int width, int height, double pixelSize, double focalLength, bool color, Func<EquatorialCoordinates> pointing,
    Func<double>? defocus = null, Func<LinkedDevices, IEnumerable<FitsCard>>? linkedHeaders = null,
    Func<double>? skyAngle = null, Func<double?>? panelGlow = null) : ICamera
{
    private LinkedDevices _links = new(null, null, null, null);

    public Task LinkDevicesAsync(LinkedDevices devices, CancellationToken ct)
    {
        _links = devices;
        return Task.CompletedTask;
    }

    // The image scale follows the optics, so setting a focal length really changes the field (for plate solving).
    private double _focalLength = focalLength;
    private double ArcsecPerPixel => 206.265 * pixelSize / _focalLength;

    public double? PixelSize => pixelSize;

    public (int Width, int Height)? SensorSize => (width, height);

    public Task SetOpticsAsync(Optics optics, CancellationToken ct)
    {
        if (optics.FocalLength > 0) _focalLength = optics.FocalLength;
        return Task.CompletedTask;
    }

    // Relative response of the R, G, B pixels to the sky (orange-ish light pollution) and a flat panel.
    // Made-up background stars stop at this brightness (electrons/s), which the catalog's magnitude scale
    // puts at magnitude 10.5: brighter stars are all real, named catalog stars.
    private const double ProceduralFluxLimit = 20_000;
    private const double ProceduralMagnitudeLimit = 10.5;

    private static readonly double[] SkyColor = [1.15, 1.0, 0.6];
    private static readonly double[] FlatColor = [0.8, 1.0, 0.7];
    /// <summary>Electrons per second per pixel from the simulated flat panel.</summary>
    public const double FlatPanelRate = 30_000;

    private const double CellDegrees = 0.25;
    private const int StarsPerCell = 14;
    private const double Ambient = 18;          // °C
    private const double MaxDelta = 35;          // cooler can reach ambient - 35 °C
    private const double CoolingRate = 0.5;      // °C per second
    private const int MaxBinning = 4;

    private readonly object _sync = new();
    private bool _coolerOn;
    private double _target = -10;
    private double _temperature = Ambient;
    private DateTime _lastThermalUpdate = DateTime.UtcNow;
    private double _gain = 100;
    private double _offset = 10;
    private int _binning = 1;
    private FrameType _frameType = FrameType.Light;
    private bool _dewHeater;

    public string Name => name;
    public bool IsConnected { get; private set; }
    public CameraState State { get; private set; } = CameraState.Idle;

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { IsConnected = false; return Task.CompletedTask; }

    public CameraControls Controls
    {
        get
        {
            lock (_sync)
            {
                UpdateTemperature();
                var goal = Goal();
                var power = _coolerOn
                    ? Math.Clamp((Ambient - _temperature) / MaxDelta * 100 + (Math.Abs(goal - _temperature) > 0.2 ? 15 : 0), 0, 100)
                    : 0;
                return new CameraControls(
                    new CoolerStatus(true, _coolerOn, Math.Round(_temperature, 1), _target, Math.Round(power)),
                    new NumberSetting(_gain, 0, 500, 1),
                    new NumberSetting(_offset, 0, 100, 1),
                    new NumberSetting(_binning, 1, MaxBinning, 1),
                    _frameType,
                    _dewHeater);
            }
        }
    }

    public Task ApplyAsync(CameraSettingsUpdate update, CancellationToken ct)
    {
        if (update.Gain is < 0 or > 500) throw new ArgumentException("Gain must be 0–500");
        if (update.Offset is < 0 or > 100) throw new ArgumentException("Offset must be 0–100");
        if (update.Binning is < 1 or > MaxBinning) throw new ArgumentException($"Binning must be 1–{MaxBinning}");
        if (update.TargetTemperature is < -40 or > 30) throw new ArgumentException("Target temperature must be -40–30 °C");

        lock (_sync)
        {
            UpdateTemperature();
            if (update.Gain is { } gain) _gain = gain;
            if (update.Offset is { } offset) _offset = offset;
            if (update.Binning is { } binning) _binning = binning;
            if (update.FrameType is { } frameType) _frameType = frameType;
            if (update.DewHeater is { } heater) _dewHeater = heater;
            if (update.TargetTemperature is { } target)
            {
                _target = target;
                _coolerOn = update.CoolerOn ?? true; // like real drivers, setting a target starts the cooler
            }
            else if (update.CoolerOn is { } on)
            {
                _coolerOn = on;
            }
        }
        return Task.CompletedTask;
    }

    public async Task<CapturedImage> ExposeAsync(ExposureRequest request, IProgress<ExposureProgress>? progress, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Camera is not connected");
        if (State is CameraState.Exposing or CameraState.Downloading) throw new InvalidOperationException("Camera is busy");

        var started = DateTime.UtcNow;
        var duration = request.DurationSeconds;
        try
        {
            State = CameraState.Exposing;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < duration)
            {
                progress?.Report(new ExposureProgress(clock.Elapsed.TotalSeconds, duration, State));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.2, Math.Max(0.01, duration - clock.Elapsed.TotalSeconds))), ct);
            }

            State = CameraState.Downloading;
            progress?.Report(new ExposureProgress(duration, duration, State));

            var center = pointing();
            var controls = Controls;
            var settings = new FrameSettings((int)controls.Binning!.Value, controls.FrameType!.Value,
                controls.Gain!.Value, controls.Offset!.Value, controls.Cooler!.Temperature ?? Ambient);
            var w = width / settings.Binning;
            var h = height / settings.Binning;
            // Binning mixes neighbouring colour pixels, so a binned frame is effectively mono (like real drivers).
            var bayer = color && settings.Binning == 1;
            // Stars are placed in J2000 (like the catalog and the plate solver) with an exact TAN projection.
            var j2000 = Precession.JNowToJ2000(center, started);
            // Turned with the rotator it sits on: the position angle of the frame's top, which the mount gives relative to
            // today's north (JNow). Near the pole, J2000's north points quite differently, so convert it too.
            var pa = ToJ2000PositionAngle(center, skyAngle?.Invoke() ?? 0, started);
            var pixels = await Task.Run(() => Render(j2000, duration, settings, w, h, bayer, pa), ct);

            List<FitsCard> colorCards = bayer
                ? [new("BAYERPAT", "RGGB", "Bayer color pattern"), new("XBAYROFF", 0), new("YBAYROFF", 0)]
                : [];
            var fits = Fits.WriteUInt16(w, h, pixels,
            [
                .. colorCards,
                .. linkedHeaders?.Invoke(_links) ?? [],
                new("EXPTIME", duration, "exposure time [s]"),
                new("DATE-OBS", started.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "UTC start"),
                new("INSTRUME", Name),
                new("IMAGETYP", settings.FrameType + " Frame"),
                new("XBINNING", settings.Binning),
                new("YBINNING", settings.Binning),
                new("GAIN", settings.Gain),
                new("OFFSET", settings.Offset),
                new("CCD-TEMP", settings.Temperature, "sensor temperature [C]"),
                new("PIXSCALE", ArcsecPerPixel * settings.Binning, "arcsec per pixel"),
                // Where this frame really points (J2000), so NOVA's simulated plate solver can "find" it.
                new("SIM-RA", j2000.RaHours * 15, "simulator: true centre RA [deg]"),
                new("SIM-DEC", j2000.DecDegrees, "simulator: true centre Dec [deg]"),
                new("SIM-PA", Math.Round(pa, 4), "simulator: true position angle of the top [deg]"),
            ]);

            State = CameraState.Idle;
            return new CapturedImage
            {
                Image = Fits.Read(fits),
                FitsBytes = fits,
                StartedUtc = started,
                DurationSeconds = duration,
            };
        }
        catch
        {
            State = CameraState.Idle;
            throw;
        }
    }

    private sealed record FrameSettings(int Binning, FrameType FrameType, double Gain, double Offset, double Temperature);

    private double Goal() => _coolerOn ? Math.Max(_target, Ambient - MaxDelta) : Ambient;

    private void UpdateTemperature()
    {
        var now = DateTime.UtcNow;
        var step = CoolingRate * (now - _lastThermalUpdate).TotalSeconds;
        _lastThermalUpdate = now;
        _temperature += Math.Clamp(Goal() - _temperature, -step, step);
    }

    /// <summary>A position angle at <paramref name="jnow"/> measured from today's north, measured from J2000's north instead.</summary>
    private static double ToJ2000PositionAngle(EquatorialCoordinates jnow, double positionAngle, DateTime utc)
    {
        const double rad = Math.PI / 180;
        const double distance = 0.05 * rad;
        var (ra1, dec1, pa) = (jnow.RaHours * 15 * rad, jnow.DecDegrees * rad, positionAngle * rad);
        var dec2 = Math.Asin(Math.Sin(dec1) * Math.Cos(distance) + Math.Cos(dec1) * Math.Sin(distance) * Math.Cos(pa));
        var ra2 = ra1 + Math.Atan2(Math.Sin(pa) * Math.Sin(distance) * Math.Cos(dec1), Math.Cos(distance) - Math.Sin(dec1) * Math.Sin(dec2));
        var from = Precession.JNowToJ2000(jnow, utc);
        var to = Precession.JNowToJ2000(new EquatorialCoordinates((ra2 / rad / 15 % 24 + 24) % 24, dec2 / rad), utc);
        var (d1, d2, da) = (from.DecDegrees * rad, to.DecDegrees * rad, (to.RaHours - from.RaHours) * 15 * rad);
        var j2000 = Math.Atan2(Math.Sin(da) * Math.Cos(d2), Math.Cos(d1) * Math.Sin(d2) - Math.Sin(d1) * Math.Cos(d2) * Math.Cos(da)) / rad;
        return (j2000 % 360 + 360) % 360;
    }

    /// <summary>How much the seeing wanders from frame to frame (arcseconds on top of 3"); 0 for steady seeing (tests).</summary>
    internal double SeeingVariation { get; set; } = 0.8;

    /// <summary>
    /// Hot pixels on the sensor, a quarter of them warm 2×2 clusters (which look like faint stars). They're dark
    /// current, so they show in darks as well, always in the same places. 0 = a perfect sensor.
    /// </summary>
    internal int HotPixels { get; set; }

    private (int W, int H, int Count, double[] Rates)? _hot;

    /// <summary>Electrons per second for each (binned) pixel from hot pixels: the same pattern every frame.</summary>
    private double[] HotRates(int w, int h)
    {
        if (_hot is { } cached && cached.W == w && cached.H == h && cached.Count == HotPixels) return cached.Rates;
        var random = new Random(4242);
        var rates = new double[w * h];
        for (var i = 0; i < HotPixels; i++)
        {
            var x = random.Next(1, w - 2);
            var y = random.Next(1, h - 2);
            var rate = 300 + random.NextDouble() * 3000;
            if (i % 4 == 0)
                foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) }) rates[(y + dy) * w + x + dx] += rate / 2;
            else
                rates[y * w + x] += rate;
        }
        _hot = (w, h, HotPixels, rates);
        return rates;
    }

    private ushort[] Render(EquatorialCoordinates center, double exposure, FrameSettings settings, int w, int h, bool bayer, double pa)
    {
        var random = new Random();
        var signal = new double[w * h];
        var scale = ArcsecPerPixel * settings.Binning;
        var binArea = settings.Binning * settings.Binning;

        if (settings.FrameType == FrameType.Light)
            AddStars(signal, center, exposure, scale, w, h, random, bayer, defocus?.Invoke() ?? 1, pa, SeeingVariation);

        // Electrons per (binned) pixel from sky, flat panel and thermal dark current.
        var sky = settings.FrameType switch
        {
            FrameType.Light => 40 * exposure * binArea,
            // A steady panel: longer exposures, brighter flats. With the simulated flat panel connected, only its light
            // (cover closed, lit, as bright as it's set; 128 matches a hand-held panel); otherwise one held up by hand.
            FrameType.Flat => FlatPanelRate * (panelGlow?.Invoke() is { } glow ? glow * 255 / 128 : 1) * exposure * binArea,
            _ => 0,
        };
        // Dark current doubles every ~6 °C.
        var dark = settings.FrameType == FrameType.Bias ? 0 : 0.1 * Math.Pow(2, (settings.Temperature - 20) / 6) * exposure * binArea;

        var hot = HotPixels > 0 && settings.FrameType != FrameType.Bias ? HotRates(w, h) : null;

        const double readNoise = 8;
        var aduPerElectron = settings.Gain / 100;
        var bias = 100 + settings.Offset * 10;
        var output = new ushort[w * h];
        var tint = settings.FrameType == FrameType.Flat ? FlatColor : SkyColor;
        for (var i = 0; i < output.Length; i++)
        {
            var electrons = (bayer ? sky * tint[Channel(i % w, i / w)] : sky) + dark + signal[i] + (hot is null ? 0 : hot[i] * exposure);
            var noise = Gaussian(random) * Math.Sqrt(electrons + readNoise * readNoise);
            output[i] = (ushort)Math.Clamp(bias + (electrons + noise) * aduPerElectron, 0, 65535);
        }
        return output;
    }

    /// <summary>RGGB: 0 = red, 1 = green, 2 = blue.</summary>
    private static int Channel(int x, int y) => (x & 1) + (y & 1);

    private static void AddStars(double[] signal, EquatorialCoordinates center, double exposure, double scale, int w, int h,
        Random random, bool bayer, double defocus, double pa, double seeingVariation)
    {
        var ra0 = center.RaHours * 15;
        var dec0 = center.DecDegrees;
        var cosDec0 = Math.Max(Math.Cos(dec0 * Math.PI / 180), 0.05);
        // Turned, the frame reaches as far as its half-diagonal in any direction.
        var halfDiagonal = Math.Sqrt(w * w + h * h) / 2 * scale / 3600;
        var halfH = (pa % 360 == 0 ? h / 2.0 * scale / 3600 : halfDiagonal) + CellDegrees;
        var halfW = Math.Min(180, ((pa % 360 == 0 ? w / 2.0 * scale / 3600 : halfDiagonal) + CellDegrees) / cosDec0);
        // Seeing ~3-4" varies frame to frame; defocus (from the focuser) widens every star.
        var sigma = (3.0 + random.NextDouble() * seeingVariation) / scale * defocus;
        var cellsAround = (int)(360 / CellDegrees);
        var wcs = Wcs.Rotated(center, scale, w, h, pa);

        // Real stars from the catalog (so annotations line up), then made-up faint ones to fill the field.
        // AddStar works in pixel-edge coordinates, hence the + 0.5.
        var radius = Math.Sqrt(w * w + h * h) / 2 * scale / 3600 + 0.05;
        foreach (var star in StarCatalog.Near(center, radius, faintestMagnitude: 11))
        {
            if (wcs.ToPixel(star.Position) is not { } at) continue;
            var (px, py) = at;
            var flux = Math.Min(5_000_000, ProceduralFluxLimit * Math.Pow(10, -0.4 * (star.Magnitude - ProceduralMagnitudeLimit)));
            AddStar(signal, w, h, px + 0.5, py + 0.5, flux * exposure, sigma, bayer ? [1.1, 1.0, 0.9] : [1, 1, 1], bayer);
        }

        for (var cy = (int)Math.Floor((dec0 - halfH) / CellDegrees); cy <= (int)Math.Floor((dec0 + halfH) / CellDegrees); cy++)
        for (var cx = (int)Math.Floor((ra0 - halfW) / CellDegrees); cx <= (int)Math.Floor((ra0 + halfW) / CellDegrees); cx++)
        {
            var wrappedCx = ((cx % cellsAround) + cellsAround) % cellsAround;
            // Deterministic per sky cell, so the same patch of sky always has the same stars.
            var cellRandom = new Random(wrappedCx * 100_003 + cy * 7_919);
            var cellDec = (cy + 0.5) * CellDegrees;
            var count = (int)(StarsPerCell * Math.Max(0.05, Math.Cos(cellDec * Math.PI / 180)));
            for (var i = 0; i < count; i++)
            {
                var starRa = (wrappedCx + cellRandom.NextDouble()) * CellDegrees;
                var starDec = (cy + cellRandom.NextDouble()) * CellDegrees;
                // Fainter than the catalog's real stars, so made-up stars never outshine named ones.
                var flux = Math.Min(ProceduralFluxLimit, 1_500 / Math.Pow(cellRandom.NextDouble() + 0.002, 1.6));
                // Star colour from cool (orange) to hot (blue-white).
                var warmth = cellRandom.NextDouble();
                double[] tint = bayer ? [0.6 + 0.7 * warmth, 1.0, 1.3 - 0.8 * warmth] : [1, 1, 1];

                if (wcs.ToPixel(new EquatorialCoordinates(starRa / 15 % 24, starDec)) is not { } at) continue;
                var (x, y) = at;
                AddStar(signal, w, h, x + 0.5, y + 0.5, flux * exposure, sigma, tint, bayer);
            }
        }
    }

    private static void AddStar(double[] signal, int w, int h, double cx, double cy, double flux, double sigma, double[] tint, bool bayer)
    {
        var radius = (int)Math.Ceiling(sigma * 5);
        if (cx < -radius || cy < -radius || cx > w + radius || cy > h + radius) return;

        var norm = flux / (2 * Math.PI * sigma * sigma);
        var twoSigma2 = 2 * sigma * sigma;
        for (var y = Math.Max(0, (int)cy - radius); y <= Math.Min(h - 1, (int)cy + radius); y++)
        for (var x = Math.Max(0, (int)cx - radius); x <= Math.Min(w - 1, (int)cx + radius); x++)
        {
            var dx = x + 0.5 - cx;
            var dy = y + 0.5 - cy;
            var weight = bayer ? tint[Channel(x, y)] : 1;
            signal[y * w + x] += weight * norm * Math.Exp(-(dx * dx + dy * dy) / twoSigma2);
        }
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
