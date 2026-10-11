using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class HourAngleTests
{
    [Fact]
    public void NegativeBeforeTheMeridianPositiveAfterAndWrapped()
    {
        var utc = new DateTime(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc);
        var lst = Horizon.LocalSiderealDegrees(utc, -0.1) / 15;
        Assert.Equal(-1, Horizon.HourAngle(new EquatorialCoordinates((lst + 1) % 24, 30), utc, -0.1), 6);
        Assert.Equal(2, Horizon.HourAngle(new EquatorialCoordinates((lst - 2 + 24) % 24, 30), utc, -0.1), 6);
        Assert.InRange(Horizon.HourAngle(new EquatorialCoordinates((lst + 12.5) % 24, 30), utc, -0.1), -12, 12);
        Assert.Equal(59.836, Horizon.SiderealToClock(1).TotalMinutes, 3);
    }
}

public class MeridianFlipTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(EquipmentService Equipment, SequencerService Sequencer, SequenceTarget Target)> SetUpAsync(double secondsEast)
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"),
            SolveExposureSeconds = 0, Phd2AutoConnect = false,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var autofocus = new AutofocusService(equipment, imaging, log, options);
        // Sun protection off: whether a test target is near the Sun depends on the time of day the tests run.
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None);
        var sequencer = new SequencerService(equipment, imaging, solver, autofocus, new GuiderService(options, log),
            sites, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        var mount = equipment.Mount!;
        await mount.SetSiteAsync(new GeoLocation(51.5, -0.1, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        await mount.UnparkAsync(CancellationToken.None);

        // Slew a little further east than asked (slews take a few seconds), then wait until the tracking mount is
        // exactly secondsEast before the meridian: that's where the target is, and the crossing happens during the test.
        var lead = secondsEast + 15;
        var jnow = new EquatorialCoordinates((Horizon.LocalSiderealDegrees(DateTime.UtcNow, -0.1) / 15 + lead / 3600 + 24) % 24, 30);
        await mount.SlewAsync(jnow, CancellationToken.None);
        while (Horizon.HourAngle(mount.Coordinates, DateTime.UtcNow, -0.1) < -secondsEast / 3600) await Task.Delay(10);
        var j2000 = Precession.JNowToJ2000(mount.Coordinates, DateTime.UtcNow);
        return (equipment, sequencer, new SequenceTarget("Test", j2000.RaHours.ToString("0.######"), j2000.DecDegrees.ToString("0.######")));
    }

    private static Sequence Plan(SequenceTarget target, double exposure, int count, double flipAfterSeconds, double latestSeconds) => new(
        "Flip", target,
        new SequenceStart(CoolCamera: false, CenterTarget: false, Autofocus: false, StartGuiding: false),
        [new SequenceRow("L", exposure, count)],
        Dither: new SequenceDither(EveryFrames: 0),
        Autofocus: new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null),
        MeridianFlip: new SequenceMeridianFlip(MinutesAfterMeridian: flipAfterSeconds / 60, MaxMinutesAfterMeridian: latestSeconds / 60,
            SettleSeconds: 0));

    private static async Task<SequencerStatus> WaitForEnd(SequencerService sequencer, Action<SequencerStatus>? watch = null)
    {
        for (var i = 0; i < 1200 && sequencer.Running; i++)
        {
            if (sequencer.Status is { } s) watch?.Invoke(s);
            await Task.Delay(50);
        }
        Assert.False(sequencer.Running, "the sequence didn't finish");
        return sequencer.Status!;
    }

    [Fact]
    public async Task FlipsOnceThePastTheMeridianAndCarriesOn()
    {
        var (equipment, sequencer, target) = await SetUpAsync(secondsEast: 6);
        Assert.Equal(PierSide.West, equipment.Mount!.SideOfPier);
        Assert.True(sequencer.Start(Plan(target, exposure: 0.5, count: 16, flipAfterSeconds: 2, latestSeconds: 30), out var error), error);

        var status = await WaitForEnd(sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(16, status.FramesDone);
        Assert.Equal(1, status.Flips);
        Assert.Equal(PierSide.East, equipment.Mount!.SideOfPier);
    }

    [Fact]
    public async Task WaitsForTheFlipRatherThanRunPastTheLimit()
    {
        // A 6 s frame started now would end 6 s past the meridian, beyond the 3 s limit: wait, flip at +1 s, then expose.
        var (equipment, sequencer, target) = await SetUpAsync(secondsEast: 1);
        Assert.Equal(PierSide.West, equipment.Mount!.SideOfPier);
        var waited = false;
        Assert.True(sequencer.Start(Plan(target, exposure: 6, count: 1, flipAfterSeconds: 1, latestSeconds: 3), out var error), error);

        var status = await WaitForEnd(sequencer, s => waited |= s.Message.StartsWith("Waiting"));
        Assert.Equal("Done", status.State);
        Assert.True(waited, "it should have waited for the flip");
        Assert.Equal(1, status.Flips);
        Assert.Equal(1, status.FramesDone);
        Assert.Equal(PierSide.East, equipment.Mount!.SideOfPier);
    }

    [Fact]
    public async Task LeavesAMountOnTheNormalSideAlone()
    {
        // Already past the meridian when the mount went there: it's on the East side, so no flip is due.
        var (equipment, sequencer, target) = await SetUpAsync(secondsEast: -120);
        Assert.Equal(PierSide.East, equipment.Mount!.SideOfPier);
        Assert.True(sequencer.Start(Plan(target, exposure: 0.3, count: 3, flipAfterSeconds: 1, latestSeconds: 2), out var error), error);
        var status = await WaitForEnd(sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(0, status.Flips);
    }

    [Fact]
    public void RejectsALatestTimeBeforeTheFlipTime()
    {
        var plan = Plan(new SequenceTarget("x", "1", "1"), 1, 1, flipAfterSeconds: 600, latestSeconds: 60);
        Assert.Contains("latest time", plan.Validate());
    }
}

public class HorizonGuardTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task RefusesTargetsBelowTheHorizon()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var sites = new SiteService(equipment, options, log);
        await equipment.ConnectAsync(CancellationToken.None);

        // No site known: nothing to check against.
        Assert.Null(sites.BelowHorizon(new EquatorialCoordinates(0, -89)));

        await equipment.Mount!.SetSiteAsync(new GeoLocation(51.5, -0.1, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, -0.1) / 15;
        var low = sites.BelowHorizon(Precession.JNowToJ2000(new EquatorialCoordinates((lst + 12) % 24, -30), DateTime.UtcNow), "M 7");
        Assert.NotNull(low);
        Assert.StartsWith("M 7 is below the horizon", low);
        Assert.Null(sites.BelowHorizon(Precession.JNowToJ2000(new EquatorialCoordinates(lst, 45), DateTime.UtcNow)));
    }
}

public class MountClockTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task SetsAMountClockThatRestartedAt2000()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var sites = new SiteService(equipment, options, log);
        var mount = equipment.Provider.GetMount("Simulated Mount")!;
        Assert.Equal(2000, mount.ClockUtc!.Value.Year); // like a ZWO mount after power-on

        // Connecting sends the time even though no site is set in NOVA.
        await equipment.ConnectAsync(CancellationToken.None);
        for (var i = 0; i < 100 && mount.ClockUtc!.Value.Year == 2000; i++) await Task.Delay(20);
        Assert.InRange((mount.ClockUtc!.Value - DateTime.UtcNow).TotalSeconds, -5, 5);

        // Later it resets (power blip without NOVA noticing a reconnect): the clock watch puts it right.
        await mount.SetClockAsync(new DateTimeOffset(2000, 1, 1, 0, 19, 37, TimeSpan.Zero), CancellationToken.None);
        await sites.CheckClockAsync();
        Assert.InRange((mount.ClockUtc!.Value - DateTime.UtcNow).TotalSeconds, -5, 5);
        var note = log.Recent.Select(e => e.Message).FirstOrDefault(m => m.Contains("clock said")) ?? string.Join(" | ", log.Recent.TakeLast(5).Select(e => e.Message));
        Assert.True(note.Contains("clock said 2000-01-01 00:19 UTC"), note);
        Assert.Contains("27 years off", note); // 2000-01-01 to October 2026
    }
}

public class MountLocationTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task SendsTheSiteAgainWhenTheDriverForgetsIt()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var sites = new SiteService(equipment, options, log);
        await sites.SaveAsync(new Site("Backyard", 48.5197, -123.4167, 300), null, CancellationToken.None);
        await equipment.ConnectAsync(CancellationToken.None);
        var mount = equipment.Mount!;
        for (var i = 0; i < 100 && mount.Location?.IsNear(new GeoLocation(48.5197, -123.4167, 0)) != true; i++) await Task.Delay(20);

        // Like ZWO's driver finishing its connection: it reads the mount's empty location over NOVA's.
        await mount.SetSiteAsync(new GeoLocation(0, 0, 0), DateTimeOffset.Now, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(5.1)); // past the resend limit from the connect

        await sites.EnsureMountLocationAsync();
        Assert.True(mount.Location!.IsNear(new GeoLocation(48.5197, -123.4167, 0), 0.001), $"{mount.Location}");
        Assert.Contains(log.Recent, e => e.Message.Contains("sent site 'Backyard'") && e.Message.Contains("again"));
    }
}

public class SunProtectionTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task KeepsTheTelescopeAwayFromTheSun()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(30, CancellationToken.None);
        var now = DateTime.UtcNow;
        var sun = Precession.JNowToJ2000(SolarSystem.Sun(now), now);

        // No site: NOVA can't tell if the Sun is up, so it assumes it is.
        var near = sites.NearSun(new EquatorialCoordinates(sun.RaHours, sun.DecDegrees + 10), "Venus");
        Assert.NotNull(near);
        Assert.StartsWith("Venus is only 10° from the Sun", near);
        Assert.Null(sites.NearSun(new EquatorialCoordinates((sun.RaHours + 6) % 24, 0)));
        Assert.Throws<InvalidOperationException>(() => sites.EnsureAwayFromSun(sun));

        // A site where the Sun is down right now (the opposite longitude to where it's overhead): no limit.
        var gmst = Horizon.LocalSiderealDegrees(now, 0);
        var subSolar = (sun.RaHours * 15 - gmst + 540) % 360 - 180;
        await sites.SaveAsync(new Site("Night side", 0, (subSolar + 360) % 360 - 180, 0), null, CancellationToken.None);
        Assert.True(sites.SunAltitude < -30, $"{sites.SunAltitude}");
        Assert.Null(sites.NearSun(sun));

        // The day side: refused again; and with protection off, allowed.
        await sites.SaveAsync(new Site("Day side", 0, subSolar, 0), null, CancellationToken.None);
        Assert.True(sites.SunAltitude > 30, $"{sites.SunAltitude}");
        Assert.NotNull(sites.NearSun(sun));
        // Switched off (indoors): no limit, but the distance is kept for when it's switched back on.
        await sites.SetSunProtectionAsync(false, CancellationToken.None);
        Assert.Null(sites.NearSun(sun));
        Assert.False(sites.Status.SunProtection);
        Assert.Equal(30, sites.Status.SunAvoidance);
        await sites.SetSunProtectionAsync(true, CancellationToken.None);
        Assert.NotNull(sites.NearSun(sun));

        await sites.SetSunAvoidanceAsync(0, CancellationToken.None);
        Assert.Null(sites.NearSun(sun));
        Assert.Throws<ArgumentException>(() => sites.SetSunAvoidanceAsync(120, CancellationToken.None).GetAwaiter().GetResult());
    }
}

public class SunWatchdogTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task HaltsASlewHeadingIntoTheSun()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var sites = new SiteService(equipment, options, log);
        var now = DateTime.UtcNow;
        var sun = SolarSystem.Sun(now); // JNow
        // A site where the Sun is high right now.
        var subSolar = (sun.RaHours * 15 - Horizon.LocalSiderealDegrees(now, 0) + 540) % 360 - 180;
        await sites.SaveAsync(new Site("Day side", sun.DecDegrees, subSolar, 0), null, CancellationToken.None);
        await equipment.ConnectAsync(CancellationToken.None);
        var mount = equipment.Mount!;
        await mount.UnparkAsync(CancellationToken.None);
        // Getting into position (protection off for that: the way there isn't what's being tested).
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None);
        await mount.SlewAsync(new EquatorialCoordinates((sun.RaHours + 4) % 24, sun.DecDegrees), CancellationToken.None);
        await sites.SetSunAvoidanceAsync(30, CancellationToken.None);

        // Straight at the Sun and past it, bypassing NOVA's own checks (as a hand controller could).
        try
        {
            await mount.SlewAsync(new EquatorialCoordinates((sun.RaHours + 20) % 24, sun.DecDegrees), CancellationToken.None);
        }
        catch (Exception)
        {
            // an aborted slew may end with an error
        }
        var distance = Precession.Separation(mount.Coordinates, SolarSystem.Sun(DateTime.UtcNow));
        Assert.True(distance > 25, $"stopped {distance:0.0}° from the Sun");
        Assert.Contains(log.Recent, e => e.Message.Contains("slewing towards the Sun"));
    }
}

public class ManualMotionTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(EquipmentService Equipment, MountMotionService Motion)> SetUpAsync()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        var motion = new MountMotionService(equipment, log, options);
        await motion.SetRateAsync("4", CancellationToken.None); // 720x, 3°/s
        return (equipment, motion);
    }

    [Fact]
    public async Task MovesWhileHeldAndStopsWhenReleased()
    {
        var (equipment, motion) = await SetUpAsync();
        var mount = equipment.Mount!;
        var dec = mount.Coordinates.DecDegrees;

        for (var i = 0; i < 3; i++) // held for ~1.2 s, renewing like the browser does
        {
            await motion.MoveAsync(MotionDirection.North, CancellationToken.None);
            await Task.Delay(400);
        }
        Assert.Equal([MotionDirection.North], motion.Active);
        await motion.StopAsync(MotionDirection.North, CancellationToken.None);
        var stopped = mount.Coordinates.DecDegrees;
        Assert.InRange(stopped - dec, 2, 6); // ~3°/s for ~1.2 s

        await Task.Delay(400);
        Assert.Equal(stopped, mount.Coordinates.DecDegrees, 6);
        Assert.Empty(motion.Active);
    }

    [Fact]
    public async Task StopsByItselfWhenTheBrowserGoesQuiet()
    {
        var (equipment, motion) = await SetUpAsync();
        var mount = equipment.Mount!;
        await motion.MoveAsync(MotionDirection.West, CancellationToken.None); // pressed… then the Wi-Fi drops

        await Task.Delay(MountMotionService.Timeout + TimeSpan.FromMilliseconds(700));
        Assert.Empty(motion.Active);
        var ra = mount.Coordinates.RaHours;
        await Task.Delay(400);
        Assert.Equal(ra, mount.Coordinates.RaHours, 9);
    }

    [Theory]
    // ZWO's driver: the AM5's 1440x is listed too (an AM3 ignores it).
    [InlineData("0.25x 0.5x 1x 2x 4x 8x 20x 60x 720x 1440x", "720x")]
    // Heavy Duty Mode: NOVA leaves out what's above 720x; it used to fall back to 60x.
    [InlineData("0.25x 0.5x 1x 2x 4x 8x 20x 60x 720x", "720x")]
    [InlineData("1x 8x 64x", "64x")]
    [InlineData("Guide Centering Find Max", "Find")] // not multiples: one below the fastest
    public void StartsAtTheFastestSpeedUpTo720x(string labels, string expected)
    {
        var rates = labels.Split(' ').Select(l => new Nova.Core.Equipment.SlewRate("RATE_" + l, l)).ToList();
        Assert.Equal("RATE_" + expected, MountMotionService.DefaultSpeed(rates));
    }

    [Fact]
    public async Task GivesAMountItsSpeedWhenItConnects()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        using var motion = new MountMotionService(equipment, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        for (var i = 0; i < 100 && equipment.Mount!.CurrentSlewRate != "4"; i++) await Task.Delay(20);
        Assert.Equal("4", equipment.Mount!.CurrentSlewRate); // 720x
    }

    [Fact]
    public async Task AvoidsTheFastestSpeedUntilOneIsChosenAndRemembersTheChoice()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        await equipment.Mount.SetSlewRateAsync("5", CancellationToken.None); // the driver's default: the fastest

        var motion = new MountMotionService(equipment, log, options);
        Assert.Equal("4", motion.SpeedFor(equipment.Mount)); // one below the top, like an AM3's 720x
        await motion.MoveAsync(MotionDirection.North, CancellationToken.None);
        Assert.Equal("4", equipment.Mount.CurrentSlewRate);
        await motion.StopAsync(null, CancellationToken.None);

        await motion.SetRateAsync("2", CancellationToken.None);
        Assert.Equal("2", new MountMotionService(equipment, log, options).SpeedFor(equipment.Mount)); // remembered
    }

    [Fact]
    public async Task RefusesWhileParked()
    {
        var (equipment, motion) = await SetUpAsync();
        await equipment.Mount!.ParkAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => motion.MoveAsync(MotionDirection.East, CancellationToken.None));
        Assert.Contains("Unpark", error.Message);
    }
}

public class MountLockTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task LocksManualMovesWhileExposing()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var sequencer = new SequencerService(equipment, imaging, solver, new AutofocusService(equipment, imaging, log, options),
            new GuiderService(options, log), new SiteService(equipment, options, log), log, options);
        await equipment.ConnectAsync(CancellationToken.None);

        Assert.Null(Nova.Server.Api.NovaApi.ManualMoveLock(imaging, sequencer));
        var exposing = imaging.CaptureAsync(2, save: false, CancellationToken.None);
        for (var i = 0; i < 100 && !imaging.IsBusy; i++) await Task.Delay(10);
        Assert.Contains("camera is exposing", Nova.Server.Api.NovaApi.ManualMoveLock(imaging, sequencer));
        await exposing;
        Assert.Null(Nova.Server.Api.NovaApi.ManualMoveLock(imaging, sequencer));
    }
}
