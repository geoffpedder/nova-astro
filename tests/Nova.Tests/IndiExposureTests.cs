using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Indi;

namespace Nova.Tests;

public class IndiExposureTests
{
    private const string Camera = "CCD Simulator";

    [Fact]
    public async Task ReceivesTheImageEvenIfAnotherSessionLeftTheCameraConnectedAndUploadingLocally()
    {
        // The camera is already connected (NOVA restarted, the driver didn't) and set to save locally only.
        string[] definitions =
        [
            $"""<defTextVector device="{Camera}" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">6</defText></defTextVector>""",
            $"""<defSwitchVector device="{Camera}" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            $"""<defNumberVector device="{Camera}" name="CCD_EXPOSURE" state="Idle" perm="rw"><defNumber name="CCD_EXPOSURE_VALUE" format="%5.2f" min="0" max="3600" step="1">1</defNumber></defNumberVector>""",
            $"""<defSwitchVector device="{Camera}" name="UPLOAD_MODE" state="Idle" perm="rw" rule="OneOfMany"><defSwitch name="UPLOAD_CLIENT">Off</defSwitch><defSwitch name="UPLOAD_LOCAL">On</defSwitch><defSwitch name="UPLOAD_BOTH">Off</defSwitch></defSwitchVector>""",
            $"""<defBLOBVector device="{Camera}" name="CCD1" state="Idle" perm="ro"><defBLOB name="CCD1"/></defBLOBVector>""",
        ];

        var blobsEnabled = false;
        var uploadToClient = false;
        var fits = Convert.ToBase64String(Fits.WriteUInt16(2, 2, [10, 20, 30, 40], [new FitsCard("EXPTIME", 1.0)]));

        using var server = new ScriptedServer(definitions, line =>
        {
            if (line.Contains("<enableBLOB") && line.Contains(">Also<")) blobsEnabled = true;
            if (line.Contains("name=\"UPLOAD_MODE\"") && line.Contains("\"UPLOAD_CLIENT\">On<")) uploadToClient = true;
            if (!line.Contains("name=\"CCD_EXPOSURE\"")) return [];

            // Like indiserver: the exposure finishes either way, but the image only reaches clients
            // that enabled BLOBs, and only if the driver uploads to the client.
            List<string> replies =
            [
                $"""<setNumberVector device="{Camera}" name="CCD_EXPOSURE" state="Busy"><oneNumber name="CCD_EXPOSURE_VALUE">0.5</oneNumber></setNumberVector>""",
                $"""<setNumberVector device="{Camera}" name="CCD_EXPOSURE" state="Ok"><oneNumber name="CCD_EXPOSURE_VALUE">0</oneNumber></setNumberVector>""",
            ];
            if (blobsEnabled && uploadToClient)
                replies.Add($"""<setBLOBVector device="{Camera}" name="CCD1" state="Ok"><oneBLOB name="CCD1" size="{fits.Length}" format=".fits">{fits}</oneBLOB></setBLOBVector>""");
            return replies;
        }, skipFirstLine: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);

        // No ConnectAsync here: NOVA only ever saw this camera already connected.
        var camera = provider.GetCamera(Camera)!;
        var progress = new List<ExposureProgress>();
        var frame = await camera.ExposeAsync(new ExposureRequest(1), new SyncProgress(progress.Add), cts.Token);

        Assert.Equal([10f, 20f, 30f, 40f], frame.Image.Pixels);
        Assert.Contains(progress, p => p.State == CameraState.Downloading);
        Assert.Equal(CameraState.Idle, camera.State);
    }

    private sealed class SyncProgress(Action<ExposureProgress> report) : IProgress<ExposureProgress>
    {
        public void Report(ExposureProgress value) => report(value);
    }
}
