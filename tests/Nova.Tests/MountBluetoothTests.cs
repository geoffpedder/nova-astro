using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class MountBluetoothTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-bluetooth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private (MountBluetoothService Bluetooth, EquipmentService Equipment) Service()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        return (new MountBluetoothService(equipment, options, log), equipment);
    }

    [Fact]
    public void ReadsTheHelpersEvents()
    {
        var found = MountBluetoothService.ParseEvent("""{"event": "found", "name": "AM3_68bc8d", "address": "F4:12:FA:68:BC:8E", "rssi": -43}""");
        Assert.Equal(new MountBluetoothService.BridgeEvent("found", "AM3_68bc8d", "F4:12:FA:68:BC:8E", -43, null, false), found);

        var fatal = MountBluetoothService.ParseEvent("""{"event": "error", "message": "Python's Bleak library isn't installed", "fatal": true}""");
        Assert.True(fatal!.Fatal);
        Assert.Equal("Python's Bleak library isn't installed", fatal.Message);

        Assert.False(MountBluetoothService.ParseEvent("""{"event": "error", "message": "timeout"}""")!.Fatal);
        Assert.Null(MountBluetoothService.ParseEvent("Traceback (most recent call last):"));
        Assert.Null(MountBluetoothService.ParseEvent("""{"name": "no event"}"""));
        Assert.Null(MountBluetoothService.ParseEvent("[1, 2]"));
    }

    [Fact]
    public async Task RemembersTheChosenMount()
    {
        var (bluetooth, _) = Service();
        Assert.Equal("Off", bluetooth.Status.State);
        Assert.False(bluetooth.Status.Enabled);

        await bluetooth.SetAsync(true, " AM3_68bc8d ", "F4:12:FA:68:BC:8E", CancellationToken.None);
        Assert.True(bluetooth.Status.Enabled);
        Assert.Equal("AM3_68bc8d", bluetooth.Status.Mount);
        // On, but nothing runs until equipment is connected.
        Assert.Equal("Idle", bluetooth.Status.State);

        var (again, _) = Service();
        Assert.True(again.Status.Enabled);
        Assert.Equal("AM3_68bc8d", again.Status.Mount);
        Assert.Equal("F4:12:FA:68:BC:8E", again.Status.Address);
    }

    [Fact]
    public async Task UsbAgainKeepsTheMountForNextTime()
    {
        var (bluetooth, _) = Service();
        await bluetooth.SetAsync(true, "AM3_68bc8d", null, CancellationToken.None);
        await bluetooth.SetAsync(false, null, null, CancellationToken.None);
        Assert.Equal("Off", bluetooth.Status.State);
        Assert.Equal("AM3_68bc8d", bluetooth.Status.Mount);
    }

    [Fact]
    public async Task RefusesNonsenseNames()
    {
        var (bluetooth, _) = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => bluetooth.SetAsync(true, new string('x', 65), null, CancellationToken.None));
        Assert.False(bluetooth.Status.Enabled);
    }

    [Fact]
    public async Task OnlyEverAChosenMount()
    {
        // Never "whichever mount is found": at a shared site that could be someone else's.
        var (bluetooth, _) = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => bluetooth.SetAsync(true, " ", null, CancellationToken.None));
        Assert.False(bluetooth.Status.Enabled);

        // Turned on that way by an older NOVA: it waits to be told which mount.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "mount-bluetooth.json"), """{"Enabled": true}""");
        Assert.Equal("Choose", Service().Bluetooth.Status.State);
    }

    [Fact]
    public void EachNovaHasItsOwnCode()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => MountBluetoothService.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[0-9]{8}$", c));
        Assert.DoesNotContain("12345678", codes); // the code everyone else's software uses
        Assert.True(codes.Distinct().Count() > 45);
    }

    [Fact]
    public void ReadsTheAskToPressTheButton()
    {
        var e = MountBluetoothService.ParseEvent("""{"event": "confirm", "name": "AM3_68bc8d", "address": "F4:12:FA:68:BC:8E", "timeout": 30.0}""");
        Assert.Equal(("confirm", "AM3_68bc8d"), (e!.Event, e.Name));
    }

    [Fact]
    public void OnlyForTheZwoDriverOnIndi()
    {
        // NOVA's own simulator has no drivers: nothing to connect over Bluetooth.
        var (bluetooth, equipment) = Service();
        Assert.False(bluetooth.Status.Applicable);
        Assert.NotNull(equipment.BeforeConnect);
    }

    [Fact]
    public async Task ConnectingOtherDevicesIsUntouched()
    {
        var (bluetooth, equipment) = Service();
        await bluetooth.SetAsync(true, "AM3_68bc8d", null, CancellationToken.None);
        // The simulator's mount: not applicable, so connecting it doesn't wait for a Bluetooth link.
        await equipment.ConnectAsync(CancellationToken.None);
        Assert.True(equipment.Mount!.IsConnected);
        Assert.Equal("Idle", bluetooth.Status.State);
        await equipment.DisconnectAsync(CancellationToken.None);
    }
}
