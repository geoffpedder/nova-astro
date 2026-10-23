using Nova.Core.Astronomy;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Nova.Core.Equipment;
using Nova.Indi;

namespace Nova.Tests;

public class IndiParserTests
{
    [Fact]
    public void ParsesNumberDefinition()
    {
        var msg = IndiParser.Parse(XElement.Parse("""
            <defNumberVector device="Telescope Simulator" name="EQUATORIAL_EOD_COORD" label="Eq. Coordinates"
                             group="Main Control" state="Idle" perm="rw" timeout="60">
              <defNumber name="RA" label="RA (hh:mm:ss)" format="%010.6m" min="0" max="24" step="0">
                5:35:17.3
              </defNumber>
              <defNumber name="DEC" label="DEC (dd:mm:ss)" format="%010.6m" min="-90" max="90" step="0">-5.39</defNumber>
            </defNumberVector>
            """));

        var def = Assert.IsType<IndiDefineMessage>(msg);
        Assert.Equal("Telescope Simulator", def.Property.Device);
        Assert.Equal(IndiPropertyType.Number, def.Property.Type);
        Assert.Equal(5 + 35 / 60.0 + 17.3 / 3600, def.Property["RA"]!.Number, 9);
        Assert.Equal(-5.39, def.Property["DEC"]!.Number, 9);
        Assert.Equal(-90, def.Property["DEC"]!.Min);
    }

    [Fact]
    public void ParsesSwitchUpdate()
    {
        var msg = IndiParser.Parse(XElement.Parse("""
            <setSwitchVector device="CCD Simulator" name="CONNECTION" state="Ok" message="Connected">
              <oneSwitch name="CONNECT">On</oneSwitch>
              <oneSwitch name="DISCONNECT">Off</oneSwitch>
            </setSwitchVector>
            """));

        var set = Assert.IsType<IndiSetMessage>(msg);
        Assert.Equal(IndiState.Ok, set.State);
        Assert.Equal("Connected", set.Message);
        Assert.Equal("On", set.Values[0].Text);
    }

    [Fact]
    public void DecodesCompressedBlob()
    {
        var payload = Encoding.ASCII.GetBytes("SIMPLE  =                    T");
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload);
        // INDI wraps base64 at 72 columns; make sure line breaks are tolerated.
        var base64 = string.Join("\n", Convert.ToBase64String(compressed.ToArray()).Chunk(72).Select(c => new string(c)));

        var msg = IndiParser.Parse(XElement.Parse($"""
            <setBLOBVector device="CCD Simulator" name="CCD1" state="Ok">
              <oneBLOB name="CCD1" size="{payload.Length}" format=".fits.z">{base64}</oneBLOB>
            </setBLOBVector>
            """));

        var value = Assert.IsType<IndiSetMessage>(msg).Values.Single();
        Assert.Equal(".fits", value.BlobFormat);
        Assert.Equal(payload, value.BlobData);
    }

    [Fact]
    public void BuildsClientCommands()
    {
        var xml = IndiParser.NewNumbers("CCD Simulator", "CCD_EXPOSURE", [KeyValuePair.Create("CCD_EXPOSURE_VALUE", 2.5)])
            .ToString(SaveOptions.DisableFormatting);
        Assert.Equal("""<newNumberVector device="CCD Simulator" name="CCD_EXPOSURE"><oneNumber name="CCD_EXPOSURE_VALUE">2.5</oneNumber></newNumberVector>""", xml);
    }
}

/// <summary>Runs the real client and provider against a scripted fake indiserver on a local socket.</summary>
public class IndiClientTests
{
    private static readonly string[] Definitions =
    [
        """<defTextVector device="Telescope Simulator" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
        """<defSwitchVector device="Telescope Simulator" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">Off</defSwitch><defSwitch name="DISCONNECT">On</defSwitch></defSwitchVector>""",
        """<defTextVector device="CCD Simulator" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">6</defText></defTextVector>""",
    ];

    [Fact]
    public async Task DiscoversDevicesAndConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new FakeIndiServer(Definitions, received =>
            received.Contains("name=\"CONNECTION\"") && received.Contains(">On<")
                // Real drivers answer a connect request by flipping the switch and setting state Ok.
                ? ["""<setSwitchVector device="Telescope Simulator" name="CONNECTION" state="Ok" message="Telescope connected"><oneSwitch name="CONNECT">On</oneSwitch><oneSwitch name="DISCONNECT">Off</oneSwitch></setSwitchVector>"""]
                : []);

        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        var messages = new List<string>();
        provider.Message += (_, m) => messages.Add(m);
        await provider.ConnectAsync(cts.Token);

        Assert.Equal(["CCD Simulator", "Telescope Simulator"], provider.Devices.Select(d => d.Name));
        Assert.Equal("CCD Simulator", provider.GetCamera("CCD Simulator")?.Name);
        Assert.Null(provider.GetCamera("Telescope Simulator")); // a mount is not a camera
        Assert.Null(provider.GetCamera("No Such Device"));
        Assert.Equal([DeviceKind.Camera, DeviceKind.Guider], provider.Devices[0].Kinds);

        var mount = Assert.IsAssignableFrom<IMount>(provider.GetMount("Telescope Simulator"));
        Assert.False(mount.IsConnected);
        await mount.ConnectAsync(cts.Token);
        Assert.True(mount.IsConnected);
        // The driver's message arrives with the same update but is passed on separately: give it a moment.
        for (var i = 0; i < 40 && !messages.Contains("[Telescope Simulator] Telescope connected"); i++) await Task.Delay(50, cts.Token);
        Assert.Contains("[Telescope Simulator] Telescope connected", messages);
    }

    [Fact]
    public async Task FindsTheMountOnAnotherSerialPortWhenItsOwnFails()
    {
        // The driver expects /dev/ttyUSB0, but the AM3 came up as /dev/ttyACM0 after the driver started: connecting
        // fails until the ports are scanned and the right one picked, as "Scan Ports" does by hand.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = "/dev/ttyUSB0";
        var scanned = false;
        string[] definitions =
        [
            """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">Off</defSwitch><defSwitch name="DISCONNECT">On</defSwitch></defSwitchVector>""",
            """<defTextVector device="ZWO AM5" name="DEVICE_PORT" state="Idle" perm="rw"><defText name="PORT">/dev/ttyUSB0</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="DEVICE_PORT_SCAN" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="SCAN_PORT">Off</defSwitch></defSwitchVector>""",
            """<defSwitchVector device="ZWO AM5" name="SYSTEM_PORTS" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="/dev/ttyS0">Off</defSwitch></defSwitchVector>""",
        ];
        using var server = new FakeIndiServer(definitions, line =>
        {
            if (line.Contains("name=\"DEVICE_PORT_SCAN\""))
            {
                scanned = true;
                return ["""<defSwitchVector device="ZWO AM5" name="SYSTEM_PORTS" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="/dev/ttyS0">Off</defSwitch><defSwitch name="/dev/ttyACM0">Off</defSwitch></defSwitchVector>"""];
            }
            if (line.Contains("name=\"DEVICE_PORT\""))
            {
                port = System.Text.RegularExpressions.Regex.Match(line, @">(/dev/[^<]+)<").Groups[1].Value;
                return [$"""<setTextVector device="ZWO AM5" name="DEVICE_PORT" state="Ok"><oneText name="PORT">{port}</oneText></setTextVector>"""];
            }
            if (line.Contains("name=\"CONNECTION\"") && line.Contains(">On<"))
                return port == "/dev/ttyACM0"
                    ? ["""<setSwitchVector device="ZWO AM5" name="CONNECTION" state="Ok"><oneSwitch name="CONNECT">On</oneSwitch><oneSwitch name="DISCONNECT">Off</oneSwitch></setSwitchVector>"""]
                    : ["""<setSwitchVector device="ZWO AM5" name="CONNECTION" state="Alert" message="Failed to connect to port"><oneSwitch name="CONNECT">Off</oneSwitch><oneSwitch name="DISCONNECT">On</oneSwitch></setSwitchVector>"""];
            return [];
        });

        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        var messages = new List<string>();
        provider.Message += (_, m) => messages.Add(m);
        await provider.ConnectAsync(cts.Token);
        for (var i = 0; i < 40 && provider.GetMount("ZWO AM5") is null; i++) await Task.Delay(25, cts.Token);
        var mount = provider.GetMount("ZWO AM5")!;

        await mount.ConnectAsync(cts.Token);
        Assert.True(mount.IsConnected);
        Assert.True(scanned);
        Assert.Equal("/dev/ttyACM0", port);
        Assert.Contains("[ZWO AM5] Not on its usual port: found it on /dev/ttyACM0", messages);
    }

    [Fact]
    public async Task StillFailsWhenNoPortWorks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string[] definitions =
        [
            """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">Off</defSwitch><defSwitch name="DISCONNECT">On</defSwitch></defSwitchVector>""",
            """<defTextVector device="ZWO AM5" name="DEVICE_PORT" state="Idle" perm="rw"><defText name="PORT">/dev/ttyUSB0</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="DEVICE_PORT_SCAN" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="SCAN_PORT">Off</defSwitch></defSwitchVector>""",
            """<defSwitchVector device="ZWO AM5" name="SYSTEM_PORTS" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="/dev/ttyS0">Off</defSwitch></defSwitchVector>""",
        ];
        using var server = new FakeIndiServer(definitions, line =>
            line.Contains("name=\"CONNECTION\"") && line.Contains(">On<")
                ? ["""<setSwitchVector device="ZWO AM5" name="CONNECTION" state="Alert"><oneSwitch name="CONNECT">Off</oneSwitch><oneSwitch name="DISCONNECT">On</oneSwitch></setSwitchVector>"""]
                : []);
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        for (var i = 0; i < 40 && provider.GetMount("ZWO AM5") is null; i++) await Task.Delay(25, cts.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetMount("ZWO AM5")!.ConnectAsync(cts.Token));
        Assert.Contains("failed to connect", error.Message);
    }

    [Fact]
    public async Task SendsGuidePulsesToTheMountAndAnSt4Camera()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string[] guider(string device) =>
        [
            $$"""<defNumberVector device="{{device}}" name="TELESCOPE_TIMED_GUIDE_NS" state="Idle" perm="rw"><defNumber name="TIMED_GUIDE_N" format="%g" min="0" max="60000" step="1">0</defNumber><defNumber name="TIMED_GUIDE_S" format="%g" min="0" max="60000" step="1">0</defNumber></defNumberVector>""",
            $$"""<defNumberVector device="{{device}}" name="TELESCOPE_TIMED_GUIDE_WE" state="Idle" perm="rw"><defNumber name="TIMED_GUIDE_W" format="%g" min="0" max="60000" step="1">0</defNumber><defNumber name="TIMED_GUIDE_E" format="%g" min="0" max="60000" step="1">0</defNumber></defNumberVector>""",
        ];
        string[] definitions =
        [
            """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO AM5" name="GUIDE_RATE" state="Ok" perm="rw"><defNumber name="GUIDE_RATE_WE" format="%g" min="0.1" max="0.9" step="0.1">0.5</defNumber><defNumber name="GUIDE_RATE_NS" format="%g" min="0.1" max="0.9" step="0.1">0.5</defNumber></defNumberVector>""",
            .. guider("ZWO AM5"),
            """<defTextVector device="ZWO CCD ASI120MM Mini" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">6</defText></defTextVector>""",
            """<defSwitchVector device="ZWO CCD ASI120MM Mini" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            .. guider("ZWO CCD ASI120MM Mini"),
        ];
        // The driver goes Busy for the pulse, then Ok.
        using var server = new FakeIndiServer(definitions, line =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "device=\"([^\"]+)\" name=\"(TELESCOPE_TIMED_GUIDE_(?:NS|WE))\"");
            if (!match.Success) return [];
            var (device, property) = (match.Groups[1].Value, match.Groups[2].Value);
            return [$"""<setNumberVector device="{device}" name="{property}" state="Busy"></setNumberVector>""",
                    $"""<setNumberVector device="{device}" name="{property}" state="Ok"></setNumberVector>"""];
        });
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        var mount = Assert.IsAssignableFrom<IGuideOutput>(provider.GetMount("ZWO AM5"));
        Assert.True(mount.CanPulseGuide);
        Assert.Equal(0.5, mount.GuideRateSidereal);
        await mount.PulseAsync(GuideDirection.North, TimeSpan.FromMilliseconds(250), cts.Token);
        await mount.PulseAsync(GuideDirection.West, TimeSpan.FromMilliseconds(1200), cts.Token);

        var camera = Assert.IsAssignableFrom<IGuideOutput>(provider.GetCamera("ZWO CCD ASI120MM Mini"));
        Assert.True(camera.CanPulseGuide);
        await camera.PulseAsync(GuideDirection.East, TimeSpan.FromMilliseconds(80), cts.Token);

        var sent = await WaitForLinesAsync(server, 3);
        Assert.Contains(sent, l => l.Contains("device=\"ZWO AM5\"") && l.Contains("TELESCOPE_TIMED_GUIDE_NS")
                                   && l.Contains("<oneNumber name=\"TIMED_GUIDE_N\">250</oneNumber>") && l.Contains("<oneNumber name=\"TIMED_GUIDE_S\">0</oneNumber>"));
        Assert.Contains(sent, l => l.Contains("TELESCOPE_TIMED_GUIDE_WE") && l.Contains("<oneNumber name=\"TIMED_GUIDE_W\">1200</oneNumber>"));
        Assert.Contains(sent, l => l.Contains("device=\"ZWO CCD ASI120MM Mini\"") && l.Contains("<oneNumber name=\"TIMED_GUIDE_E\">80</oneNumber>"));
    }

    /// <summary>A connected ZWO-style camera: gain and offset live in CCD_CONTROLS, not CCD_GAIN.</summary>
    private static readonly string[] AsiCamera =
    [
        """<defTextVector device="ZWO CCD ASI2600MC Pro" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">6</defText></defTextVector>""",
        """<defSwitchVector device="ZWO CCD ASI2600MC Pro" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
        """<defNumberVector device="ZWO CCD ASI2600MC Pro" name="CCD_TEMPERATURE" group="Main Control" state="Busy" perm="rw"><defNumber name="CCD_TEMPERATURE_VALUE" format="%5.2f" min="-50" max="50" step="0">-4.5</defNumber></defNumberVector>""",
        """<defSwitchVector device="ZWO CCD ASI2600MC Pro" name="CCD_COOLER" state="Ok" perm="wo" rule="OneOfMany"><defSwitch name="COOLER_ON">On</defSwitch><defSwitch name="COOLER_OFF">Off</defSwitch></defSwitchVector>""",
        """<defNumberVector device="ZWO CCD ASI2600MC Pro" name="CCD_COOLER_POWER" state="Ok" perm="ro"><defNumber name="CCD_COOLER_VALUE" format="%+06.2f" min="0" max="100" step="1">42</defNumber></defNumberVector>""",
        """<defNumberVector device="ZWO CCD ASI2600MC Pro" name="CCD_CONTROLS" group="Controls" state="Ok" perm="rw"><defNumber name="Gain" format="%g" min="0" max="700" step="1">100</defNumber><defNumber name="Offset" format="%g" min="0" max="80" step="1">50</defNumber><defNumber name="AntiDewHeater" format="%g" min="0" max="1" step="1">0</defNumber></defNumberVector>""",
        """<defNumberVector device="ZWO CCD ASI2600MC Pro" name="CCD_BINNING" state="Ok" perm="rw"><defNumber name="HOR_BIN" format="%2.0f" min="1" max="4" step="1">1</defNumber><defNumber name="VER_BIN" format="%2.0f" min="1" max="4" step="1">1</defNumber></defNumberVector>""",
        """<defSwitchVector device="ZWO CCD ASI2600MC Pro" name="CCD_FRAME_TYPE" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="FRAME_LIGHT">Off</defSwitch><defSwitch name="FRAME_BIAS">Off</defSwitch><defSwitch name="FRAME_DARK">On</defSwitch><defSwitch name="FRAME_FLAT">Off</defSwitch></defSwitchVector>""",
        """<defTextVector device="ZWO CCD ASI2600MC Pro" name="ZWO CCD ASI2600MC ProICE_PORT" group="Connection" state="Idle" perm="rw"><defText name="PORT">/dev/ttyUSB0</defText></defTextVector>""",
        """<defNumberVector device="ZWO CCD ASI2600MC Pro" name="TARGET" group="Main Control" state="Idle" perm="rw"><defNumber name="RA" format="%010.6m" min="0" max="24" step="0">5.588138</defNumber><defNumber name="DEC" format="%010.6m" min="-90" max="90" step="0">-5.391111</defNumber></defNumberVector>""",
    ];

    private const string Asi = "ZWO CCD ASI2600MC Pro";

    [Fact]
    public async Task ReadsAndAppliesAsiCameraControls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new FakeIndiServer(AsiCamera, _ => []);
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        var camera = provider.GetCamera(Asi)!;
        var controls = camera.Controls;
        Assert.Equal(new CoolerStatus(true, true, -4.5, null, 42), controls.Cooler);
        Assert.Equal(new NumberSetting(100, 0, 700, 1), controls.Gain);
        Assert.Equal(50, controls.Offset?.Value);
        Assert.Equal(4, controls.Binning?.Max);
        Assert.Equal(FrameType.Dark, controls.FrameType);
        Assert.False(controls.DewHeater);

        await camera.ApplyAsync(new CameraSettingsUpdate(Gain: 120, Binning: 2, TargetTemperature: -10, FrameType: FrameType.Light, DewHeater: true), cts.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettingsUpdate(Gain: 9000), cts.Token));

        var sent = await WaitForLinesAsync(server, 6);
        Assert.Contains(sent, l => l.Contains("name=\"CCD_CONTROLS\"") && l.Contains("<oneNumber name=\"Gain\">120</oneNumber>"));
        Assert.Contains(sent, l => l.Contains("name=\"CCD_CONTROLS\"") && l.Contains("<oneNumber name=\"AntiDewHeater\">1</oneNumber>"));
        Assert.Contains(sent, l => l.Contains("name=\"CCD_BINNING\"") && l.Contains("\"HOR_BIN\">2<") && l.Contains("\"VER_BIN\">2<"));
        Assert.Contains(sent, l => l.Contains("name=\"CCD_TEMPERATURE\"") && l.Contains(">-10<"));
        Assert.Contains(sent, l => l.Contains("name=\"CCD_FRAME_TYPE\"") && l.Contains("\"FRAME_LIGHT\">On<"));
        Assert.Equal(-10, camera.Controls.Cooler?.TargetTemperature);
    }

    [Fact]
    public async Task GivesAZwoImagingCameraGain100AndOffset50WhenItConnects()
    {
        // Nothing chosen for the camera: NOVA's own default for ZWO (the driver starts at 200).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new FakeIndiServer(AsiCamera, _ => []);
        var config = Path.Combine(Path.GetTempPath(), "nova-zwo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new Nova.Server.Services.NovaOptions
            {
                Backend = "Indi", IndiHost = "127.0.0.1", IndiPort = server.Port, ConfigDirectory = config,
            });
            var log = new Nova.Server.Services.ActivityLog(new NullHubContext(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Nova.Server.Services.ActivityLog>.Instance);
            var equipment = new Nova.Server.Services.EquipmentService(options, log);
            var optics = new Nova.Server.Services.OpticsService(equipment, options, log);
            await equipment.ConnectAsync(cts.Token);

            List<string> controls = [];
            for (var i = 0; i < 100 && controls.Count < 2; i++)
            {
                await Task.Delay(50, cts.Token);
                controls = server.Received.Where(l => l.Contains("name=\"CCD_CONTROLS\"")).ToList();
            }
            Assert.Contains(controls, l => l.Contains("<oneNumber name=\"Gain\">100</oneNumber>"));
            Assert.Contains(controls, l => l.Contains("<oneNumber name=\"Offset\">50</oneNumber>"));
            Assert.Equal(2, controls.Count); // once each, not again
            var info = optics.All.Single(c => c.Camera == Asi);
            Assert.Equal((100.0, 50.0, true), (info.DefaultGain!.Value, info.DefaultOffset!.Value, info.BuiltInDefaults));

            // Chosen for this camera: that wins.
            optics.SetConnectDefaults(Asi, 0, 30);
            info = optics.All.Single(c => c.Camera == Asi);
            Assert.Equal((0.0, 30.0, false), (info.DefaultGain!.Value, info.DefaultOffset!.Value, info.BuiltInDefaults));
        }
        finally
        {
            if (Directory.Exists(config)) Directory.Delete(config, recursive: true);
        }
    }

    [Fact]
    public async Task ExposesAndSetsRawDriverProperties()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new FakeIndiServer(AsiCamera, _ => []);
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var settings = provider.Settings;

        var properties = settings.GetProperties(Asi);
        var target = properties.Single(p => p.Name == "TARGET");
        Assert.Equal("5:35:17", target.Elements[0].Display); // %010.6m = h:mm:ss
        Assert.Equal("-5:23:28", target.Elements[1].Display);
        Assert.Equal("Connection", properties.Single(p => p.Name == "ZWO CCD ASI2600MC ProICE_PORT").Group);

        await settings.SetPropertyAsync(Asi, "ZWO CCD ASI2600MC ProICE_PORT", new Dictionary<string, string> { ["PORT"] = "/dev/ttyACM0" }, cts.Token);
        // Only DEC given: the whole vector is still sent, with RA's current value.
        await settings.SetPropertyAsync(Asi, "TARGET", new Dictionary<string, string> { ["DEC"] = "+10:30" }, cts.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetPropertyAsync(
            Asi, "CCD_COOLER_POWER", new Dictionary<string, string> { ["CCD_COOLER_VALUE"] = "5" }, cts.Token));

        var sent = await WaitForLinesAsync(server, 3);
        Assert.Contains(sent, l => l.Contains("<oneText name=\"PORT\">/dev/ttyACM0</oneText>"));
        Assert.Contains(sent, l => l.Contains("\"RA\">5.588138<") && l.Contains("\"DEC\">10.5<"));
    }

    // A ZWO mount as the user's AM3 was found: stops tracking at the meridian, limit 0°, its own flip on.
    private static readonly string[] ZwoMount =
    [
        """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
        """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
        """<defSwitchVector device="ZWO AM5" name="MERIDIAN_FLIP" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="INDI_ENABLED">On</defSwitch><defSwitch name="INDI_DISABLED">Off</defSwitch></defSwitchVector>""",
        """<defSwitchVector device="ZWO AM5" name="POST_MERIDIAN_TRACK" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="TRACK">Off</defSwitch><defSwitch name="STOP">On</defSwitch></defSwitchVector>""",
        """<defNumberVector device="ZWO AM5" name="MERIDIAN_LIMIT" state="Ok" perm="rw"><defNumber name="LIMIT" format="%.f" min="-15" max="15" step="1">0</defNumber></defNumberVector>""",
    ];

    [Fact]
    public async Task SetsAZwoMountToTrackPastTheMeridianForNovasFlips()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Answers like the driver: the new settings, Ok.
        using var server = new FakeIndiServer(ZwoMount, line =>
            line.Contains("\"POST_MERIDIAN_TRACK\"")
                ? ["""<setSwitchVector device="ZWO AM5" name="POST_MERIDIAN_TRACK" state="Ok"><oneSwitch name="TRACK">On</oneSwitch><oneSwitch name="STOP">Off</oneSwitch></setSwitchVector>"""]
            : line.Contains("\"MERIDIAN_LIMIT\"")
                ? ["""<setNumberVector device="ZWO AM5" name="MERIDIAN_LIMIT" state="Ok"><oneNumber name="LIMIT">8</oneNumber></setNumberVector>"""]
            : []);
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var mount = provider.GetMount("ZWO AM5")!;

        var changed = await mount.PrepareForMeridianFlipsAsync(8, cts.Token);
        Assert.Contains("Meridian Limit: 0° → 8°", changed);
        Assert.Contains("After Meridian: Stop → Track", changed);
        Assert.Contains("Meridian Flip (by the mount): Enabled → Disabled", changed);
        var sent = await WaitForLinesAsync(server, 3);
        Assert.Contains(sent, l => l.Contains("name=\"POST_MERIDIAN_TRACK\"") && l.Contains("\"TRACK\">On<"));
        Assert.Contains(sent, l => l.Contains("name=\"MERIDIAN_LIMIT\"") && l.Contains(">8<"));
        Assert.Contains(sent, l => l.Contains("name=\"MERIDIAN_FLIP\"") && l.Contains("\"INDI_DISABLED\">On<"));
    }

    [Fact]
    public async Task ExplainsASlewRefusedByTheAltitudeLimitOrUnderThePole()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var now = DateTime.UtcNow;
        string[] mount =
        [
            .. ZwoMount,
            """<defNumberVector device="ZWO AM5" name="GEOGRAPHIC_COORD" state="Ok" perm="rw"><defNumber name="LAT" format="%g" min="-90" max="90" step="0">48.52</defNumber><defNumber name="LONG" format="%g" min="0" max="360" step="0">236.58</defNumber><defNumber name="ELEV" format="%g" min="-200" max="10000" step="0">300</defNumber></defNumberVector>""",
            $"""<defTextVector device="ZWO AM5" name="TIME_UTC" state="Ok" perm="rw"><defText name="UTC">{now:yyyy-MM-ddTHH:mm:ss}</defText><defText name="OFFSET">-7</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="ALTITUDE_LIMIT_CONTROL" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="ENABLE">On</defSwitch><defSwitch name="DISABLE">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO AM5" name="ALTITUDE_LOWER_LIMIT" state="Ok" perm="rw"><defNumber name="LOWER_LIMIT" format="%.f" min="0" max="30" step="1">30</defNumber></defNumberVector>""",
            """<defNumberVector device="ZWO AM5" name="ALTITUDE_UPPER_LIMIT" state="Ok" perm="rw"><defNumber name="UPPER_LIMIT" format="%.f" min="60" max="90" step="1">90</defNumber></defNumberVector>""",
        ];
        using var server = new FakeIndiServer(mount, _ => []);
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var am5 = (IndiMount)provider.GetMount("ZWO AM5")!;
        var site = new GeoLocation(48.52, -123.42, 0);
        var lst = Horizon.LocalSiderealDegrees(now, site.Longitude) / 15;

        // On the meridian, 20° up (Dec = latitude − 70): under the 30° limit.
        var low = new EquatorialCoordinates(lst, 48.52 - 70);
        Assert.Contains("below the mount's lower altitude limit of 30°", am5.RefusalReason(low, ["Slew failed (-1)."], now));

        // 9 h past the meridian at Dec +75 (under the pole, about 37° up): the counterweight-up case.
        var underPole = new EquatorialCoordinates(((lst - 9) % 24 + 24) % 24, 75);
        Assert.True(Horizon.AltAz(Horizon.ToVector(underPole, now, site)).Altitude > 30);
        Assert.Contains("under the pole", am5.RefusalReason(underPole, [], now));
    }

    private static async Task<List<string>> WaitForLinesAsync(FakeIndiServer server, int count)
    {
        for (var i = 0; i < 100 && server.Received.Count < count; i++) await Task.Delay(50);
        return server.Received.ToList();
    }

    private sealed class FakeIndiServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Every line the client sent, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Received { get; } = new();

        public FakeIndiServer(IEnumerable<string> onConnect, Func<string, IEnumerable<string>> onLine)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = socket.GetStream();
                var reader = new StreamReader(stream);
                async Task Send(string xml) => await stream.WriteAsync(Encoding.UTF8.GetBytes(xml + "\n"), _cts.Token);

                await reader.ReadLineAsync(_cts.Token); // getProperties
                foreach (var xml in onConnect) await Send(xml);
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    Received.Enqueue(line);
                    foreach (var reply in onLine(line)) await Send(reply);
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}

public class IndiNumberFormatTests
{
    [Theory]
    [InlineData(5.588138, "%010.6m", "5:35:17")]
    [InlineData(-5.391111, "%09.6m", "-5:23:28")]
    [InlineData(5.588138, "%011.8m", "5:35:17.3")]
    [InlineData(5.588138, "%012.9m", "5:35:17.30")]
    [InlineData(5.588138, "%8.5m", "5:35.3")]
    [InlineData(5.999999, "%010.6m", "6:00:00")]
    [InlineData(12.25, "%6.3m", "12:15")]
    [InlineData(-4.5, "%5.2f", "-4.50")]
    [InlineData(1234.4, "%d", "1234")]
    [InlineData(0.000123, "%g", "0.000123")]
    [InlineData(3.0, null, "3")]
    public void FormatsLikeTheDriverAsks(double value, string? format, string expected) =>
        Assert.Equal(expected, IndiNumber.Format(value, format));
}

public class IndiFrameReaderTests
{
    private const string Stream =
        "<?xml version=\"1.0\"?>\n" +
        "<getProperties version=\"1.7\"/>\n" +
        "<message device=\"x\" message=\"a > b, &quot;quoted&quot; />\"/>" +
        "<defTextVector device=\"x\" name=\"T\"><defText name=\"A\">1 > 0</defText><defText name=\"B\"/></defTextVector>\n" +
        "<setNumberVector device='x' name='N'>\n<oneNumber name='V'>2</oneNumber>\n</setNumberVector>";

    private static readonly string[] Expected =
    [
        "<getProperties version=\"1.7\"/>",
        "<message device=\"x\" message=\"a > b, &quot;quoted&quot; />\"/>",
        "<defTextVector device=\"x\" name=\"T\"><defText name=\"A\">1 > 0</defText><defText name=\"B\"/></defTextVector>",
        "<setNumberVector device='x' name='N'>\n<oneNumber name='V'>2</oneNumber>\n</setNumberVector>",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task SplitsElementsRegardlessOfChunking(int chunkSize)
    {
        var reader = new IndiFrameReader(new ChunkedStream(Encoding.UTF8.GetBytes(Stream), chunkSize));
        var frames = new List<string>();
        while (await reader.ReadElementAsync(CancellationToken.None) is { } frame) frames.Add(Encoding.UTF8.GetString(frame));
        Assert.Equal(Expected, frames);
    }

    /// <summary>Returns at most chunkSize bytes per read, like a slow socket.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);
    }
}

public class HeavyDutyModeTests
{
    [Theory]
    [InlineData("On", "Off", "720x")]
    [InlineData("Off", "On", "1440x")]
    public async Task OffersNoSpeedAboveWhatHeavyDutyModeAllows(string enabled, string disabled, string fastest)
    {
        string[] mount =
        [
            """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defSwitchVector device="ZWO AM5" name="TELESCOPE_SLEW_RATE" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="0" label="0.25x">Off</defSwitch><defSwitch name="7" label="60x">Off</defSwitch><defSwitch name="8" label="720x">Off</defSwitch><defSwitch name="9" label="1440x">On</defSwitch></defSwitchVector>""",
            $"""<defSwitchVector device="ZWO AM5" name="HEAVY_DUTY_MODE" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="INDI_ENABLED">{enabled}</defSwitch><defSwitch name="INDI_DISABLED">{disabled}</defSwitch></defSwitchVector>""",
        ];
        using var server = new ScriptedServer(mount, _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var am5 = provider.GetMount("ZWO AM5")!;

        Assert.Equal(fastest, am5.SlewRates[^1].Label);
        Assert.Contains(am5.SlewRates, r => r.Label == "60x");
    }
}


public class RaAxisTurnTests
{
    // A ZWO mount whose reported position never changes: at home it says Dec 90° and an RA that doesn't follow the axis.
    private static string[] Mount(double dec) =>
    [
        """<defTextVector device="ZWO AM5" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
        """<defSwitchVector device="ZWO AM5" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
        $"""<defNumberVector device="ZWO AM5" name="EQUATORIAL_EOD_COORD" state="Ok" perm="rw"><defNumber name="RA" format="%010.6m" min="0" max="24" step="0">6.1</defNumber><defNumber name="DEC" format="%010.6m" min="-90" max="90" step="0">{dec.ToString(System.Globalization.CultureInfo.InvariantCulture)}</defNumber></defNumberVector>""",
        """<defSwitchVector device="ZWO AM5" name="TELESCOPE_MOTION_WE" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="MOTION_WEST">Off</defSwitch><defSwitch name="MOTION_EAST">Off</defSwitch></defSwitchVector>""",
        """<defSwitchVector device="ZWO AM5" name="TELESCOPE_MOTION_NS" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="MOTION_NORTH">Off</defSwitch><defSwitch name="MOTION_SOUTH">Off</defSwitch></defSwitchVector>""",
        """<defSwitchVector device="ZWO AM5" name="TELESCOPE_SLEW_RATE" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="7" label="60x">Off</defSwitch><defSwitch name="8" label="720x">Off</defSwitch><defSwitch name="9" label="1440x">On</defSwitch></defSwitchVector>""",
    ];

    [Fact]
    public async Task WontTurnRaAtThePole()
    {
        // At the pole the reported RA can't measure the turn: refuse before anything moves.
        using var server = new ScriptedServer(Mount(90), _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var mount = provider.GetMount("ZWO AM5")!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveRaAxisAsync(90, cts.Token));
        await Task.Delay(300);
        Assert.DoesNotContain(server.Received, l => l.Contains("MOTION_WEST") && l.Contains("On"));
    }

    [Fact]
    public async Task AMountWhoseRaDoesntFollowIsStoppedAtOnce()
    {
        // The driver's motion runs until stopped: if the reported RA doesn't change, stop within seconds.
        using var server = new ScriptedServer(Mount(60), _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var mount = provider.GetMount("ZWO AM5")!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveRaAxisAsync(90, cts.Token));
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 4);
        await Task.Delay(300);
        Assert.Contains(server.Received, l => l.Contains("TELESCOPE_MOTION_WE") && l.Contains("Off"));
    }

    [Fact]
    public async Task SteppingOffThePoleStopsIfDecDoesntChange()
    {
        using var server = new ScriptedServer(Mount(90), _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var mount = provider.GetMount("ZWO AM5")!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Nova.Server.Services.PolarAlignmentService.StepOffThePoleAsync(mount, new GeoLocation(51.5, -0.1, 0), cts.Token));
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 4);
        await Task.Delay(300);
        Assert.Contains(server.Received, l => l.Contains("MOTION_SOUTH") && l.Contains("On"));
        Assert.Contains(server.Received, l => l.Contains("TELESCOPE_MOTION_NS") && l.Contains("Off"));
        Assert.Contains(server.Received, l => l.Contains("TELESCOPE_SLEW_RATE") && l.Contains("name=\"7\""));  // 60x
    }

    /// <summary>
    /// A ZWO mount that moves: while MOTION_WEST is on it reports RA falling at <paramref name="degreesPerSecond"/>,
    /// a reading every <paramref name="pollMs"/> (as drivers poll), after <paramref name="glitchAfter"/> seconds it
    /// can report one wrong reading (<paramref name="glitchDegrees"/> off), and it really stops when told.
    /// </summary>
    private sealed class MovingMount : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private double _ra = 6.0;
        private volatile bool _moving;
        public double TurnedDegrees => Math.Abs(6.0 - _ra) * 15;

        public MovingMount(double degreesPerSecond, int pollMs, double glitchAfter = -1, double glitchDegrees = 0)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = socket.GetStream();
                var reader = new StreamReader(stream);
                var gate = new SemaphoreSlim(1, 1);
                async Task Send(string line)
                {
                    await gate.WaitAsync(_cts.Token);
                    try { await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), _cts.Token); }
                    finally { gate.Release(); }
                }
                string Coords(double ra) => $"""<setNumberVector device="ZWO AM5" name="EQUATORIAL_EOD_COORD" state="Ok"><oneNumber name="RA">{ra.ToString(System.Globalization.CultureInfo.InvariantCulture)}</oneNumber><oneNumber name="DEC">60</oneNumber></setNumberVector>""";
                await reader.ReadLineAsync(_cts.Token);
                foreach (var line in Mount(60)) await Send(line);
                _ = Task.Run(async () =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var last = clock.Elapsed;
                    var glitched = false;
                    TimeSpan? movingSince = null;
                    var bucket = 0L;
                    while (!_cts.IsCancellationRequested)
                    {
                        await Task.Delay(20, _cts.Token);
                        var now = clock.Elapsed;
                        if (_moving) _ra -= degreesPerSecond / 15 * (now - last).TotalSeconds;
                        if (_moving) movingSince ??= now;
                        last = now;
                        if ((long)(now.TotalMilliseconds / pollMs) is var b && b != bucket)
                        {
                            bucket = b;
                            var reported = _ra;
                            if (glitchAfter >= 0 && !glitched && movingSince is { } since && (now - since).TotalSeconds > glitchAfter) { reported -= glitchDegrees / 15; glitched = true; }
                            await Send(Coords(reported));
                        }
                    }
                });
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.Contains("MOTION_WEST") && line.Contains(">On")) _moving = true;
                    if (line.Contains("TELESCOPE_MOTION_WE") && line.Contains("Off")) _moving = false;
                    if (line.Contains("oneSwitch") && line.Contains("MOTION_WEST") && line.Contains("Off")) _moving = false;
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static async Task<(IMount Mount, IndiEquipmentProvider Provider)> ConnectAsync(MovingMount fake)
    {
        var provider = new IndiEquipmentProvider("127.0.0.1", fake.Port);
        await provider.ConnectAsync(CancellationToken.None);
        for (var i = 0; i < 100 && provider.GetMount("ZWO AM5")?.Coordinates.RaHours is not > 0; i++) await Task.Delay(20);
        return (provider.GetMount("ZWO AM5")!, provider);
    }

    [Fact]
    public async Task StopsOnTheAngleDespiteTheReadingLagging()
    {
        // 4°/s with a reading only every second: watching the reading alone would overshoot by up to 4°.
        using var fake = new MovingMount(degreesPerSecond: 4, pollMs: 1000);
        var (mount, provider) = await ConnectAsync(fake);
        await using var _ = provider;
        await mount.MoveRaAxisAsync(20, CancellationToken.None);
        await Task.Delay(300);
        Assert.InRange(fake.TurnedDegrees, 18.5, 21.5);
    }

    [Fact]
    public async Task AReadingThatJumpsStopsTheMount()
    {
        using var fake = new MovingMount(degreesPerSecond: 2, pollMs: 250, glitchAfter: 2, glitchDegrees: 40);
        var (mount, provider) = await ConnectAsync(fake);
        await using var _ = provider;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveRaAxisAsync(90, CancellationToken.None));
        Assert.Contains("jumped", error.Message);
        await Task.Delay(300);
        Assert.InRange(fake.TurnedDegrees, 3, 7); // stopped about 2 s in, not at 90°
    }

    [Fact]
    public async Task AReadingThatGoesBackwardsStopsTheMount()
    {
        using var fake = new MovingMount(degreesPerSecond: 2, pollMs: 250, glitchAfter: 2, glitchDegrees: -3);
        var (mount, provider) = await ConnectAsync(fake);
        await using var _ = provider;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveRaAxisAsync(90, CancellationToken.None));
        Assert.True(error.Message.Contains("backwards"), error.Message);
        await Task.Delay(300);
        Assert.InRange(fake.TurnedDegrees, 3, 7);
    }
}
