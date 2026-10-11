using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class FocusOffsetTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(EquipmentService, AutofocusService, FocusOffsetService)> SetUpAsync()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var imaging = new ImagingService(equipment, new OpticsService(equipment, options, log), log, hub, options);
        var autofocus = new AutofocusService(equipment, imaging, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return (equipment, autofocus, new FocusOffsetService(equipment, autofocus, options, log));
    }

    [Fact]
    public async Task FilterChangesMoveTheFocuserByTheDifference()
    {
        var (equipment, _, offsets) = await SetUpAsync();
        var wheel = equipment.FilterWheel!;
        var focuser = equipment.Focuser!;
        offsets.Save(focuser.Name, true, "L", new Dictionary<string, int> { ["L"] = 0, ["R"] = 60, ["B"] = -50 });
        var start = focuser.Position;

        await offsets.ChangeFilterAsync(wheel, 2, CancellationToken.None); // L → R
        Assert.Equal(start + 60, focuser.Position);
        await offsets.ChangeFilterAsync(wheel, 4, CancellationToken.None); // R → B
        Assert.Equal(start - 50, focuser.Position);
        await offsets.ChangeFilterAsync(wheel, 5, CancellationToken.None); // B → Ha: no offset known, focus stays
        Assert.Equal(start - 50, focuser.Position);

        offsets.Save(focuser.Name, false, "L", new Dictionary<string, int> { ["L"] = 0, ["R"] = 60, ["B"] = -50 });
        await offsets.ChangeFilterAsync(wheel, 2, CancellationToken.None); // off: the wheel moves, the focuser doesn't
        Assert.Equal(2, wheel.Position);
        Assert.Equal(start - 50, focuser.Position);
    }

    [Fact]
    public async Task MeasuresEachFiltersOffsetFromTheReference()
    {
        var (equipment, autofocus, offsets) = await SetUpAsync();
        // Steady seeing, so each autofocus lands close to the simulator's real per-filter focus.
        ((Nova.Core.Simulation.SimulatedCamera)equipment.Camera!).SeeingVariation = 0;
        autofocus.UpdateSettings(new AutofocusSettings(StepSize: 300, StepsPerSide: 4, ExposureSeconds: 0.5));

        var status = await offsets.MeasureAndWaitAsync(CancellationToken.None, passes: 2);

        Assert.False(status.Running);
        // Two passes, each closed by the reference again.
        Assert.Equal(["L", "R", "G", "B", "Ha", "L", "R", "G", "B", "Ha", "L"], status.Results.Select(r => r.Filter));
        var info = offsets.For(equipment.Focuser!.Name)!;
        Assert.True(info.Enabled);
        Assert.Equal("L", info.Reference);
        // The simulator's filters focus at L 0, R +60, G 0, B -50, Ha +90 (SimulatedFilterWheel.FocusShifts).
        var expected = new Dictionary<string, int> { ["L"] = 0, ["R"] = 60, ["G"] = 0, ["B"] = -50, ["Ha"] = 90 };
        foreach (var (filter, offset) in expected)
            Assert.True(Math.Abs(info.Offsets[filter] - offset) <= 40, $"{filter}: measured {info.Offsets[filter]}, expected about {offset}");
    }
}
