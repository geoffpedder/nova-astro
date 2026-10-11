using Nova.Core.Equipment;
using Nova.Core.Simulation;
using Nova.Indi;

namespace Nova.Tests;

public class OpticsTests
{
    [Fact]
    public void ComputesFocalRatioAndImageScale()
    {
        var optics = new Optics(FocalLength: 400, Aperture: 80);
        Assert.Equal(5, optics.FocalRatio);
        Assert.Equal(1.94, optics.ImageScale(pixelSizeMicrons: 3.76), 2); // 206.265 × 3.76 / 400
    }

    [Fact]
    public async Task SendsOpticsToIndiCameraAndReadsPixelSize()
    {
        string[] camera =
        [
            """<defTextVector device="ZWO CCD ASI120MM Mini" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">6</defText></defTextVector>""",
            """<defSwitchVector device="ZWO CCD ASI120MM Mini" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO CCD ASI120MM Mini" name="CCD_INFO" state="Idle" perm="ro"><defNumber name="CCD_MAX_X" format="%4.0f" min="1" max="16000" step="0">1280</defNumber><defNumber name="CCD_PIXEL_SIZE" format="%5.2f" min="1" max="40" step="0">3.75</defNumber></defNumberVector>""",
            """<defNumberVector device="ZWO CCD ASI120MM Mini" name="SCOPE_INFO" group="Options" state="Idle" perm="rw"><defNumber name="FOCAL_LENGTH" format="%g" min="0" max="10000" step="1">0</defNumber><defNumber name="APERTURE" format="%g" min="0" max="3000" step="1">0</defNumber></defNumberVector>""",
        ];
        using var server = new ScriptedServer(camera, _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        var asi120 = provider.GetCamera("ZWO CCD ASI120MM Mini")!;
        Assert.Equal(3.75, asi120.PixelSize);

        await asi120.SetOpticsAsync(new Optics(120, 30), cts.Token);
        for (var i = 0; i < 40 && !server.Received.Any(l => l.Contains("SCOPE_INFO")); i++) await Task.Delay(50);
        Assert.Contains(server.Received, l =>
            l.Contains("name=\"SCOPE_INFO\"") && l.Contains("\"FOCAL_LENGTH\">120<") && l.Contains("\"APERTURE\">30<"));
    }

    [Fact]
    public async Task SimulatedImageScaleFollowsFocalLength()
    {
        await using var simulator = new SimulatorProvider();
        var camera = simulator.GetCamera("Simulated Camera")!;
        await camera.ConnectAsync(CancellationToken.None);

        double Scale(CapturedImage frame) => double.Parse(frame.Image.Headers["PIXSCALE"], System.Globalization.CultureInfo.InvariantCulture);

        var before = Scale(await camera.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None));
        await camera.SetOpticsAsync(new Optics(776, 100), CancellationToken.None); // double the focal length
        var after = Scale(await camera.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None));

        Assert.Equal(2.0, before, 2);
        Assert.Equal(before / 2, after, 3);
    }
}
