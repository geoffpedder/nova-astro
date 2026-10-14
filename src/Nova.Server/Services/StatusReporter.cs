using Microsoft.AspNetCore.SignalR;
using Nova.Core.Equipment;
using Nova.Server.Hubs;

namespace Nova.Server.Services;

public sealed record CameraStatus(string Name, bool Connected, CameraState State, CameraControls? Controls);

/// <param name="RaHours">J2000 (converted from the mount's JNow).</param>
/// <param name="HourAngle">Hours from the meridian (negative: still rising in the east), when the site is known.</param>
public sealed record MountStatus(
    string Name, bool Connected, MountState State,
    double RaHours, double DecDegrees, string RaText, string DecText, string JNowRaText, string JNowDecText,
    PierSide? SideOfPier = null, double? HourAngle = null,
    IReadOnlyList<SlewRate>? SlewRates = null, string? SlewRate = null, IReadOnlyList<MotionDirection>? Moving = null);

public sealed record FocuserStatus(
    string Name, bool Connected, bool CanMoveAbsolute, int Position, int MinPosition, int MaxPosition,
    bool IsMoving, double? Temperature, IReadOnlyList<FocusPreset> Presets, FocusOffsetsInfo? Offsets = null);

public sealed record FilterWheelStatus(
    string Name, bool Connected, int Position, IReadOnlyList<string> Names, bool IsMoving);

public sealed record RotatorStatus(string Name, bool Connected, double Angle, bool IsMoving);

/// <param name="CoverClosed">Null when the panel has no cover.</param>
public sealed record FlatPanelStatus(
    string Name, bool Connected, bool HasLight, bool LightOn, NumberSetting? Brightness, bool HasCover, bool? CoverClosed, bool CoverMoving);

public sealed record AutofocusInfo(bool Running, AutofocusSettings Settings, AutofocusStatus? Last);

public sealed record StatusSnapshot(
    string Backend, bool ServerConnected, bool EquipmentConnected, bool HasDriverSettings, SimulatorStatus Simulators,
    IReadOnlyList<DeviceInfo> Devices, EquipmentSelection Selection,
    CameraStatus? Camera, MountStatus? Mount, FocuserStatus? Focuser, FilterWheelStatus? FilterWheel,
    RotatorStatus? Rotator, GuiderStatus Guider, IReadOnlyList<CameraOpticsInfo> Optics, LinkedDevices HeaderLinks, SiteStatus Site,
    SolverStatus PlateSolve, AutofocusInfo Autofocus, PolarAlignmentInfo PolarAlignment, StellariumLinkStatus? Stellarium, SequencerInfo Sequencer, PowerStatus Power, NetworkStatus Network, IReadOnlyList<HfrPoint> HfrHistory,
    ExposureStatus? Exposure, ImageInfo? LatestImage, IReadOnlyDictionary<string, string> Nicknames, FlatsInfo? Flats = null,
    ImageHistoryStatus? ImageHistory = null, FlatPanelStatus? FlatPanel = null, string? Version = null,
    StorageStatus? Storage = null, HotspotStatus? Hotspot = null);

/// <summary>
/// Builds the full state snapshot and pushes it to every browser once a second.
/// A periodic full snapshot (rather than only deltas) means a client that reconnects is
/// back in sync within a second without any extra logic.
/// </summary>
public sealed class StatusReporter(
    EquipmentService equipment, ImagingService imaging, GuiderService guider, OpticsService optics,
    CameraLinkService links, SimulatorService simulators, SiteService sites, PlateSolveService solver,
    AutofocusService autofocus, FocusPresetService focusPresets, PolarAlignmentService polar, StellariumTelescopeServer stellarium,
    SequencerService sequencer, MountMotionService motion, PowerService power, NetworkService network,
    NicknameService nicknames, FlatsService flats, IHubContext<NovaHub> hub, ILogger<StatusReporter> logger,
    FocusOffsetService? offsets = null, StorageService? storage = null, HotspotService? hotspot = null)
    : BackgroundService
{
    public StatusSnapshot GetSnapshot()
    {
        var provider = equipment.Provider;
        var camera = equipment.Camera;
        var mount = equipment.Mount;
        var focuser = equipment.Focuser;
        var wheel = equipment.FilterWheel;
        var rotator = equipment.Rotator;
        var coords = mount?.Coordinates ?? default;
        var j2000 = Nova.Core.Astronomy.Precession.JNowToJ2000(coords, DateTime.UtcNow);

        return new StatusSnapshot(
            provider.Name,
            provider.IsConnected,
            equipment.EquipmentConnected,
            provider.Settings is not null,
            simulators.Status,
            equipment.VisibleDevices,
            equipment.Selection,
            camera is null ? null : new CameraStatus(camera.Name, camera.IsConnected, camera.State,
                camera.IsConnected ? camera.Controls : null),
            mount is null ? null : new MountStatus(mount.Name, mount.IsConnected, mount.State,
                j2000.RaHours, j2000.DecDegrees, j2000.RaText, j2000.DecText, coords.RaText, coords.DecText,
                mount.IsConnected ? mount.SideOfPier : null,
                mount.IsConnected && (mount.Location ?? sites.ActiveSite?.Location) is { } site
                    ? Math.Round(Nova.Core.Astronomy.Horizon.HourAngle(coords, DateTime.UtcNow, site.Longitude), 4)
                    : null,
                mount.IsConnected ? mount.SlewRates : null,
                mount.IsConnected ? motion.SpeedFor(mount) : null,
                motion.Active),
            focuser is null ? null : new FocuserStatus(focuser.Name, focuser.IsConnected, focuser.CanMoveAbsolute,
                focuser.Position, focuser.MinPosition, focuser.MaxPosition, focuser.IsMoving, focuser.Temperature,
                focusPresets.For(focuser.Name), offsets?.For(focuser.Name)),
            wheel is null ? null : new FilterWheelStatus(wheel.Name, wheel.IsConnected, wheel.Position, wheel.Names, wheel.IsMoving),
            rotator is null ? null : new RotatorStatus(rotator.Name, rotator.IsConnected, Math.Round(rotator.Angle, 2), rotator.IsMoving),
            guider.Status,
            optics.All,
            links.Current,
            sites.Status,
            solver.Status,
            new AutofocusInfo(autofocus.Running, autofocus.Settings, autofocus.Status),
            polar.Info,
            stellarium.Status,
            sequencer.Info,
            power.Status,
            network.Status,
            imaging.HfrHistory,
            imaging.Exposure,
            imaging.LatestImage,
            nicknames.All,
            flats.Info,
            imaging.HistoryStatus is var history
                ? history with { Images = history.Images.Select(i => i with { Solve = solver.SolveOf(i.Info.Id) }).ToList() }
                : null,
            equipment.FlatPanel is { } panel
                ? new FlatPanelStatus(panel.Name, panel.IsConnected, panel.IsConnected && panel.HasLight, panel.LightOn,
                    panel.IsConnected ? panel.Brightness : null, panel.IsConnected && panel.HasCover, panel.CoverClosed, panel.CoverMoving)
                : null,
            AppVersion,
            storage?.Status,
            hotspot?.Status);
    }

    /// <summary>NOVA's version ("0.2.0"; "0.0.0-dev" for a local build), without the build's commit hash.</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()?
             .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
             .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0-dev")
        .Split('+')[0];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.All.SendAsync(NovaHub.Events.Status, GetSnapshot(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Status broadcast failed");
            }
        }
    }
}
