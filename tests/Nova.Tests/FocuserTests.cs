using System.Net;
using System.Net.Sockets;
using System.Text;
using Nova.Core.Equipment;
using Nova.Core.Simulation;
using Nova.Indi;

namespace Nova.Tests;

public class FocuserTests
{
    private const string Eaf = "ZWO EAF";

    // An EAF-like focuser that answers moves the way the driver does: Busy, then Ok at the new position.
    private static readonly string[] EafDefinitions =
    [
        $"""<defTextVector device="{Eaf}" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">8</defText></defTextVector>""",
        $"""<defSwitchVector device="{Eaf}" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
        $"""<defNumberVector device="{Eaf}" name="ABS_FOCUS_POSITION" state="Ok" perm="rw"><defNumber name="FOCUS_ABSOLUTE_POSITION" format="%6.0f" min="0" max="100000" step="10">5000</defNumber></defNumberVector>""",
        $"""<defNumberVector device="{Eaf}" name="FOCUS_MAX" state="Ok" perm="rw"><defNumber name="FOCUS_MAX_VALUE" format="%6.0f" min="0" max="100000" step="10">20000</defNumber></defNumberVector>""",
        $"""<defNumberVector device="{Eaf}" name="FOCUS_TEMPERATURE" state="Ok" perm="ro"><defNumber name="TEMPERATURE" format="%6.2f" min="-50" max="70" step="0">8.25</defNumber></defNumberVector>""",
        $"""<defSwitchVector device="{Eaf}" name="FOCUS_ABORT_MOTION" state="Idle" perm="rw" rule="AtMostOne"><defSwitch name="ABORT">Off</defSwitch></defSwitchVector>""",
    ];

    [Fact]
    public async Task MovesStepsAndHaltsAnIndiFocuser()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cts.Token);
            var stream = socket.GetStream();
            var reader = new StreamReader(stream);
            async Task Send(string xml) => await stream.WriteAsync(Encoding.UTF8.GetBytes(xml + "\n"), cts.Token);
            await reader.ReadLineAsync(cts.Token);
            foreach (var xml in EafDefinitions) await Send(xml);
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                received.Enqueue(line);
                var match = System.Text.RegularExpressions.Regex.Match(line, "FOCUS_ABSOLUTE_POSITION\">([0-9.]+)<");
                if (!match.Success) continue;
                await Send($"""<setNumberVector device="{Eaf}" name="ABS_FOCUS_POSITION" state="Busy"><oneNumber name="FOCUS_ABSOLUTE_POSITION">5000</oneNumber></setNumberVector>""");
                await Task.Delay(100);
                await Send($"""<setNumberVector device="{Eaf}" name="ABS_FOCUS_POSITION" state="Ok"><oneNumber name="FOCUS_ABSOLUTE_POSITION">{match.Groups[1].Value}</oneNumber></setNumberVector>""");
            }
        });

        await using var provider = new IndiEquipmentProvider("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        await provider.ConnectAsync(cts.Token);
        Assert.Contains(DeviceKind.Focuser, provider.Devices.Single().Kinds);

        var focuser = provider.GetFocuser(Eaf)!;
        Assert.True(focuser.CanMoveAbsolute);
        Assert.Equal((5000, 0, 20000), (focuser.Position, focuser.MinPosition, focuser.MaxPosition)); // FOCUS_MAX wins
        Assert.Equal(8.25, focuser.Temperature);

        await focuser.MoveToAsync(6000, cts.Token);
        Assert.Equal(6000, focuser.Position);

        await focuser.MoveRelativeAsync(-250, cts.Token);
        Assert.Equal(5750, focuser.Position);

        await focuser.MoveRelativeAsync(50_000, cts.Token); // clamped to FOCUS_MAX
        Assert.Equal(20000, focuser.Position);

        await Assert.ThrowsAsync<ArgumentException>(() => focuser.MoveToAsync(25_000, cts.Token));

        await focuser.HaltAsync(cts.Token);
        for (var i = 0; i < 50 && !received.Any(l => l.Contains("FOCUS_ABORT_MOTION")); i++) await Task.Delay(50);
        Assert.Contains(received, l => l.Contains("FOCUS_ABORT_MOTION") && l.Contains(">On<"));
    }

    [Fact]
    public async Task SimulatedFocusSharpensStars()
    {
        await using var simulator = new SimulatorProvider();
        var focuser = simulator.GetFocuser("Simulated Focuser")!;
        var camera = simulator.GetCamera("Simulated Camera")!;
        await focuser.ConnectAsync(CancellationToken.None);
        await camera.ConnectAsync(CancellationToken.None);

        async Task<float> PeakAt(int position)
        {
            await focuser.MoveToAsync(position, CancellationToken.None);
            var frame = await camera.ExposeAsync(new ExposureRequest(1), null, CancellationToken.None);
            return frame.Image.Pixels.Max();
        }

        // Defocused starlight spreads out, so the brightest pixel is much dimmer far from focus.
        var focused = await PeakAt(25_000);
        var blurred = await PeakAt(29_000);
        Assert.True(focused > blurred * 2, $"focused peak {focused} vs blurred {blurred}");
    }
}
