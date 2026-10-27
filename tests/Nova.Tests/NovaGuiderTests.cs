using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.Simulation;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

/// <summary>NOVA's own guider end to end on the simulator: guide camera, calibration, guiding, dither, stop.</summary>
public class NovaGuiderTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-guider-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_config, recursive: true); } catch (IOException) { }
    }

    private sealed record Rig(EquipmentService Equipment, NovaGuider Nova, GuiderService Guider, SimulatedMount Mount) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Guider.DisposeAsync();
            await Equipment.DisposeAsync();
        }
    }

    private ActivityLog? _log;

    private async Task<Rig> SetUpAsync()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"), Phd2AutoConnect = false,
        });
        var log = _log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var nova = new NovaGuider(equipment, optics, options, log);
        var guider = new GuiderService(options, log, nova);
        await equipment.ConnectAsync(CancellationToken.None);
        var mount = (SimulatedMount)equipment.Mount!;
        await mount.UnparkAsync(CancellationToken.None);

        // Quick: short guide frames and a fast guide rate, so calibration takes seconds rather than minutes.
        mount.GuideRate = 4;
        await guider.SetBackendAsync("nova", CancellationToken.None);
        nova.UpdateSettings(new NovaGuiderSettings(Camera: "Simulated Guide Camera", ExposureSeconds: 0.2, CalibrationStepMs: 200));
        return new Rig(equipment, nova, guider, mount);
    }

    [Fact]
    public async Task CalibratesGuidesDithersAndStops()
    {
        await using var rig = await SetUpAsync();
        var (guider, nova, mount) = (rig.Guider, rig.Nova, rig.Mount);
        Assert.True(guider.IsConnected);
        Assert.Equal("nova", guider.Status.Backend);

        // Periodic error the guider has to fight: ±8″ over 40 s (2 guide pixels each way).
        mount.PeriodicError = [(8, 40)];

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await guider.GuideAndWaitAsync(new SettleSettings(Pixels: 1.5, Seconds: 2, Timeout: 60), timeout.Token);
        Assert.True(guider.IsGuiding);

        var status = guider.Status;
        var cal = status.Nova!.Calibration!;
        // The simulated guide camera has north up and east left: West moves the stars right-ish… whichever way,
        // the axes must be near square (the periodic error tilts them a little) and the rate match the 4× guide rate.
        Assert.True(cal.Orthogonality < 10, $"{cal.Orthogonality}° from square");
        Assert.Contains(cal.Points!, p => p.Phase == "North");
        Assert.InRange(cal.DecArcsecPerSecond!.Value, 54, 66); // 4 × 15.04″/s
        Assert.NotNull(status.Nova.Star);
        Assert.Equal("arcsec", status.Stats.Units);

        // Guide for a while: the star stays close despite the periodic error (unguided it would swing ±2 px).
        await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token);
        var stats = guider.Status.Stats;
        Assert.True(stats.Samples > 10, $"{stats.Samples} guide steps");
        Assert.True(stats.RmsTotal < 4, $"RMS {stats.RmsTotal:0.00}″"); // under a pixel

        // Dither and settle again: the walk to the new place is marked, and doesn't count in the RMS.
        await guider.DitherAndWaitAsync(3, raOnly: false, new SettleSettings(1.5, 2, 60), timeout.Token);
        Assert.True(guider.IsGuiding);
        var history = guider.Status.History;
        Assert.Single(history, p => p.Dither);
        Assert.Contains(history, p => p.Settling);
        Assert.True(guider.Status.Stats.RmsTotal < 4, $"RMS {guider.Status.Stats.RmsTotal:0.00}″ after the dither");
        Assert.NotNull(nova.FramePng());

        await guider.StopAsync(CancellationToken.None);
        Assert.False(guider.IsGuiding);
        Assert.Equal(GuiderState.Stopped, guider.Status.State);

        // The night's guide log, in PHD2's format, in the night's image folder.
        var log = Assert.Single(nova.GuideLogs());
        Assert.False(log.OnDrive);
        var path = nova.GuideLogPath(log.Name, onDrive: false)!;
        Assert.Equal(Path.Combine(_config, "images", log.Night, log.Name), path);
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("PHD2 version", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Calibration Begins at"));
        Assert.Contains(lines, l => l.StartsWith("West,0,"));
        Assert.Contains(lines, l => l.StartsWith("West calibration complete. Angle = "));
        Assert.Contains(lines, l => l.StartsWith("North calibration complete. Angle = "));
        Assert.Contains(lines, l => l.StartsWith("Guiding Begins at"));
        Assert.Contains(lines, l => l.StartsWith("X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700"));
        Assert.Contains(lines, l => l.StartsWith("Frame,Time,mount,dx,dy,RARawDistance"));
        var rows = lines.Where(l => l.Length > 0 && char.IsDigit(l[0])).ToList();
        Assert.True(rows.Count > 10, $"{rows.Count} guide rows");
        Assert.All(rows, r => Assert.Equal(18, r.Split(',').Length));
        Assert.Contains(lines, l => l.StartsWith("INFO: DITHER by "));
        Assert.Contains(lines, l => l == "INFO: SETTLING STATE CHANGE, Settling complete");
        Assert.Contains(lines, l => l.StartsWith("Guiding Ends at"));

        // Only NOVA's guide logs can be fetched.
        Assert.Null(nova.GuideLogPath("../guider.json", onDrive: false));
    }

    [Fact]
    public async Task ReusesTheSavedCalibrationAndForgetsItWhenAsked()
    {
        await using (var rig = await SetUpAsync())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await rig.Nova.CalibrateAsync(timeout.Token);
            Assert.NotNull(rig.Nova.Status.Calibration);
            await rig.Guider.StopAsync(CancellationToken.None);
        }

        // A new start (NOVA restarted) finds it in guider.json, with the backend still NOVA.
        await using var again = await SetUpAsync();
        Assert.Equal("nova", again.Guider.Backend);
        Assert.NotNull(again.Nova.Status.Calibration);

        // Guide straight away with it, from a stopped guide camera: no calibration run, it waits for the first star.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
            await again.Guider.GuideAndWaitAsync(new SettleSettings(1.5, 1, 30), timeout.Token);
        Assert.True(again.Guider.IsGuiding);
        await again.Guider.StopAsync(CancellationToken.None);

        // A different binning needs its own calibration.
        again.Nova.UpdateSettings(again.Nova.Settings with { Binning = 2 });
        Assert.Null(again.Nova.Status.Calibration);
        again.Nova.UpdateSettings(again.Nova.Settings with { Binning = 1 });
        again.Nova.ClearCalibration();
        Assert.Null(again.Nova.Status.Calibration);
    }

    [Fact]
    public async Task AGuideCameraThatDropsOutIsWaitedForAndGuidingCarriesOn()
    {
        // As when an ASI120 drops off USB: frames stop. Say so, reconnect it when it's back, and carry on.
        await using var rig = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await rig.Nova.LoopAsync(timeout.Token);
        while (rig.Nova.Status.Stars.Count < 1) await Task.Delay(100, timeout.Token);

        var camera = rig.Equipment.Provider.GetCamera("Simulated Guide Camera")!;
        await camera.DisconnectAsync(CancellationToken.None);
        while (!_log!.Recent.Any(e => e.Message.Contains("is back"))) await Task.Delay(100, timeout.Token);

        Assert.Contains(_log.Recent, e => e.Message.Contains("Simulated Guide Camera stopped sending frames") && e.Message.Contains("dropped off USB"));
        Assert.True(camera.IsConnected);
        Assert.Equal(GuiderState.Looping, rig.Nova.State); // still going, not "Guiding stopped"
        Assert.DoesNotContain(_log.Recent, e => e.Message.StartsWith("Guiding stopped"));
    }

    [Fact]
    public async Task AClickedStarBecomesTheGuideStar()
    {
        await using var rig = await SetUpAsync();
        Assert.Throws<InvalidOperationException>(() => rig.Nova.SelectStar(100, 100)); // not looping

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await rig.Nova.LoopAsync(timeout.Token);
        while (rig.Nova.Status.Stars.Count < 2) await Task.Delay(100, timeout.Token);
        var marks = rig.Nova.Status.Stars;
        Assert.True(marks[0].Lock);
        var other = marks[1];

        rig.Nova.SelectStar(other.X + 2, other.Y - 1);
        var now = rig.Nova.Status.Stars[0];
        Assert.True(now.Lock);
        Assert.Equal(other.X, now.X, 1);
        Assert.Equal(other.Y, now.Y, 1);

        Assert.Throws<InvalidOperationException>(() => rig.Nova.SelectStar(-500, -500)); // no star there
        await rig.Guider.StopAsync(CancellationToken.None);
        Assert.Empty(rig.Nova.Status.Stars);
    }

    [Fact]
    public async Task DarksStopHotPixelsBeingTakenForStars()
    {
        await using var rig = await SetUpAsync();
        var camera = (SimulatedCamera)rig.Nova.Camera!;
        camera.HotPixels = 200;
        // The scope covered: a dark frame is all the guide camera sees.
        await camera.ApplyAsync(new CameraSettingsUpdate(FrameType: FrameType.Dark), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        async Task<int> LoopAndCountStars()
        {
            await rig.Nova.LoopAsync(timeout.Token);
            var frame = rig.Nova.Status.Frame;
            while (rig.Nova.Status.Frame < frame + 3) await Task.Delay(50, timeout.Token);
            var count = rig.Nova.Status.Stars.Count;
            await rig.Nova.StopAsync();
            return count;
        }

        // Without darks the warm clusters look like stars, and the loop locks on one.
        Assert.True(await LoopAndCountStars() > 0);

        // Build the library (refused while looping), then the same covered frames show nothing to guide on.
        await rig.Nova.LoopAsync(timeout.Token);
        Assert.False(rig.Guider.BuildDarks([0.2], 3, out var refused));
        Assert.Contains("Stop the guide camera", refused);
        await rig.Nova.StopAsync();
        await rig.Nova.BuildDarksAsync([0.2, 0.4], 3, timeout.Token);
        var darks = rig.Nova.Status.Darks!;
        Assert.Equal([0.2, 0.4], darks.Exposures);
        Assert.True(darks.InUse);
        Assert.True(darks.HotPixels >= 100, $"{darks.HotPixels} hot pixels");
        Assert.Equal(FrameType.Light, camera.Controls.FrameType); // the build ends back on light frames
        await camera.ApplyAsync(new CameraSettingsUpdate(FrameType: FrameType.Dark), CancellationToken.None);
        Assert.Equal(0, await LoopAndCountStars());

        rig.Nova.ClearDarks();
        Assert.Null(rig.Nova.Status.Darks);
    }

    [Fact]
    public async Task SetsTheGuideCamerasGain()
    {
        await using var rig = await SetUpAsync();
        var camera = rig.Nova.Camera!;
        await camera.ConnectAsync(CancellationToken.None);
        var range = rig.Nova.Status.CameraGain!;
        var wanted = range.Value == 150 ? 120 : 150;

        rig.Nova.UpdateSettings(rig.Nova.Settings with { Gain = wanted });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await rig.Nova.LoopAsync(timeout.Token);
        var frame = rig.Nova.Status.Frame;
        while (rig.Nova.Status.Frame < frame + 1) await Task.Delay(50, timeout.Token);
        Assert.Equal(wanted, camera.Controls.Gain!.Value);
        Assert.Equal(wanted, rig.Nova.Status.CameraGain!.Value);
        await rig.Nova.StopAsync();

        Assert.Throws<ArgumentException>(() => rig.Nova.UpdateSettings(rig.Nova.Settings with { Gain = range.Max + 1 }));
        rig.Nova.UpdateSettings(rig.Nova.Settings with { Gain = null }); // back to the driver's
    }

    [Fact]
    public async Task GuidingAssistantMeasuresTheSeeingAndAppliesItsAdvice()
    {
        await using var rig = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await rig.Nova.CalibrateAsync(timeout.Token); // so it measures along RA and Dec
        rig.Mount.SeeingJitterArcsec = 2;             // the star jumps ~2″ (half a guide pixel) per frame and axis

        Assert.False(rig.Guider.Assistant(10, out var tooShort));
        Assert.Contains("30", tooShort);
        await rig.Nova.AssistantAsync(30, timeout.Token);

        var result = rig.Nova.Status.Assistant!;
        Assert.Equal("RA/Dec", result.Axes);
        Assert.Equal("arcsec", result.Units);
        Assert.InRange(result.JitterRa, 1.3, 2.8);
        Assert.InRange(result.JitterDec, 1.3, 2.8);
        Assert.True(result.Frames > 20, $"{result.Frames} frames");
        Assert.Equal(4, result.RecommendedExposure); // over 1.5″ of jitter: longer exposures average it
        Assert.InRange(result.RecommendedRaMinMove, 0.2, 0.5); // 0.65 × ~0.5 px
        Assert.Equal((result.JitterRa, result.JitterDec), rig.Nova.Seeing);

        rig.Nova.ApplyAssistant();
        Assert.Equal(result.RecommendedRaMinMove, rig.Nova.Settings.Algorithms.RaMinMove);
        Assert.Equal(result.RecommendedDecMinMove, rig.Nova.Settings.Algorithms.DecMinMove);
        Assert.Equal(4, rig.Nova.Settings.ExposureSeconds);
        await rig.Nova.StopAsync();
    }

    [Fact]
    public async Task SavesLoadsAndDeletesGuideProfiles()
    {
        await using var rig = await SetUpAsync();
        var nova = rig.Nova;
        var mine = nova.Settings with { ExposureSeconds = 1.5, Algorithms = nova.Settings.Algorithms with { RaAggressiveness = 0.55 } };
        nova.UpdateSettings(mine);
        nova.SaveProfile("  Windy night ");
        Assert.Equal("Windy night", nova.Status.Profile);
        Assert.Contains(nova.Status.Profiles!, p => p is { Name: "Windy night", BuiltIn: false });
        Assert.Contains(nova.Status.Profiles!, p => p is { Name: "ZWO AM mount", BuiltIn: true });

        // A built-in preset: its exposure, gain and algorithms; the camera stays.
        nova.LoadProfile("ZWO AM mount");
        Assert.Equal("ZWO AM mount", nova.Status.Profile);
        Assert.Equal(1.5, nova.Settings.ExposureSeconds);
        Assert.Equal(60, nova.Settings.Gain);
        Assert.Equal(0.7, nova.Settings.Algorithms.RaAggressiveness);
        Assert.Equal("Simulated Guide Camera", nova.Settings.Camera);

        // Changed by hand: no profile matches until one is loaded again.
        nova.UpdateSettings(nova.Settings with { ExposureSeconds = 2.5 });
        Assert.Null(nova.Status.Profile);
        nova.LoadProfile("windy night"); // names match either case
        Assert.Equal(mine, nova.Settings);
        Assert.Equal("Windy night", nova.Status.Profile);

        Assert.Throws<ArgumentException>(() => nova.SaveProfile("Poor seeing")); // a built-in's name
        Assert.Throws<ArgumentException>(() => nova.SaveProfile(" "));
        Assert.Throws<ArgumentException>(() => nova.LoadProfile("Nope"));
        Assert.Throws<ArgumentException>(() => nova.DeleteProfile("NOVA defaults")); // built-in

        // A profile with another binning can't be loaded while the guide camera runs.
        nova.UpdateSettings(mine with { Binning = 2 });
        nova.SaveProfile("Binned");
        nova.UpdateSettings(mine);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await nova.LoopAsync(timeout.Token);
        Assert.Throws<InvalidOperationException>(() => nova.LoadProfile("Binned"));
        await nova.StopAsync();
        nova.LoadProfile("Binned");
        Assert.Equal(2, nova.Settings.Binning);

        nova.DeleteProfile("Windy night");
        Assert.DoesNotContain(nova.Status.Profiles!, p => p.Name == "Windy night");
        Assert.Equal("", nova.Status.Defaults!.Mount); // the simulator's mount isn't a ZWO AM
    }

    [Fact]
    public async Task PredictivePecLearnsTheSimulatedMountsPeriodicError()
    {
        await using var rig = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await rig.Nova.CalibrateAsync(timeout.Token);

        rig.Mount.PeriodicError = [(12, 24)]; // ±12″ every 24 s (a fast, made-up strain-wave error)
        rig.Nova.UpdateSettings(rig.Nova.Settings with
        {
            Algorithms = rig.Nova.Settings.Algorithms with { RaAlgorithm = RaAlgorithm.PredictivePec },
        });
        Assert.Equal(PecState.Learning, rig.Nova.Status.Pec!.State);
        await rig.Guider.GuideAndWaitAsync(new SettleSettings(2, 1, 60), timeout.Token);

        // Two cycles (48 s) and a fit later it predicts, with the period it was given.
        while (rig.Nova.Status.Pec!.State != PecState.Predicting) await Task.Delay(500, timeout.Token);
        var pec = rig.Nova.Status.Pec!;
        Assert.Contains(pec.Periods, p => Math.Abs(p - 24) < 2);
        Assert.True(pec.Quality > 0.5, $"quality {pec.Quality}");

        await Task.Delay(TimeSpan.FromSeconds(10), timeout.Token);
        Assert.True(rig.Guider.Status.Stats.RmsRa < 4, $"RA RMS {rig.Guider.Status.Stats.RmsRa:0.00}″"); // under a guide pixel
        await rig.Guider.StopAsync(CancellationToken.None);

        var lines = File.ReadAllLines(rig.Nova.GuideLogPath(rig.Nova.GuideLogs()[0].Name, onDrive: false)!);
        Assert.Contains(lines, l => l.StartsWith("X guide algorithm = Predictive PEC, Period = auto"));
        Assert.Contains(lines, l => l.StartsWith("INFO: PEC: predicting, period "));
    }

    [Fact]
    public async Task MeasuresAndCompensatesTheSimulatedMountsDecBacklash()
    {
        await using var rig = await SetUpAsync();
        rig.Mount.DecBacklashArcsec = 20;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await rig.Nova.CalibrateAsync(timeout.Token);

        var cal = rig.Nova.Status.Calibration!;
        var arcsecPerPixel = cal.DecArcsecPerSecond!.Value / cal.DecRate;
        var measured = cal.DecBacklashPixels * arcsecPerPixel;
        Assert.InRange(measured, 14, 26); // 20″, through star centroids
        var backlash = rig.Nova.Status.Backlash!;
        Assert.True(backlash.Enabled);
        Assert.Equal(cal.DecBacklashMs, backlash.MeasuredMs);

        await rig.Guider.GuideAndWaitAsync(new SettleSettings(2, 1, 60), timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        await rig.Guider.StopAsync(CancellationToken.None);
        var lines = File.ReadAllLines(rig.Nova.GuideLogPath(rig.Nova.GuideLogs()[0].Name, onDrive: false)!);
        Assert.Contains(lines, l => l.StartsWith("Backlash comp = enabled, pulse = ") && !l.EndsWith(" 0 ms"));

        // Turned off: shown as off, and the guide log says so.
        rig.Nova.UpdateSettings(rig.Nova.Settings with { Algorithms = rig.Nova.Settings.Algorithms with { DecBacklashCompensation = false } });
        Assert.False(rig.Nova.Status.Backlash!.Enabled);
    }

    [Fact]
    public async Task FollowsACometWithLowpass2AndAutomaticExposureAndTurnsWithTheCamera()
    {
        await using var rig = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Assert.Throws<ArgumentException>(() => rig.Nova.UpdateSettings(rig.Nova.Settings with { AutoExposure = true, MinExposureSeconds = 2, MaxExposureSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => rig.Nova.UpdateSettings(rig.Nova.Settings with { TargetSnr = 1 }));
        await rig.Nova.CalibrateAsync(timeout.Token);
        var rotator = rig.Equipment.Rotator!;
        Assert.True(rotator.IsConnected);

        // A comet going north at 10″ a second, guided with Lowpass2 on Dec; the star is bright, so exposures shorten to the minimum.
        rig.Nova.UpdateSettings(rig.Nova.Settings with
        {
            ExposureSeconds = 0.5, CometTracking = true, CometDecRate = 36_000, AutoExposure = true, MinExposureSeconds = 0.1, MaxExposureSeconds = 0.5, TargetSnr = 100,
            Algorithms = rig.Nova.Settings.Algorithms with { DecAlgorithm = DecAlgorithm.Lowpass2 },
        });
        await rig.Guider.GuideAndWaitAsync(new SettleSettings(2, 1, 60), timeout.Token);
        var cal = rig.Nova.Status.Calibration!;
        var scale = cal.DecArcsecPerSecond!.Value / cal.DecRate;
        var start = rig.Nova.Status.Star!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
        var now = rig.Nova.Status.Star!;
        var moved = Math.Sqrt(Math.Pow(now.LockX - start.LockX, 2) + Math.Pow(now.LockY - start.LockY, 2)) * scale;
        Assert.InRange(moved, 10 * clock.Elapsed.TotalSeconds * 0.7, 10 * clock.Elapsed.TotalSeconds * 1.3);
        Assert.True(Math.Sqrt(Math.Pow(now.X - now.LockX, 2) + Math.Pow(now.Y - now.LockY, 2)) < 3, "the star follows the lock");
        Assert.True(rig.Nova.Status.Exposure == 0.1, $"exposure {rig.Nova.Status.Exposure}, SNR {now.Snr}");
        await rig.Guider.StopAsync(CancellationToken.None);

        var lines = File.ReadAllLines(rig.Nova.GuideLogPath(rig.Nova.GuideLogs()[0].Name, onDrive: false)!);
        Assert.Contains(lines, l => l.StartsWith("Y guide algorithm = Lowpass2, Aggressiveness = 100.000"));
        Assert.Contains(lines, l => l.StartsWith("Comet tracking = enabled, RA rate = 0.00 a-s/hr, Dec rate = 36000.00 a-s/hr"));
        Assert.Contains(lines, l => l.Contains("Star mass tolerance = 50.0%"));
        Assert.Contains(lines, l => l.StartsWith("INFO: Guiding parameter change, Exposure = ") && l.EndsWith("ms (auto)"));

        // An off-axis guider: turning the camera turns the calibration with it.
        rig.Nova.UpdateSettings(rig.Nova.Settings with { RotateCalibration = true });
        Assert.Equal(0, rig.Nova.Status.Calibration!.CameraTurn);
        await rotator.MoveToAsync((rotator.Angle + 30) % 360, timeout.Token);
        Assert.Equal(30, rig.Nova.Status.Calibration!.CameraTurn!.Value, 0.5);
        rig.Nova.UpdateSettings(rig.Nova.Settings with { RotateCalibration = false });
        Assert.Null(rig.Nova.Status.Calibration!.CameraTurn);
    }

    [Fact]
    public async Task ANewInstallGuidesWithNovaAndAUsedOneKeepsPhd2()
    {
        NovaGuider Make(string folder)
        {
            var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = folder, Phd2AutoConnect = false });
            var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
            var equipment = new EquipmentService(options, log);
            return new NovaGuider(equipment, new OpticsService(equipment, options, log), options, log);
        }

        // Nothing set up yet: NOVA's guider, and that's kept (it doesn't change when a site is added later).
        var fresh = Path.Combine(_config, "fresh");
        Assert.Equal("nova", Make(fresh).Backend);
        await File.WriteAllTextAsync(Path.Combine(fresh, "sites.json"), "{}");
        Assert.Equal("nova", Make(fresh).Backend);

        // Used before (a site set up, no guider settings): PHD2, as it was.
        var used = Directory.CreateDirectory(Path.Combine(_config, "used")).FullName;
        await File.WriteAllTextAsync(Path.Combine(used, "sites.json"), "{}");
        var guider = Make(used);
        Assert.Equal("phd2", guider.Backend);
        guider.SetBackend("nova");
        Assert.Equal("nova", Make(used).Backend); // a choice made is kept
    }

    [Fact]
    public async Task SwitchingBackendsAndRefusals()
    {
        await using var rig = await SetUpAsync();
        Assert.False(rig.Guider.Dither(3, false, new SettleSettings(), out var _) && rig.Guider.IsGuiding);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Nova.DitherAsync(3, false, new SettleSettings(), CancellationToken.None)); // not guiding

        Assert.Throws<ArgumentException>(() => rig.Nova.UpdateSettings(new NovaGuiderSettings(ExposureSeconds: 0)));

        await rig.Guider.SetBackendAsync("phd2", CancellationToken.None);
        Assert.Equal("phd2", rig.Guider.Status.Backend);
        Assert.False(rig.Guider.IsConnected); // PHD2 isn't running in the tests
        Assert.False(rig.Guider.Loop(out var error));
        Assert.Contains("isn't the one in use", error);
        Assert.NotNull(rig.Guider.Status.Nova); // still there, for switching back
    }
}
