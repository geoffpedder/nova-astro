using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class SimulatorSwitchTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private static string Driver(string device, string exec, int interfaces) =>
        $"""<defTextVector device="{device}" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_EXEC">{exec}</defText><defText name="DRIVER_INTERFACE">{interfaces}</defText></defTextVector>""";

    // Alphabetically the simulators come first, so "automatic" would pick them if they weren't excluded.
    private static readonly string[] Devices =
    [
        Driver("CCD Simulator", "indi_simulator_ccd", 6),
        Driver("Telescope Simulator", "", 5), // no DRIVER_EXEC: recognised by its name
        Driver("ZWO AM5", "indi_lx200am5", 5),
        Driver("ZWO CCD ASI2600MC Pro", "indi_asi_ccd", 2),
    ];

    private (EquipmentService Equipment, SimulatorService Simulators) Create(int port)
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Indi", IndiHost = "127.0.0.1", IndiPort = port, ConfigDirectory = _config,
            IndiControlFifo = Path.Combine(_config, "no-such-pipe"),
        });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        return (equipment, new SimulatorService(equipment, options, log));
    }

    [Fact]
    public async Task SwitchingOffHidesSimulatorsFromListsAndAutomaticChoices()
    {
        using var server = new ScriptedServer(Devices, _ => [], skipFirstLine: true);
        var (equipment, simulators) = Create(server.Port);
        await equipment.EnsureProviderConnectedAsync(CancellationToken.None);

        Assert.True(simulators.Status.Applicable);
        Assert.False(simulators.Status.DriverControl); // no control pipe here: hide only
        Assert.Equal("CCD Simulator", equipment.Camera?.Name);
        Assert.Equal("Telescope Simulator", equipment.Mount?.Name);

        await simulators.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal(["ZWO AM5", "ZWO CCD ASI2600MC Pro"], equipment.VisibleDevices.Select(d => d.Name));
        Assert.Equal("ZWO CCD ASI2600MC Pro", equipment.Camera?.Name);
        Assert.Equal("ZWO AM5", equipment.Mount?.Name);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            equipment.SelectAsync(DeviceKind.Camera, "CCD Simulator", CancellationToken.None));

        await simulators.SetEnabledAsync(true, CancellationToken.None);
        Assert.Equal(4, equipment.VisibleDevices.Count);
    }

    [Fact]
    public async Task AChosenSimulatorCountsAsMissingWhileSwitchedOffAndTheSettingIsRemembered()
    {
        using (var server = new ScriptedServer(Devices, _ => [], skipFirstLine: true))
        {
            var (equipment, simulators) = Create(server.Port);
            await equipment.EnsureProviderConnectedAsync(CancellationToken.None);
            await equipment.SelectAsync(DeviceKind.Camera, "CCD Simulator", CancellationToken.None);
            await simulators.SetEnabledAsync(false, CancellationToken.None);
            Assert.Null(equipment.Camera); // not silently swapped for another camera
        }

        using var again = new ScriptedServer(Devices, _ => [], skipFirstLine: true);
        var (_, restarted) = Create(again.Port);
        Assert.False(restarted.Enabled);
    }

    [Fact]
    public async Task DoesNotApplyToTheBuiltInSimulator()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var simulators = new SimulatorService(equipment, options, log);

        Assert.False(simulators.Status.Applicable);
        await simulators.SetEnabledAsync(false, CancellationToken.None);
        Assert.Equal(equipment.Provider.Devices.Count, equipment.VisibleDevices.Count); // nothing hidden
    }
}
