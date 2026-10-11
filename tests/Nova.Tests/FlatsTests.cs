using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class FlatsTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));
    private const double Longitude = -0.1;

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private sealed record Rig(EquipmentService Equipment, FlatsService Flats, PlateSolveService Solver, SequencerService Sequencer, string Images);

    /// <param name="panel">Use the simulator's flat panel; otherwise it's disconnected and flats need a panel held up by hand.</param>
    private async Task<Rig> SetUpAsync(bool panel = false)
    {
        var images = Path.Combine(_config, "images");
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = images, SolveExposureSeconds = 0, Phd2AutoConnect = false,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options) { UseRotator = false }; // turned by hand
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None);
        var flats = new FlatsService(equipment, imaging, log, options);
        // Quick flats for the tests: dim (short exposures) and few.
        flats.UpdateSettings(new FlatsSettings(TargetPercent: 20, Count: 2, UsePanel: panel));
        var sequencer = new SequencerService(equipment, imaging, solver, new AutofocusService(equipment, imaging, log, options),
            new GuiderService(options, log), sites, log, options, flats);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.SetSiteAsync(new GeoLocation(51.5, Longitude, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        await equipment.Mount.UnparkAsync(CancellationToken.None);
        if (!panel) await equipment.FlatPanel!.DisconnectAsync(CancellationToken.None);
        return new Rig(equipment, flats, solver, sequencer, images);
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 60)
    {
        for (var i = 0; i < seconds * 20 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static List<string> Saved(string images, string pattern) =>
        Directory.Exists(images) ? Directory.GetFiles(images, pattern, SearchOption.AllDirectories).ToList() : [];

    [Fact]
    public async Task FindsTheExposureTakesFlatsAndDarkFlats()
    {
        var rig = await SetUpAsync();
        Assert.True(rig.Flats.Start(new FlatsRequest("M 31"), out var error), error);

        // It waits for the panel before taking anything.
        await WaitFor(() => rig.Flats.Status is { Ask: "PanelOn" }, "the request for the panel");
        Assert.Empty(rig.Flats.Status!.Tests);
        Assert.True(rig.Flats.Continue());

        await WaitFor(() => rig.Flats.Status is { Ask: "Cover" }, "the request to cover the scope");
        var status = rig.Flats.Status!;
        var target = 0.2 * 65535;
        Assert.InRange(status.Tests[^1].Median, target * 0.95, target * 1.05);
        Assert.Equal(2, status.Flats);
        // About 13,000 ADU from a 30,000 ADU/s panel (less through the red and blue pixels): about half a second.
        Assert.InRange(status.Exposure!.Value, 0.35, 0.75);
        Assert.True(rig.Flats.Continue());

        await WaitFor(() => rig.Flats.Status is { Running: false }, "the end");
        status = rig.Flats.Status!;
        Assert.Equal("Done", status.Phase);
        Assert.Equal(2, status.DarkFlats);
        var flats = Saved(rig.Images, "*_Flat_*.fits");
        var darks = Saved(rig.Images, "*_Dark_*.fits");
        Assert.Equal(2, flats.Count);
        Assert.Equal(2, darks.Count);
        Assert.All(flats.Concat(darks), f => Assert.EndsWith(Path.Combine("Flats", "M 31"), Path.GetDirectoryName(f)));
        // Back on light frames for imaging.
        Assert.Equal(FrameType.Light, rig.Equipment.Camera!.Controls.FrameType);

        // Next time it starts from the exposure it found: one test frame is enough.
        Assert.True(rig.Flats.Start(new FlatsRequest("again", Settings: rig.Flats.Settings with { DarkFlats = false }), out error), error);
        await WaitFor(() => rig.Flats.Status is { Ask: "PanelOn" }, "the panel again");
        rig.Flats.Continue();
        await WaitFor(() => rig.Flats.Status is { Running: false }, "the second run");
        Assert.Single(rig.Flats.Status!.Tests);
    }

    [Fact]
    public async Task AsksForADimmerPanelWhenEvenTheShortestExposureIsTooBright()
    {
        var rig = await SetUpAsync();
        var bright = rig.Flats.Settings with { MinExposure = 2, DarkFlats = false };
        Assert.True(rig.Flats.Start(new FlatsRequest("x", Settings: bright), out var error), error);
        await WaitFor(() => rig.Flats.Status is { Ask: "PanelOn" }, "the panel");
        rig.Flats.Continue();
        await WaitFor(() => rig.Flats.Status is { Ask: "Dimmer" }, "the request to dim the panel");
        Assert.Contains("Too bright", rig.Flats.Status!.Message);
        Assert.True(rig.Flats.Skip());
        await WaitFor(() => rig.Flats.Status is { Running: false }, "the end");
        Assert.Equal("Skipped", rig.Flats.Status!.Phase);
        Assert.Empty(Saved(rig.Images, "*.fits"));
    }

    /// <summary>A target an hour east of the meridian, high at Dec +40°.</summary>
    private static SequenceTarget Up(string name, double? rotation, double hoursEast = 1)
    {
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, Longitude) / 15;
        var j2000 = Precession.JNowToJ2000(new EquatorialCoordinates((lst + hoursEast + 24) % 24, 40), DateTime.UtcNow);
        return new SequenceTarget(name, j2000.RaHours.ToString("0.####"), j2000.DecDegrees.ToString("0.####"), rotation);
    }

    [Fact]
    public async Task ASequenceTakesFlatsBeforeTheCameraIsTurnedAndAtTheEnd()
    {
        var rig = await SetUpAsync();
        SequenceTargetPlan Plan(SequenceTarget t) => new(t, [new SequenceRow(null, 0.2, 1)]);
        var night = new Sequence("Night", null,
            new SequenceStart(CoolCamera: false, CenterTarget: true, Autofocus: false, StartGuiding: false), [],
            Dither: new SequenceDither(EveryFrames: 0), Autofocus: new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null),
            MeridianFlip: new SequenceMeridianFlip(Enabled: false),
            // The simulator's camera is at 0°: A needs no turn, B needs one (so flats come first), C is at B's angle.
            Targets: [Plan(Up("A", 0)), Plan(Up("B", 40, 1.2)), Plan(Up("C", 40.5, 1.4))],
            Flats: new SequenceFlats(Enabled: true));
        Assert.True(rig.Sequencer.Start(night, out var error), error);

        // After A, before B's rotation: flats for A.
        await WaitFor(() => rig.Flats.Status is { Ask: "PanelOn" }, "flats after A");
        Assert.Equal("Flats", rig.Sequencer.Status!.Step);
        Assert.Equal("A (0°)", rig.Flats.Status!.Name);
        rig.Flats.Continue();
        await WaitFor(() => rig.Flats.Status is { Ask: "Cover" }, "dark flats after A");
        rig.Flats.Skip(); // no dark flats this time

        // B asks for the turn (the simulator can't turn: keep it). C is within a degree of B, so no flats before it
        // (its own check asks again only because the simulator's camera never turned).
        await WaitFor(() => rig.Solver.Rotation is { Waiting: true, PlannedAngle: 40 }, "B's rotation");
        rig.Solver.KeepRotation();
        await WaitFor(() => rig.Solver.Rotation is { Waiting: true, PlannedAngle: 40.5 }, "C's rotation");
        Assert.Equal(2, rig.Sequencer.Status!.FramesDone);
        rig.Solver.KeepRotation();
        await WaitFor(() => rig.Flats.Status is { Ask: "PanelOn", Name: not "A (0°)" }, "flats at the end");
        Assert.Equal("B + C (40°)", rig.Flats.Status!.Name);
        Assert.Equal(3, rig.Sequencer.Status!.FramesDone);
        rig.Flats.Continue();
        await WaitFor(() => rig.Flats.Status is { Ask: "Cover" }, "dark flats at the end");
        rig.Flats.Continue();

        await WaitFor(() => !rig.Sequencer.Running, "the end of the sequence");
        Assert.Equal("Done", rig.Sequencer.Status!.State);
        Assert.Equal(4, Saved(rig.Images, "*_Flat_*.fits").Count);
        Assert.Equal(2, Saved(rig.Images, "*_Dark_*.fits").Count);
        Assert.Equal(2, Saved(Path.Combine(rig.Images), "*.fits").Count(f => f.Contains(Path.Combine("Flats", "A (0°)"))));
    }

    [Fact]
    public async Task WithAFlatPanelNobodyIsAsked()
    {
        var rig = await SetUpAsync(panel: true);
        var panel = rig.Equipment.FlatPanel!;
        Assert.True(panel is { IsConnected: true, CoverClosed: false, LightOn: false });

        Assert.True(rig.Flats.Start(new FlatsRequest("M 31"), out var error), error);
        for (var i = 0; i < 600 && rig.Flats.Status is not { Running: false }; i++)
        {
            Assert.Null(rig.Flats.Status?.Ask); // never waits for anyone
            await Task.Delay(50);
        }
        var status = rig.Flats.Status!;
        Assert.Equal("Done", status.Phase);
        Assert.Equal("Simulated Flat Panel", status.Panel);
        Assert.Equal(2, status.Flats);
        Assert.Equal(2, status.DarkFlats);
        Assert.InRange(status.Tests[^1].Median, 0.95 * 0.2 * 65535, 1.05 * 0.2 * 65535);
        // Afterwards: light off, cover open again as it was (the cover takes a moment to swing back).
        for (var i = 0; i < 100 && panel is not { CoverClosed: false, CoverMoving: false }; i++) await Task.Delay(50);
        Assert.True(panel is { LightOn: false, CoverClosed: false });
        Assert.Equal(2, Saved(rig.Images, "*_Dark_*.fits").Count);
    }

    [Fact]
    public async Task ATooBrightPanelIsDimmedRatherThanAskingAnyone()
    {
        var rig = await SetUpAsync(panel: true);
        var panel = rig.Equipment.FlatPanel!;
        await panel.SetLightAsync(false, 255, CancellationToken.None); // full brightness
        // Short exposures only from 2 s up: at full brightness the panel is far too bright for that.
        var settings = rig.Flats.Settings with { MinExposure = 2, MaxExposure = 30, DarkFlats = false };
        Assert.True(rig.Flats.Start(new FlatsRequest("x", Settings: settings), out var error), error);
        for (var i = 0; i < 1200 && rig.Flats.Status is not { Running: false }; i++)
        {
            Assert.Null(rig.Flats.Status?.Ask);
            await Task.Delay(50);
        }
        var status = rig.Flats.Status!;
        Assert.Equal("Done", status.Phase);
        Assert.True(status.PanelBrightness < 255, $"brightness {status.PanelBrightness}");
        Assert.InRange(status.Exposure!.Value, 2, 30);
    }
}

