using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.Simulation;
using Nova.Indi;

namespace Nova.Tests;

/// <summary>A scripted TCP peer: sends lines on connect, then replies to each received line.</summary>
internal sealed class ScriptedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    public ScriptedServer(IEnumerable<string> onConnect, Func<string, IEnumerable<string>> onLine, bool skipFirstLine = false)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            using var socket = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = socket.GetStream();
            var reader = new StreamReader(stream);
            async Task Send(string line) => await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), _cts.Token);

            if (skipFirstLine) await reader.ReadLineAsync(_cts.Token);
            foreach (var line in onConnect) await Send(line);
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                Received.Enqueue(line);
                foreach (var reply in onLine(line))
                {
                    if (reply.StartsWith("sleep:")) await Task.Delay(int.Parse(reply[6..]));
                    else await Send(reply);
                }
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public System.Collections.Concurrent.ConcurrentQueue<string> Received { get; } = new();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class Phd2ClientTests
{
    private static string Reply(string request, string result)
    {
        var id = JsonNode.Parse(request)!["id"]!.GetValue<int>();
        return $$"""{"jsonrpc":"2.0","result":{{result}},"id":{{id}}}""";
    }

    private static string Method(string request) => JsonNode.Parse(request)!["method"]!.GetValue<string>();

    [Fact]
    public async Task GuidesSettlesDithersAndReportsRms()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ditherCount = 0;
        var profile = 1;
        var gearConnected = false;
        using var phd2 = new ScriptedServer(
            [
                """{"Event":"Version","PHDVersion":"2.6.13","PHDSubver":"","MsgVersion":1}""",
                """{"Event":"AppState","State":"Stopped"}""",
            ],
            request => Method(request) switch
            {
                "get_app_state" => [Reply(request, "\"Stopped\"")],
                "get_pixel_scale" => [Reply(request, "2.0")],
                "get_profiles" => [Reply(request, """[{"id":1,"name":"Simulators"},{"id":2,"name":"ASI120 + AM5"}]""")],
                "get_profile" => [Reply(request, profile == 1 ? """{"id":1,"name":"Simulators"}""" : """{"id":2,"name":"ASI120 + AM5"}""")],
                "get_connected" => [Reply(request, gearConnected ? "true" : "false")],
                "set_profile" => SetProfile(request),
                "set_connected" => SetConnected(request),
                "guide" =>
                [
                    Reply(request, "0"),
                    """{"Event":"StartCalibration","Mount":"INDI Mount"}""",
                    """{"Event":"StartGuiding"}""",
                    // While settling (not counted in the RMS), then two steady steps.
                    """{"Event":"GuideStep","Frame":1,"RADistanceRaw":4,"DECDistanceRaw":3,"SNR":40.0,"HFD":2.3}""",
                    "sleep:100",
                    """{"Event":"SettleDone","Status":0,"TotalFrames":5,"DroppedFrames":0}""",
                    """{"Event":"GuideStep","Frame":2,"RADistanceRaw":0.3,"DECDistanceRaw":-0.4,"SNR":42.5,"HFD":2.1}""",
                    """{"Event":"GuideStep","Frame":3,"RADistanceRaw":-0.3,"DECDistanceRaw":0.4,"SNR":41.0,"HFD":2.2}""",
                ],
                // The first dither fails to settle, the second succeeds.
                "dither" => ++ditherCount == 1
                    ? [Reply(request, "0"), """{"Event":"SettleDone","Status":1,"Error":"timed-out waiting for guider to settle"}"""]
                    : [Reply(request, "0"), """{"Event":"SettleDone","Status":0}"""],
                "stop_capture" => [Reply(request, "0"), """{"Event":"GuidingStopped"}"""],
                _ => [$$"""{"jsonrpc":"2.0","error":{"code":1,"message":"unknown method"},"id":{{JsonNode.Parse(request)!["id"]}}}"""],
            });

        await using var client = new Phd2Client();
        await client.ConnectAsync("127.0.0.1", phd2.Port, cts.Token);
        Assert.Equal(GuiderState.Stopped, client.State);
        Assert.Equal(2.0, client.PixelScale);
        Assert.Equal("2.6.13", client.Version);

        await client.GuideAsync(new SettleSettings(), recalibrate: false, cts.Token);
        Assert.Equal(GuiderState.Guiding, client.State);
        for (var i = 0; i < 50 && client.History.Count < 3; i++) await Task.Delay(20, cts.Token);
        Assert.Equal([true, false, false], client.History.Select(p => p.Settling));
        var stats = client.Stats;
        Assert.Equal("arcsec", stats.Units);
        Assert.Equal(2, stats.Samples);
        Assert.Equal(0.6, stats.RmsRa, 6);   // 0.3 px × 2"/px
        Assert.Equal(0.8, stats.RmsDec, 6);  // 0.4 px × 2"/px
        Assert.Equal(1.0, stats.RmsTotal, 6);
        Assert.Equal(41.0, client.Snr);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DitherAsync(5, false, new SettleSettings(), cts.Token));
        Assert.Contains("timed-out", failure.Message);
        await client.DitherAsync(5, false, new SettleSettings(), cts.Token);

        await client.StopAsync(cts.Token);
        for (var i = 0; i < 40 && client.State != GuiderState.Stopped; i++) await Task.Delay(25);
        Assert.Equal(GuiderState.Stopped, client.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync("bogus", null, cts.Token));

        // Profiles: switching disconnects PHD2's equipment first, as PHD2 requires.
        Assert.Equal(["Simulators", "ASI120 + AM5"], client.Profiles.Select(p => p.Name));
        Assert.Equal(1, client.Profile?.Id);
        await client.SetEquipmentConnectedAsync(true, cts.Token);
        Assert.True(client.EquipmentConnected);
        await client.SetProfileAsync(2, cts.Token);
        Assert.Equal("ASI120 + AM5", client.Profile?.Name);
        Assert.False(client.EquipmentConnected);
        var sent = phd2.Received.Select(l => JsonNode.Parse(l)!).ToList();
        var disconnectAt = sent.FindIndex(r => Method(r.ToJsonString()) == "set_connected" && !r["params"]!["connected"]!.GetValue<bool>());
        var switchAt = sent.FindIndex(r => Method(r.ToJsonString()) == "set_profile");
        Assert.True(disconnectAt >= 0 && disconnectAt < switchAt);
        Assert.Equal(2, sent[switchAt]["params"]!["id"]!.GetValue<int>());
        Assert.Contains(phd2.Received, l => l.Contains("\"method\":\"guide\"") && l.Contains("\"pixels\":1.5"));

        IEnumerable<string> SetProfile(string request)
        {
            var id = JsonNode.Parse(request)!["id"];
            if (gearConnected) return [$$"""{"jsonrpc":"2.0","error":{"code":1,"message":"cannot change profile when connected"},"id":{{id}}}"""];
            profile = JsonNode.Parse(request)!["params"]!["id"]!.GetValue<int>();
            return [Reply(request, "0")];
        }

        IEnumerable<string> SetConnected(string request)
        {
            gearConnected = JsonNode.Parse(request)!["params"]!["connected"]!.GetValue<bool>();
            return [Reply(request, "0")];
        }
    }
}

public class FilterWheelAndRotatorTests
{
    [Fact]
    public async Task DrivesIndiFilterWheelAndRotator()
    {
        string[] definitions =
        [
            """<defTextVector device="ZWO EFW" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">16</defText></defTextVector>""",
            """<defSwitchVector device="ZWO EFW" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO EFW" name="FILTER_SLOT" state="Ok" perm="rw"><defNumber name="FILTER_SLOT_VALUE" format="%3.0f" min="1" max="5" step="1">1</defNumber></defNumberVector>""",
            """<defTextVector device="ZWO EFW" name="FILTER_NAME" state="Ok" perm="rw"><defText name="FILTER_SLOT_NAME_1">L</defText><defText name="FILTER_SLOT_NAME_2">R</defText><defText name="FILTER_SLOT_NAME_3"></defText><defText name="FILTER_SLOT_NAME_4">B</defText><defText name="FILTER_SLOT_NAME_5">Ha</defText></defTextVector>""",
            """<defTextVector device="ZWO CAA" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">4096</defText></defTextVector>""",
            """<defSwitchVector device="ZWO CAA" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO CAA" name="ABS_ROTATOR_ANGLE" state="Ok" perm="rw"><defNumber name="ANGLE" format="%.2f" min="0" max="360" step="0.1">12.5</defNumber></defNumberVector>""",
        ];
        using var server = new ScriptedServer(definitions, line =>
        {
            var slot = Regex.Match(line, "FILTER_SLOT_VALUE\">([0-9.]+)<");
            if (slot.Success)
                return [
                    """<setNumberVector device="ZWO EFW" name="FILTER_SLOT" state="Busy"><oneNumber name="FILTER_SLOT_VALUE">1</oneNumber></setNumberVector>""",
                    $"""<setNumberVector device="ZWO EFW" name="FILTER_SLOT" state="Ok"><oneNumber name="FILTER_SLOT_VALUE">{slot.Groups[1].Value}</oneNumber></setNumberVector>""",
                ];
            var angle = Regex.Match(line, "ANGLE\">([0-9.]+)<");
            if (angle.Success)
                return [
                    """<setNumberVector device="ZWO CAA" name="ABS_ROTATOR_ANGLE" state="Busy"><oneNumber name="ANGLE">12.5</oneNumber></setNumberVector>""",
                    $"""<setNumberVector device="ZWO CAA" name="ABS_ROTATOR_ANGLE" state="Ok"><oneNumber name="ANGLE">{angle.Groups[1].Value}</oneNumber></setNumberVector>""",
                ];
            return [];
        }, skipFirstLine: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        var wheel = provider.GetFilterWheel("ZWO EFW")!;
        Assert.Null(provider.GetFilterWheel("ZWO CAA"));
        Assert.Equal(5, wheel.SlotCount);
        Assert.Equal(["L", "R", "Slot 3", "B", "Ha"], wheel.Names); // unnamed slot falls back
        await wheel.MoveToAsync(5, cts.Token);
        Assert.Equal(5, wheel.Position);
        await Assert.ThrowsAsync<ArgumentException>(() => wheel.MoveToAsync(6, cts.Token));

        var rotator = provider.GetRotator("ZWO CAA")!;
        Assert.Equal(12.5, rotator.Angle);
        await rotator.MoveToAsync(-90, cts.Token); // normalised to 270
        Assert.Equal(270, rotator.Angle);
    }

    [Fact]
    public async Task SimulatedWheelAndRotatorMove()
    {
        await using var simulator = new SimulatorProvider();
        var wheel = simulator.GetFilterWheel("Simulated Filter Wheel")!;
        var rotator = simulator.GetRotator("Simulated Rotator")!;
        await wheel.ConnectAsync(CancellationToken.None);
        await rotator.ConnectAsync(CancellationToken.None);

        await wheel.MoveToAsync(2, CancellationToken.None);
        Assert.Equal("R", wheel.Names[wheel.Position - 1]);

        await rotator.MoveToAsync(80, CancellationToken.None); // from 90, the short way
        Assert.Equal(80, rotator.Angle, 6);
    }
}

public class NamelessDeviceTests
{
    [Fact]
    public async Task PropertiesWithoutADeviceNameAreNotListedAsADevice()
    {
        using var server = new ScriptedServer(
            [
                """<defTextVector device="ZWO EAF" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_EXEC">indi_asi_focuser</defText><defText name="DRIVER_INTERFACE">8</defText></defTextVector>""",
                """<defTextVector name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_EXEC">indi_asi_wheel</defText><defText name="DRIVER_INTERFACE">0</defText></defTextVector>""",
            ],
            _ => [], skipFirstLine: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        var messages = new List<string>();
        provider.Message += (_, m) => messages.Add(m);
        await provider.ConnectAsync(cts.Token);
        for (var i = 0; i < 100 && provider.Devices.Count == 0; i++) await Task.Delay(20);

        Assert.Equal(["ZWO EAF"], provider.Devices.Select(d => d.Name));
        Assert.Contains(messages, m => m.Contains("without a device name") && m.Contains("indi_asi_wheel"));
    }
}

public class NewDeviceTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task ADriverStartedWhileConnectedHasItsDeviceConnected()
    {
        // The filter wheel's driver starts a few seconds after NOVA connected (e.g. ticked in Drivers…).
        // Like a real driver, it defines its connection switch first and says what kind of device it is a moment later.
        string[] wheel =
        [
            "sleep:2500",
            """<defSwitchVector device="Filter Simulator" name="CONNECTION" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">Off</defSwitch><defSwitch name="DISCONNECT">On</defSwitch></defSwitchVector>""",
            "sleep:300",
            """<defTextVector device="Filter Simulator" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">16</defText></defTextVector>""",
        ];
        using var server = new ScriptedServer([], line =>
            line.Contains("getProperties") && !line.Contains("device=") ? wheel
            : line.Contains("Filter Simulator") && line.Contains("CONNECTION") && line.Contains(">On<")
                ? ["""<setSwitchVector device="Filter Simulator" name="CONNECTION" state="Ok"><oneSwitch name="CONNECT">On</oneSwitch><oneSwitch name="DISCONNECT">Off</oneSwitch></setSwitchVector>"""]
                : []);
        var options = Microsoft.Extensions.Options.Options.Create(new Nova.Server.Services.NovaOptions
        {
            Backend = "Indi", IndiHost = "127.0.0.1", IndiPort = server.Port, ConfigDirectory = _config,
        });
        var log = new Nova.Server.Services.ActivityLog(new NullHubContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Nova.Server.Services.ActivityLog>.Instance);
        var equipment = new Nova.Server.Services.EquipmentService(options, log);

        await equipment.ConnectAsync(CancellationToken.None);
        Assert.Null(equipment.FilterWheel); // not there yet

        for (var i = 0; i < 160 && equipment.FilterWheel is not { IsConnected: true }; i++) await Task.Delay(50);
        Assert.True(equipment.FilterWheel is { IsConnected: true, Name: "Filter Simulator" }, "the new filter wheel should have been connected");
    }
}
