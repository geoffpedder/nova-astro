using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-diagnostics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // A camera exposing (ZWO's cameras are reset at each exposure's start), and a guide camera whose link is failing.
    private const string Resets = """
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 2-2: reset SuperSpeed USB device number 2 using xhci_hcd
        usb 1-2.2: USB disconnect, device number 4
        usb 1-2.2: new high-speed USB device number 5 using xhci_hcd
        usb 1-2.2: device descriptor read/64, error -71
        usb 1-2-port2: Cannot enable. Maybe the USB cable is bad?
        usb 1-2-port2: unable to enumerate USB device
        usb usb1-port6: over-current condition
        usb 1-6: New USB device found, idVendor=0573, idProduct=1573
        """;

    [Fact]
    public void CountsDropsAndErrorsPerPortButNotResets()
    {
        var counts = UsbHealthService.Count(Resets);
        Assert.Equal(4, counts["1-2.2"]);       // the drop, and three errors (two against its hub port)
        Assert.Equal(1, counts["1-6"]);         // over-current on the root hub's port 6
        Assert.False(counts.ContainsKey("2-2")); // resets at each exposure aren't trouble
    }

    [Fact]
    public void NamesAFailingDeviceOnceInAWhile()
    {
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var usb = new UsbHealthService(log) { DeviceName = port => port == "1-2.2" ? "ZWO ASI120MM Mini" : null };
        var now = DateTime.UtcNow;

        usb.Check(Resets, now);
        var warning = Assert.Single(log.Recent);
        Assert.Contains("ZWO ASI120MM Mini (USB port 1-2.2) dropped off USB or failed to answer 4 times", warning.Message);
        Assert.Contains("its own cable is best", warning.Message);

        usb.Check(Resets, now.AddMinutes(1)); // still failing: not every minute
        Assert.Single(log.Recent);
        usb.Check(Resets, now.AddMinutes(11));
        Assert.Equal(2, log.Recent.Count);
    }

    [Fact]
    public void BlanksSecretsAndWhereYouObserve()
    {
        var redacted = DiagnosticsService.Redact("""
            { "Active": "Home", "Sites": [ { "Name": "Home", "Latitude": 51.4779, "Longitude": -0.0015, "Elevation": 46 } ],
              "Code": "13572468", "Token": "tk_abc", "Hotspot": { "Password": "hunter2", "Ssid": "NOVA" } }
            """);
        Assert.DoesNotContain("51.4779", redacted);
        Assert.DoesNotContain("-0.0015", redacted);
        Assert.DoesNotContain("13572468", redacted);
        Assert.DoesNotContain("tk_abc", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("\"Home\"", redacted);   // names stay: they help
        Assert.Contains("\"NOVA\"", redacted);
        Assert.Contains("(blanked)", redacted);
    }

    [Fact]
    public async Task OneZipWithTheLogsAndTheSettings()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "sites.json"), """{ "Sites": [ { "Name": "Home", "Latitude": 51.4779, "Longitude": -0.0015 } ] }""");
        File.WriteAllText(Path.Combine(_folder, "auth.json"), """{ "Hash": "secret" }""");
        File.WriteAllText(Path.Combine(_folder, "mount-bluetooth.json"), """{ "Enabled": true, "Mount": "AM3_68bc8d", "Code": "13572468" }""");
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        log.Warn("Guiding stopped: a test");
        var diagnostics = new DiagnosticsService(log, options) { Run = (_, _, _) => Task.FromResult("(ran)\n") };

        using var zipped = new MemoryStream();
        await diagnostics.WriteAsync(zipped, CancellationToken.None);
        zipped.Position = 0;
        using var zip = new ZipArchive(zipped);
        string Read(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();

        Assert.NotNull(zip.GetEntry("README.txt"));
        Assert.Contains("Guiding stopped: a test", Read("activity.txt"));
        Assert.Null(zip.GetEntry("settings/auth.json")); // the password's hash never leaves
        Assert.DoesNotContain("51.4779", Read("settings/sites.json"));
        var bluetooth = Read("settings/mount-bluetooth.json");
        Assert.Contains("AM3_68bc8d", bluetooth);
        Assert.DoesNotContain("13572468", bluetooth);
        Assert.EndsWith(".zip", diagnostics.FileName);
    }
}
