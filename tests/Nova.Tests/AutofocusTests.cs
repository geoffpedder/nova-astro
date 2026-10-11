using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Focusing;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class FocusCurveTests
{
    private static List<FocusPoint> Hyperbola(double best, double minHfr, double depth, int from, int step, int count, double noise = 0, int seed = 1)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(i =>
        {
            var x = from + i * step;
            var off = (x - best) / depth;
            return new FocusPoint(x, minHfr * Math.Sqrt(1 + off * off) + (random.NextDouble() - 0.5) * 2 * noise, 50);
        }).ToList();
    }

    [Fact]
    public void FindsTheMinimumOfAnExactHyperbola()
    {
        var fit = FocusCurve.Fit(Hyperbola(best: 25_130, minHfr: 2.1, depth: 600, from: 23_800, step: 300, count: 9))!;
        Assert.Equal(25_130, fit.BestPosition);
        Assert.Equal(2.1, fit.BestHfr, 3);
        Assert.Equal(1, fit.RSquared, 3);
    }

    [Fact]
    public void ToleratesNoise()
    {
        var fit = FocusCurve.Fit(Hyperbola(best: 12_345, minHfr: 2.5, depth: 80, from: 12_000, step: 80, count: 9, noise: 0.15))!;
        Assert.InRange(fit.BestPosition, 12_345 - 25, 12_345 + 25);
        Assert.True(fit.RSquared > 0.95, $"R² {fit.RSquared}");
    }

    [Fact]
    public void RejectsCurvesWithoutAMinimum()
    {
        // Peaked (∩), e.g. the measurement went wrong.
        var peaked = Enumerable.Range(0, 7).Select(i => new FocusPoint(i * 100, 5 - Math.Abs(i - 3) * 0.8, 20)).ToList();
        Assert.Null(FocusCurve.Fit(peaked));
        Assert.Null(FocusCurve.Fit([new FocusPoint(0, 3, 10), new FocusPoint(100, 2, 10)]));
    }
}

public class AutofocusTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(EquipmentService, AutofocusService)> SetUpAsync()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var imaging = new ImagingService(equipment, new OpticsService(equipment, options, log), log, hub, options);
        var autofocus = new AutofocusService(equipment, imaging, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return (equipment, autofocus);
    }

    [Fact]
    public async Task FocusesTheSimulator()
    {
        var (equipment, autofocus) = await SetUpAsync();
        Assert.Null(autofocus.UpdateSettings(new AutofocusSettings(StepSize: 300, StepsPerSide: 4, ExposureSeconds: 0.5)));

        // The simulated focuser starts at 23800, best focus is 25000: the first sweep (22600-25000) has its
        // best point at the edge, so autofocus has to extend outward before fitting.
        var status = await autofocus.RunAsync(CancellationToken.None);

        Assert.True(status.Succeeded, status.Message);
        // Within a third of the depth of focus (600 steps): under 6% larger stars than perfect.
        Assert.InRange(status.BestPosition!.Value, 25_000 - 200, 25_000 + 200);
        var lowest = status.Points.MinBy(p => p.Hfr)!.Position;
        Assert.True(status.Points.Count(p => p.Position > lowest) >= 3, "both arms of the V should be measured");
        Assert.Equal(status.BestPosition, equipment.Focuser!.Position);
        Assert.Contains(status.Points, p => p.Position > 25_000);
        Assert.True(status.Fit!.RSquared > 0.9, $"R² {status.Fit.RSquared}");
        Assert.True(status.FinalHfr < status.Points.Max(p => p.Hfr) / 2, "should end much sharper than the worst point");
        Assert.True(status.FinalHfr < status.StartHfr, $"should beat the start: {status.StartHfr} → {status.FinalHfr}");
    }

    [Fact]
    public async Task MovesDuringDownloadsButNeverWhileExposing()
    {
        var (equipment, autofocus) = await SetUpAsync();
        Assert.Null(autofocus.UpdateSettings(new AutofocusSettings(StepSize: 300, StepsPerSide: 3, ExposureSeconds: 0.5)));
        var camera = equipment.Camera!;
        var focuser = equipment.Focuser!;

        // Watch the whole run: the camera must never be exposing while the focuser moves.
        var overlap = false;
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watching.IsCancellationRequested)
            {
                if (camera.State == Nova.Core.Equipment.CameraState.Exposing && focuser.IsMoving) overlap = true;
                await Task.Delay(10);
            }
        });
        var status = await autofocus.RunAsync(CancellationToken.None);
        watching.Cancel();
        await watcher;

        Assert.True(status.Succeeded, status.Message);
        Assert.False(overlap, "the focuser moved during an exposure");
        // The 7-point sweep: each move after the first started while the previous frame was still downloading.
        Assert.Equal(6, status.Pipelined);
        Assert.InRange(status.BestPosition!.Value, 25_000 - 200, 25_000 + 200);
    }

    [Fact]
    public async Task CancellingReturnsToTheStart()
    {
        var (equipment, autofocus) = await SetUpAsync();
        autofocus.UpdateSettings(new AutofocusSettings(StepSize: 300, StepsPerSide: 4, ExposureSeconds: 0.5));
        var start = equipment.Focuser!.Position;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var status = await autofocus.RunAsync(cts.Token);

        Assert.False(status.Succeeded);
        Assert.Equal("Cancelled", status.Message);
        Assert.Equal(start, equipment.Focuser.Position);
    }

    [Theory]
    [InlineData(3.0, 2.0, true)]   // better
    [InlineData(3.0, 3.1, true)]   // within seeing noise
    [InlineData(3.0, 3.3, false)]  // clearly worse: go back
    [InlineData(3.0, null, false)] // no stars at the result
    [InlineData(null, 2.5, true)]  // nothing to compare with
    public void JudgesTheResultAgainstTheStart(double? start, double? final, bool accepted) =>
        Assert.Equal(accepted, AutofocusService.IsImprovement(start, final));

    [Fact]
    public async Task AFailedRunLeavesTheFocuserAtTheSharpestPositionMeasured()
    {
        // Steps far too small for this optics: the HFRs barely change, so no usable V (best focus is way outside
        // the range). The focuser should still end at the sharpest point measured, not back at the start.
        var (equipment, autofocus) = await SetUpAsync();
        // Steady seeing: otherwise its frame-to-frame wander can, now and then, look like a V and the run succeeds.
        ((Nova.Core.Simulation.SimulatedCamera)equipment.Camera!).SeeingVariation = 0;
        autofocus.UpdateSettings(new AutofocusSettings(StepSize: 40, StepsPerSide: 3, ExposureSeconds: 0.5));
        var start = equipment.Focuser!.Position;

        var status = await autofocus.RunAsync(CancellationToken.None);

        Assert.False(status.Succeeded);
        var sharpest = status.Points.MinBy(p => p.Hfr)!;
        Assert.Equal(sharpest.Position, equipment.Focuser.Position);
        if (sharpest.Position != start) // (if the start was the sharpest, it simply stays there)
        {
            Assert.Equal(sharpest.Position, status.BestPosition);
            Assert.Contains("sharpest position measured", status.Message);
        }
        Assert.True(sharpest.Hfr <= status.Points.Single(p => p.Position == start).Hfr);
    }

    [Fact]
    public void ValidatesSettings()
    {
        Assert.NotNull(new AutofocusSettings(StepSize: 0).Validate());
        Assert.NotNull(new AutofocusSettings(StepsPerSide: 1).Validate());
        Assert.Null(new AutofocusSettings().Validate());
    }
}
