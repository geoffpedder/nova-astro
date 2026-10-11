using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class MultiTargetTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));
    private const double Longitude = -0.1;

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(SequencerService Sequencer, string Images)> SetUpAsync()
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
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        // Sun protection off: whether a test target is near the Sun depends on the time of day the tests run.
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None);
        var sequencer = new SequencerService(equipment, imaging, solver, new AutofocusService(equipment, imaging, log, options),
            new GuiderService(options, log), sites, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.SetSiteAsync(new GeoLocation(51.5, Longitude, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        await equipment.Mount.UnparkAsync(CancellationToken.None);
        return (sequencer, images);
    }

    /// <summary>A target the given number of hours east of the meridian (high in the sky at Dec +40°).</summary>
    private static SequenceTarget Up(string name, double hoursEast = 1)
    {
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, Longitude) / 15;
        var j2000 = Precession.JNowToJ2000(new EquatorialCoordinates((lst + hoursEast + 24) % 24, 40), DateTime.UtcNow);
        return new SequenceTarget(name, j2000.RaHours.ToString("0.####"), j2000.DecDegrees.ToString("0.####"));
    }

    private static SequenceTargetPlan Plan(SequenceTarget target, int frames, double exposure = 0.2, string? startAfter = null,
        string? stopAt = null, double? minAltitude = null) =>
        new(target, [new SequenceRow("L", exposure, frames)], StartAfter: startAfter, StopAt: stopAt, MinAltitude: minAltitude);

    private static Sequence Night(params SequenceTargetPlan[] targets) => new(
        "Night", null, new SequenceStart(CoolCamera: false, CenterTarget: true, Autofocus: false, StartGuiding: false), [],
        Dither: new SequenceDither(EveryFrames: 0), Autofocus: new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null),
        MeridianFlip: new SequenceMeridianFlip(Enabled: false), Targets: targets.ToList());

    private static async Task<SequencerStatus> WaitForEnd(SequencerService sequencer, Action<SequencerStatus>? watch = null, int seconds = 60)
    {
        for (var i = 0; i < seconds * 20 && sequencer.Running; i++)
        {
            if (sequencer.Status is { } s) watch?.Invoke(s);
            await Task.Delay(50);
        }
        Assert.False(sequencer.Running, "the sequence didn't finish");
        return sequencer.Status!;
    }

    private static string Clock(TimeSpan fromNow) => (DateTime.Now + fromNow).ToString("HH:mm");

    [Fact]
    public async Task RunsTheTargetsInTurnEachInItsOwnFolder()
    {
        var (sequencer, images) = await SetUpAsync();
        Assert.True(sequencer.Start(Night(Plan(Up("M 31"), 2), Plan(Up("M 33", 2), 3)), out var error), error);

        var status = await WaitForEnd(sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(5, status.FramesDone);
        Assert.Equal(["Done", "Done"], status.Targets!.Select(t => t.State));
        var folders = Directory.GetFiles(images, "*.fits", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileName(Path.GetDirectoryName(f))).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(2, folders["M 31"]);
        Assert.Equal(3, folders["M 33"]);
        // Numbered within each target: M 33's last frame is its 3rd.
        Assert.Contains(Directory.GetFiles(Path.Combine(Directory.GetDirectories(images).Single(), "M 33")), f => f.EndsWith("_0003.fits"));
    }

    [Fact]
    public async Task SkipsTargetsWhoseWindowHasPassedOrThatNeverRise()
    {
        var (sequencer, _) = await SetUpAsync();
        var neverUp = new SequenceTarget("Southern", "12", "-80"); // never rises at 51.5°N
        var night = Night(
            Plan(Up("Late"), 2, stopAt: Clock(TimeSpan.FromMinutes(-5))),
            Plan(neverUp, 2),
            Plan(Up("Fine"), 2));
        Assert.True(sequencer.Start(night, out var error), error);

        var status = await WaitForEnd(sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(["Skipped", "Skipped", "Done"], status.Targets!.Select(t => t.State));
        Assert.Contains("already passed", status.Targets[0].Note);
        Assert.Contains("doesn't get above 0°", status.Targets[1].Note);
        Assert.Equal(2, status.FramesDone);
    }

    [Fact]
    public async Task WaitsForTheStartTimeAndCanBeSkippedMeanwhile()
    {
        var (sequencer, _) = await SetUpAsync();
        Assert.True(sequencer.Start(Night(Plan(Up("Later"), 2, startAfter: Clock(TimeSpan.FromMinutes(30))), Plan(Up("Now"), 1)), out var error), error);

        for (var i = 0; i < 200 && sequencer.Status?.Step != "Waiting"; i++) await Task.Delay(50);
        Assert.Equal("Waiting", sequencer.Status!.Step);
        Assert.Contains("start time", sequencer.Status.Message);
        sequencer.SkipTarget();

        var status = await WaitForEnd(sequencer);
        Assert.Equal(["Skipped", "Done"], status.Targets!.Select(t => t.State));
        Assert.Equal("Skipped by you", status.Targets[0].Note);
    }

    [Fact]
    public async Task MovesOnWhenTheStopTimeComes()
    {
        var (sequencer, _) = await SetUpAsync();
        // Stop times are minutes ("HH:mm"): pick the next minute boundary at least 25 s away (so up to 85 s), which
        // leaves time for the slew and a few frames even on a busy machine.
        var stop = DateTime.Now.AddSeconds(25);
        stop = stop.AddSeconds(60 - stop.Second).AddMilliseconds(-stop.Millisecond);
        Assert.True(sequencer.Start(Night(Plan(Up("Short"), 500, exposure: 0.5, stopAt: stop.ToString("HH:mm")), Plan(Up("Next"), 1)),
            out var error), error);

        var status = await WaitForEnd(sequencer, seconds: 120);
        Assert.Equal(["Stopped", "Done"], status.Targets!.Select(t => t.State));
        Assert.Contains("stop time", status.Targets[0].Note);
        Assert.True(status.Targets[0].Rows[0].Done is > 0 and < 500);
        Assert.True(DateTime.Now >= stop.AddSeconds(-2), "it shouldn't have moved on before the stop time");
    }

    [Fact]
    public void ValidatesEachTarget()
    {
        Assert.Contains("needs coordinates", Night(Plan(Up("A"), 1), new SequenceTargetPlan(null, [new SequenceRow("L", 1, 1)])).Validate());
        Assert.Contains("B: add at least one row", Night(Plan(Up("A"), 1), new SequenceTargetPlan(Up("B"), [])).Validate());
        Assert.Contains("a time like 22:30", Night(Plan(Up("A"), 1, startAfter: "late")).Validate());
        Assert.Null(Night(Plan(Up("A"), 1, startAfter: "22:30", stopAt: "1:15", minAltitude: 25)).Validate());
    }

    [Fact]
    public void ClockTimesMeanTonight()
    {
        var evening = new DateTime(2026, 10, 4, 21, 0, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2026, 10, 5, 1, 30, 0, DateTimeKind.Local), SequenceTargetPlan.ClockToUtc(TimeSpan.FromHours(1.5), evening).ToLocalTime());
        Assert.Equal(new DateTime(2026, 10, 4, 20, 0, 0, DateTimeKind.Local), SequenceTargetPlan.ClockToUtc(TimeSpan.FromHours(20), evening).ToLocalTime());
        var small = new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Local);
        Assert.Equal(new DateTime(2026, 10, 4, 22, 0, 0, DateTimeKind.Local), SequenceTargetPlan.ClockToUtc(TimeSpan.FromHours(22), small).ToLocalTime());
    }
}
