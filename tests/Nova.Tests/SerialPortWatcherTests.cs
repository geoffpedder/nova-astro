using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class SerialPortWatcherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-serial-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void OnlyNewPortsCount()
    {
        var before = new HashSet<string> { "/dev/ttyUSB0" };
        var now = new HashSet<string> { "/dev/ttyUSB0", "/dev/ttyUSB1", "/dev/ttyACM0" };
        Assert.Equal(["/dev/ttyACM0", "/dev/ttyUSB1"], SerialPortWatcher.Added(before, now));
        Assert.Empty(SerialPortWatcher.Added(now, before)); // unplugging isn't
    }

    [Fact]
    public async Task APortAppearingConnectsTheChosenDevicesThatArent()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder, Phd2AutoConnect = false });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        await using var equipment = new EquipmentService(options, log);

        // Not connected yet (Connect not pressed): plugging something in doesn't connect anything.
        await equipment.SerialPortsAddedAsync(["/dev/ttyUSB0"], CancellationToken.None);
        Assert.False(equipment.FlatPanel!.IsConnected);

        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.FlatPanel!.DisconnectAsync(CancellationToken.None); // as if it had failed to connect
        await equipment.SerialPortsAddedAsync(["/dev/ttyUSB0"], CancellationToken.None);
        Assert.True(equipment.FlatPanel.IsConnected);
        Assert.Contains(log.Recent, e => e.Message.Contains("/dev/ttyUSB0 appeared, connecting"));
    }
}
