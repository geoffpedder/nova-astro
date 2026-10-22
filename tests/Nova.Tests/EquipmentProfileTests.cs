using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class EquipmentProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-profiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private (EquipmentProfileService Profiles, EquipmentService Equipment) Service()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var profiles = new EquipmentProfileService(equipment, new DriverSetupService(equipment, options, log),
            new SimulatorService(equipment, options, log), new MountBluetoothService(equipment, options, log), options, log);
        return (profiles, equipment);
    }

    [Fact]
    public async Task SavesWhatsInUseAndPutsItBack()
    {
        var (profiles, equipment) = Service();
        // "Automatic" is saved as the device it picked, so the profile names real devices.
        var saved = profiles.Save("  Askar  ", null);
        Assert.Equal("Askar", saved.Name);
        Assert.Equal("Simulated Camera", saved.Devices.Camera);
        Assert.Equal("Simulated Mount", saved.Devices.Mount);
        Assert.Null(saved.Drivers); // NOVA's own simulator has no INDI drivers
        Assert.Equal("Askar", profiles.Status.Active);

        await equipment.SelectAsync(DeviceKind.Camera, "Simulated Guide Camera", CancellationToken.None);
        profiles.Save("Guide test", null);
        Assert.Equal("Simulated Guide Camera", equipment.Selection.Camera);

        await profiles.LoadAsync("Askar", CancellationToken.None);
        Assert.Equal("Simulated Camera", equipment.Selection.Camera);
        Assert.Equal("Simulated Camera", equipment.Camera!.Name);

        // Remembered across restarts, with the active one.
        var (again, _) = Service();
        Assert.Equal(["Askar", "Guide test"], again.Status.Profiles.Select(p => p.Name));
        Assert.Equal("Askar", again.Status.Active);
    }

    [Fact]
    public async Task OnlyLoadsWhileDisconnected()
    {
        var (profiles, equipment) = Service();
        profiles.Save("Askar", null);
        await equipment.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.LoadAsync("Askar", CancellationToken.None));
        await equipment.DisconnectAsync(CancellationToken.None);
        await profiles.LoadAsync("Askar", CancellationToken.None);
    }

    [Fact]
    public async Task RenamesReplacesAndDeletes()
    {
        var (profiles, _) = Service();
        profiles.Save("Askar", null);
        profiles.Save("RedCat", null);
        Assert.Throws<ArgumentException>(() => profiles.Save("redcat", "Askar")); // that name's taken
        Assert.Throws<ArgumentException>(() => profiles.Save(" ", null));

        profiles.Save("Askar 71F", "Askar");
        Assert.Equal(["Askar 71F", "RedCat"], profiles.Status.Profiles.Select(p => p.Name));

        profiles.Delete("Askar 71F");
        Assert.Equal(["RedCat"], profiles.Status.Profiles.Select(p => p.Name));
        Assert.Throws<ArgumentException>(() => profiles.Delete("Askar 71F"));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.LoadAsync("Askar 71F", CancellationToken.None));
    }
}
