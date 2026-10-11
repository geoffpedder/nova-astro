using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Indi;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class HeaderLinkTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task LinksIndiCameraAndClearsSimulatorDefaults()
    {
        string[] camera =
        [
            """<defTextVector device="ZWO CCD ASI2600MC Pro" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">2</defText></defTextVector>""",
            """<defTextVector device="ZWO CCD ASI2600MC Pro" name="ACTIVE_DEVICES" group="Options" state="Idle" perm="rw"><defText name="ACTIVE_TELESCOPE">Telescope Simulator</defText><defText name="ACTIVE_ROTATOR">Rotator Simulator</defText><defText name="ACTIVE_FOCUSER">Focuser Simulator</defText><defText name="ACTIVE_FILTER">CCD Simulator</defText><defText name="ACTIVE_SKYQUALITY">SQM</defText></defTextVector>""",
        ];
        using var server = new ScriptedServer(camera, _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        await provider.GetCamera("ZWO CCD ASI2600MC Pro")!.LinkDevicesAsync(
            new LinkedDevices("ZWO AM5", "ZWO EFW", null, null), cts.Token);

        for (var i = 0; i < 40 && !server.Received.Any(l => l.Contains("ACTIVE_DEVICES")); i++) await Task.Delay(50);
        var sent = server.Received.Single(l => l.Contains("ACTIVE_DEVICES"));
        Assert.Contains("<oneText name=\"ACTIVE_TELESCOPE\">ZWO AM5</oneText>", sent);
        Assert.Contains("<oneText name=\"ACTIVE_FILTER\">ZWO EFW</oneText>", sent);
        Assert.Contains("<oneText name=\"ACTIVE_FOCUSER\"></oneText>", sent); // cleared, not left on the simulator
        Assert.DoesNotContain("ACTIVE_SKYQUALITY", sent);                     // left alone
    }

    [Fact]
    public async Task FramesCarryHeadersFromTheChosenDevices()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var links = new CameraLinkService(equipment, log);

        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.FilterWheel!.MoveToAsync(5, CancellationToken.None); // Ha
        Assert.Equal(new LinkedDevices("Simulated Mount", "Simulated Filter Wheel", "Simulated Focuser", "Simulated Rotator"), links.Current);

        async Task<IReadOnlyDictionary<string, string>> Headers()
        {
            // Linking happens in the background right after connects; give it a moment.
            await Task.Delay(200);
            var frame = await equipment.Camera!.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None);
            return frame.Image.Headers;
        }

        var headers = await Headers();
        Assert.Equal("Ha", headers["FILTER"]);
        Assert.Equal("Simulated Mount", headers["TELESCOP"]);
        Assert.True(headers.ContainsKey("OBJCTRA"));
        Assert.Equal("23800", headers["FOCUSPOS"]);
        Assert.Equal("90", headers["ROTATANG"]);

        // A device that's no longer connected stops being recorded.
        await equipment.FilterWheel!.DisconnectAsync(CancellationToken.None);
        // Any selection change re-links (re-selecting the wheel itself would just reconnect it).
        await equipment.SelectAsync(DeviceKind.Rotator, "Simulated Rotator", CancellationToken.None);
        headers = await Headers();
        Assert.False(headers.ContainsKey("FILTER"));
        Assert.Equal("Simulated Mount", headers["TELESCOP"]);
    }
}
