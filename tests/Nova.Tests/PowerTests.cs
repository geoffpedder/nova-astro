using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class PowerTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<(EquipmentService Equipment, SequencerService Sequencer, PowerService Power, List<string> Commands)> SetUpAsync(int exitCode = 0)
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"), Phd2AutoConnect = false,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var autofocus = new AutofocusService(equipment, imaging, log, options);
        var sites = new SiteService(equipment, options, log);
        var sequencer = new SequencerService(equipment, imaging, solver, autofocus, new GuiderService(options, log), sites, log, options);
        var polar = new PolarAlignmentService(equipment, imaging, solver, sites, log, options);
        var commands = new List<string>();
        var power = new PowerService(equipment, imaging, sequencer, autofocus, polar, log)
        {
            CheckProblem = () => null,
            Run = (file, args) =>
            {
                commands.Add($"{file} {args}");
                return Task.FromResult((exitCode, exitCode == 0 ? "" : "Access denied"));
            },
        };
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return (equipment, sequencer, power, commands);
    }

    [Fact]
    public async Task StopsTheSequenceParksCoolerOffThenShutsDown()
    {
        var (equipment, sequencer, power, commands) = await SetUpAsync();
        await equipment.Camera!.ApplyAsync(new CameraSettingsUpdate(CoolerOn: true, TargetTemperature: -10), CancellationToken.None);
        var plan = new Sequence("Long", null, new SequenceStart(CoolCamera: false, CenterTarget: false, Autofocus: false, StartGuiding: false),
            [new SequenceRow("L", 1, 100)], Dither: new SequenceDither(EveryFrames: 0),
            Autofocus: new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null), MeridianFlip: new SequenceMeridianFlip(Enabled: false));
        Assert.True(sequencer.Start(plan, out var error), error);
        for (var i = 0; i < 100 && sequencer.Status?.Step != "Exposing"; i++) await Task.Delay(20);

        Assert.Null(await power.ShutdownAsync(reboot: false, park: true, coolerOff: true, CancellationToken.None));

        for (var i = 0; i < 100 && sequencer.Running; i++) await Task.Delay(20);
        Assert.Equal("Aborted", sequencer.Status!.State);
        Assert.Equal(MountState.Parked, equipment.Mount!.State);
        Assert.False(equipment.Camera.Controls.Cooler!.On);
        Assert.Equal(["systemctl poweroff"], commands);
        Assert.Equal("Shutting down", power.Status.Pending);
    }

    [Fact]
    public async Task RestartsWithoutParkingWhenAsked()
    {
        var (equipment, _, power, commands) = await SetUpAsync();
        Assert.Null(await power.ShutdownAsync(reboot: true, park: false, coolerOff: false, CancellationToken.None));
        Assert.Equal(["systemctl reboot"], commands);
        Assert.NotEqual(MountState.Parked, equipment.Mount!.State);
    }

    [Fact]
    public async Task ReportsARefusalAndCanBeTriedAgain()
    {
        var (_, _, power, _) = await SetUpAsync(exitCode: 1);
        var error = await power.ShutdownAsync(reboot: true, park: false, coolerOff: false, CancellationToken.None);
        Assert.Contains("refused to restart", error);
        Assert.Contains("Access denied", error);
        Assert.Null(power.Status.Pending);
    }

    [Fact]
    public async Task OnlyWhereAllowed()
    {
        var (_, _, power, commands) = await SetUpAsync();
        power.CheckProblem = () => "not allowed here";
        Assert.Equal("not allowed here", await power.ShutdownAsync(reboot: false, park: true, coolerOff: true, CancellationToken.None));
        Assert.Empty(commands);
        Assert.False(power.Status.Available);
    }
}
