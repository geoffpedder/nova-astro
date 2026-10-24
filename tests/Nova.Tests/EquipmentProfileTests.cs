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

    private (EquipmentProfileService Profiles, EquipmentService Equipment) Service() => Service(out _);

    private (EquipmentProfileService Profiles, EquipmentService Equipment) Service(out NovaGuider guider)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder, Phd2AutoConnect = false });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        guider = new NovaGuider(equipment, new OpticsService(equipment, options, log), options, log);
        var profiles = new EquipmentProfileService(equipment, new DriverSetupService(equipment, options, log),
            new MountBluetoothService(equipment, options, log), new GuiderService(options, log, guider), guider, options, log);
        return (profiles, equipment);
    }

    [Fact]
    public async Task AnEmptyRoleStaysEmptyAndNothingIsConnectedForIt()
    {
        // Saved with no rotator: loading it mustn't let "automatic" connect one (a simulated one, say).
        var (profiles, equipment) = Service(out var guider);
        await equipment.SelectAsync(DeviceKind.Rotator, EquipmentSelection.None, CancellationToken.None);
        guider.UpdateSettings(guider.Settings with { Camera = "Simulated Guide Camera" });
        var saved = profiles.Save("No rotator", null);
        Assert.Equal(EquipmentSelection.None, saved.Devices.Rotator);
        Assert.Equal("Simulated Guide Camera", saved.GuideCamera);

        await equipment.SelectAsync(DeviceKind.Rotator, null, CancellationToken.None); // back to automatic
        guider.UpdateSettings(guider.Settings with { Camera = null });
        await profiles.LoadAsync("No rotator", CancellationToken.None);
        Assert.Equal(EquipmentSelection.None, equipment.Selection.Rotator);
        Assert.Null(equipment.Rotator);
        Assert.Equal("Simulated Guide Camera", guider.Settings.Camera);

        await equipment.ConnectAsync(CancellationToken.None);
        Assert.False(equipment.Provider.Devices.Single(d => d.Name == "Simulated Rotator").IsConnected);
        Assert.True(equipment.Camera!.IsConnected);
        await equipment.DisconnectAsync(CancellationToken.None);
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
