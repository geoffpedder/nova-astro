using Nova.Core.Astronomy;
using Nova.Core.Imaging;

namespace Nova.Core.Equipment;

public enum DeviceKind { Camera, Mount, Focuser, FilterWheel, Rotator, Guider, Dome, Weather, FlatPanel, Other }

public sealed record DeviceInfo(string Name, IReadOnlyList<DeviceKind> Kinds, bool IsConnected);

/// <summary>
/// A source of devices: the built-in simulator, an INDI server, and later Alpaca.
/// The rest of the app only talks to these interfaces, never to a protocol directly.
/// </summary>
public interface IEquipmentProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>The named device if it exists and is a camera, otherwise null.</summary>
    ICamera? GetCamera(string name);
    /// <summary>The named device if it exists and is a mount, otherwise null.</summary>
    IMount? GetMount(string name);
    /// <summary>The named device if it exists and is a focuser, otherwise null.</summary>
    IFocuser? GetFocuser(string name);
    IFilterWheel? GetFilterWheel(string name);
    IRotator? GetRotator(string name);
    /// <summary>The named device if it's a flat panel (a light box, a flip-flat with a cover).</summary>
    IFlatPanel? GetFlatPanel(string name) => null;

    /// <summary>Raw access to every driver setting, or null if the provider has none (the simulator).</summary>
    IDeviceSettings? Settings { get; }

    /// <summary>Starting and stopping drivers, or null if the provider has no drivers (the simulator).</summary>
    IDriverControl? Drivers { get; }

    /// <summary>Raised when devices appear, disappear or change connection state.</summary>
    event EventHandler? Changed;

    /// <summary>Raised for driver messages worth showing to the user.</summary>
    event EventHandler<string>? Message;

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public interface IDevice
{
    string Name { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public enum CameraState { Idle, Exposing, Downloading, Error }

public sealed record ExposureRequest(double DurationSeconds);

public sealed record ExposureProgress(double ElapsedSeconds, double TotalSeconds, CameraState State);

public sealed class CapturedImage
{
    public required RawImage Image { get; init; }
    /// <summary>FITS file exactly as the driver produced it, so driver headers are kept.</summary>
    public required byte[] FitsBytes { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required double DurationSeconds { get; init; }
}

public enum FrameType { Light, Bias, Dark, Flat }

/// <summary>A numeric setting with its allowed range.</summary>
public sealed record NumberSetting(double Value, double Min, double Max, double Step);

/// <summary>Temperatures in °C. <c>CanCool</c> is false for uncooled cameras that only report temperature.</summary>
public sealed record CoolerStatus(bool CanCool, bool On, double? Temperature, double? TargetTemperature, double? PowerPercent);

/// <summary>Snapshot of the common camera settings. Null members are not supported by this camera.</summary>
public sealed record CameraControls(
    CoolerStatus? Cooler,
    NumberSetting? Gain,
    NumberSetting? Offset,
    NumberSetting? Binning,
    FrameType? FrameType);

/// <summary>Settings to change; null members are left as they are.</summary>
public sealed record CameraSettingsUpdate(
    bool? CoolerOn = null,
    double? TargetTemperature = null,
    double? Gain = null,
    double? Offset = null,
    int? Binning = null,
    FrameType? FrameType = null);

/// <summary>Telescope optics in front of a camera, in millimetres.</summary>
public sealed record Optics(double FocalLength, double Aperture)
{
    [System.Text.Json.Serialization.JsonIgnore] // derived, so not saved
    public double FocalRatio => FocalLength / Aperture;

    /// <summary>Arcseconds per (unbinned) pixel for a pixel size in micrometres.</summary>
    public double ImageScale(double pixelSizeMicrons) => 206.265 * pixelSizeMicrons / FocalLength;
}

/// <summary>
/// The devices a camera should read from when writing FITS headers (INDI's ACTIVE_DEVICES):
/// the mount gives coordinates, pier side and airmass, the wheel FILTER, the focuser FOCUSPOS/FOCUSTEM,
/// the rotator ROTATANG. Null means none.
/// </summary>
public sealed record LinkedDevices(string? Mount, string? FilterWheel, string? Focuser, string? Rotator);

public interface ICamera : IDevice
{
    CameraState State { get; }
    CameraControls Controls { get; }
    /// <summary>Pixel size in micrometres, if the driver reports it.</summary>
    double? PixelSize { get; }

    /// <summary>Full sensor size in (unbinned) pixels, if the driver reports it.</summary>
    (int Width, int Height)? SensorSize => null;

    /// <summary>Tells the driver the optics, so FITS headers carry FOCALLEN/APTDIA (INDI's SCOPE_INFO).</summary>
    Task SetOpticsAsync(Optics optics, CancellationToken ct);

    /// <summary>Tells the driver which mount, filter wheel, focuser and rotator to record in FITS headers.</summary>
    Task LinkDevicesAsync(LinkedDevices devices, CancellationToken ct);

    /// <summary>Applies the given settings. Throws ArgumentException for values the camera can't take.</summary>
    Task ApplyAsync(CameraSettingsUpdate update, CancellationToken ct);

    /// <summary>Runs one exposure. Cancelling the token aborts the exposure on the device.</summary>
    Task<CapturedImage> ExposeAsync(ExposureRequest request, IProgress<ExposureProgress>? progress, CancellationToken ct);
}

public enum MountState { Idle, Tracking, Slewing, Parking, Parked, Error }

/// <summary>
/// Which side of the pier the telescope is on (INDI's convention). Normally West while looking east of the
/// meridian and East while looking west; still West after crossing the meridian means a flip is due.
/// </summary>
public enum PierSide { East, West }

/// <summary>Directions for moving a mount by hand (on the sky: north/south in Dec, east/west in RA).</summary>
public enum MotionDirection { North, South, East, West }

/// <summary>A manual-motion speed the mount offers, e.g. Name "4", Label "720x".</summary>
public sealed record SlewRate(string Name, string Label);

/// <summary>An observing location: latitude (+N), longitude (+E, -180..180) and elevation in metres.</summary>
public sealed record GeoLocation(double Latitude, double Longitude, double Elevation)
{
    /// <summary>Whether two locations are within <paramref name="degrees"/> of each other in latitude and longitude.</summary>
    public bool IsNear(GeoLocation other, double degrees = 0.05)
    {
        var dLon = Math.Abs(Longitude - other.Longitude) % 360;
        return Math.Abs(Latitude - other.Latitude) <= degrees && Math.Min(dLon, 360 - dLon) <= degrees;
    }
}

public interface IMount : IDevice
{
    MountState State { get; }
    EquatorialCoordinates Coordinates { get; }

    /// <summary>Side of the pier, if the mount reports it (German equatorial mounts do).</summary>
    PierSide? SideOfPier { get; }

    /// <summary>Where the mount thinks it is, if it reports a location.</summary>
    GeoLocation? Location { get; }

    /// <summary>Tells the mount where it is and what time it is (needed for alt/az, meridian and parking).</summary>
    Task SetSiteAsync(GeoLocation location, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// The mount's own clock (UTC), if it reports one. Mounts without a clock battery (like ZWO's) restart at
    /// 2000-01-01 when powered on, and a wrong clock makes every GoTo go wrong or be refused.
    /// </summary>
    DateTime? ClockUtc { get; }

    /// <summary>Sets the mount's clock.</summary>
    Task SetClockAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Slews and completes when the mount reports the slew finished (then tracks).</summary>
    Task SlewAsync(EquatorialCoordinates target, CancellationToken ct);

    /// <summary>Speeds for moving by hand, slowest first as the driver lists them; empty if it can't.</summary>
    IReadOnlyList<SlewRate> SlewRates { get; }
    string? CurrentSlewRate { get; }
    Task SetSlewRateAsync(string name, CancellationToken ct);

    /// <summary>Starts moving in a direction at the current slew rate, until <see cref="StopMotionAsync"/>.</summary>
    Task StartMotionAsync(MotionDirection direction, CancellationToken ct);

    /// <summary>Stops moving along <paramref name="direction"/>'s axis, or along both axes when null.</summary>
    Task StopMotionAsync(MotionDirection? direction, CancellationToken ct);

    /// <summary>Whether <see cref="MoveRaAxisAsync"/> works: the RA axis can be driven directly.</summary>
    bool CanMoveRaAxis { get; }

    /// <summary>
    /// Turns the RA axis by about <paramref name="degrees"/> (positive = west, i.e. later hour angle) by driving
    /// the axis rather than a GoTo, so the mount never flips sides and its pointing model isn't involved —
    /// what polar alignment needs. Tracking carries on afterwards. Completes when the axis has stopped.
    /// </summary>
    Task MoveRaAxisAsync(double degrees, CancellationToken ct);

    /// <summary>
    /// Sets the mount's own meridian behaviour for flips NOVA makes: keep tracking past the meridian (at least
    /// <paramref name="limitDegrees"/>) and don't flip by itself. Returns what was changed, or null if nothing was
    /// (or the mount has no such settings).
    /// </summary>
    Task<string?> PrepareForMeridianFlipsAsync(double limitDegrees, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Turns sidereal tracking on (e.g. after the mount stopped by itself at the meridian).</summary>
    Task StartTrackingAsync(CancellationToken ct) => Task.CompletedTask;
    /// <summary>
    /// Tells the mount it's actually pointing at <paramref name="actual"/> (JNow), e.g. from a plate solve,
    /// correcting its pointing model so later slews land where they should.
    /// </summary>
    Task SyncAsync(EquatorialCoordinates actual, CancellationToken ct);
    Task AbortAsync(CancellationToken ct);
    Task ParkAsync(CancellationToken ct);
    Task UnparkAsync(CancellationToken ct);
}

/// <summary>
/// A motorised focuser. Positions are in steps; "in" means decreasing position, "out" increasing,
/// following INDI's convention.
/// </summary>
public interface IFocuser : IDevice
{
    /// <summary>False for relative-only focusers, which can step in/out but don't report a position.</summary>
    bool CanMoveAbsolute { get; }
    int Position { get; }
    int MinPosition { get; }
    int MaxPosition { get; }
    bool IsMoving { get; }
    /// <summary>°C, if the focuser has a temperature probe (the ZWO EAF does).</summary>
    double? Temperature { get; }

    /// <summary>Moves to an absolute position and completes when the focuser stops.</summary>
    Task MoveToAsync(int position, CancellationToken ct);
    /// <summary>Moves by a signed number of steps (negative = in) and completes when the focuser stops.</summary>
    Task MoveRelativeAsync(int steps, CancellationToken ct);
    Task HaltAsync(CancellationToken ct);
}

public interface IFilterWheel : IDevice
{
    /// <summary>Current slot, 1-based as in INDI and on the wheel itself.</summary>
    int Position { get; }
    int SlotCount { get; }
    /// <summary>Filter names by slot (index 0 = slot 1). Empty names fall back to "Slot n".</summary>
    IReadOnlyList<string> Names { get; }
    bool IsMoving { get; }
    Task MoveToAsync(int slot, CancellationToken ct);
}

/// <summary>A flat panel: a light (maybe dimmable) and, on flip-flats, a cover over the telescope.</summary>
public interface IFlatPanel : IDevice
{
    bool HasLight { get; }
    bool LightOn { get; }
    /// <summary>The light's brightness and its range, if it can be set.</summary>
    NumberSetting? Brightness { get; }
    bool HasCover { get; }
    /// <summary>Whether the cover is closed over the telescope; null without a cover.</summary>
    bool? CoverClosed { get; }
    bool CoverMoving { get; }
    /// <summary>Switches the light on or off, setting its brightness first when given.</summary>
    Task SetLightAsync(bool on, double? brightness, CancellationToken ct);
    /// <summary>Closes (parks) or opens the cover, and waits for it.</summary>
    Task SetCoverAsync(bool closed, CancellationToken ct);
}

public interface IRotator : IDevice
{
    /// <summary>Mechanical angle in degrees, 0–360.</summary>
    double Angle { get; }
    bool IsMoving { get; }
    Task MoveToAsync(double angle, CancellationToken ct);
    Task HaltAsync(CancellationToken ct);
}
