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

    private async Task<Rig> SetUpAsync()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config, Phd2AutoConnect = false });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
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
        Assert.InRange(cal.DecArcsecPerSecond!.Value, 54, 66); // 4 × 15.04″/s
        Assert.NotNull(status.Nova.Star);
        Assert.Equal("arcsec", status.Stats.Units);

        // Guide for a while: the star stays close despite the periodic error (unguided it would swing ±2 px).
        await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token);
        var stats = guider.Status.Stats;
        Assert.True(stats.Samples > 10, $"{stats.Samples} guide steps");
        Assert.True(stats.RmsTotal < 4, $"RMS {stats.RmsTotal:0.00}″"); // under a pixel

        // Dither and settle again.
        await guider.DitherAndWaitAsync(3, raOnly: false, new SettleSettings(1.5, 2, 60), timeout.Token);
        Assert.True(guider.IsGuiding);
        Assert.NotNull(nova.FramePng());

        await guider.StopAsync(CancellationToken.None);
        Assert.False(guider.IsGuiding);
        Assert.Equal(GuiderState.Stopped, guider.Status.State);
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
