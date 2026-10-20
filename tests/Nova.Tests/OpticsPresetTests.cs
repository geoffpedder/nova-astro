using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Hubs;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class OpticsPresetTests : IDisposable
{
    private const string Camera = "Simulated Camera";
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private OpticsService CreateService() => CreateService(out _);

    private OpticsService CreateService(out EquipmentService equipment)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        equipment = new EquipmentService(options, log);
        return new OpticsService(equipment, options, log);
    }

    [Fact]
    public async Task GivesTheCameraItsDefaultGainAndOffsetEachTimeItConnects()
    {
        var optics = CreateService(out var equipment);
        optics.SetConnectDefaults(Camera, 100, 50);
        Assert.Equal((100.0, 50.0), (optics.All.Single(c => c.Camera == Camera).DefaultGain!.Value, optics.All.Single(c => c.Camera == Camera).DefaultOffset!.Value));

        await equipment.ConnectAsync(CancellationToken.None);
        var camera = equipment.Camera!;
        for (var i = 0; i < 100 && camera.Controls.Offset?.Value != 50; i++) await Task.Delay(20);
        Assert.Equal(100, camera.Controls.Gain!.Value);
        Assert.Equal(50, camera.Controls.Offset!.Value);

        // Changed by hand, then reconnected: back to the defaults.
        await camera.ApplyAsync(new Nova.Core.Equipment.CameraSettingsUpdate(Gain: 200, Offset: 10), CancellationToken.None);
        await equipment.DisconnectAsync(CancellationToken.None);
        await equipment.ConnectAsync(CancellationToken.None);
        camera = equipment.Camera!;
        for (var i = 0; i < 100 && camera.Controls.Gain?.Value != 100; i++) await Task.Delay(20);
        Assert.Equal(100, camera.Controls.Gain!.Value);
        await equipment.DisconnectAsync(CancellationToken.None);

        // Cleared: the driver's own are left alone, and that survives a restart.
        optics.SetConnectDefaults(Camera, null, null);
        Assert.Null(CreateService().All.Single(c => c.Camera == Camera).DefaultGain);
        Assert.Throws<ArgumentException>(() => optics.SetConnectDefaults(Camera, -1, 0));
    }

    [Fact]
    public async Task SavesSelectsRenamesAndDeletesPresets()
    {
        var optics = CreateService();
        await optics.SavePresetAsync(Camera, new OpticsPreset("RedCat 51", 250, 51), null, CancellationToken.None);
        await optics.SavePresetAsync(Camera, new OpticsPreset("  RedCat + 0.8x  ", 200, 51), null, CancellationToken.None);

        var info = optics.All.Single(c => c.Camera == Camera);
        Assert.Equal(["RedCat 51", "RedCat + 0.8x"], info.Presets.Select(p => p.Name)); // trimmed
        Assert.Equal("RedCat + 0.8x", info.ActivePreset); // saving makes it active
        Assert.Equal(200, info.FocalLength);
        Assert.Equal(3.88, info.ImageScale!.Value, 2); // 3.76 µm at 200 mm

        await optics.SelectPresetAsync(Camera, "RedCat 51", CancellationToken.None);
        Assert.Equal(250, optics.All.Single(c => c.Camera == Camera).FocalLength);

        // Names are unique (ignoring case), but a preset can keep its own name when edited.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            optics.SavePresetAsync(Camera, new OpticsPreset("redcat 51", 300, 60), "RedCat + 0.8x", CancellationToken.None));
        await optics.SavePresetAsync(Camera, new OpticsPreset("RedCat 51", 255, 51), "RedCat 51", CancellationToken.None);
        await optics.SavePresetAsync(Camera, new OpticsPreset("RedCat 0.8x reducer", 200, 51), "RedCat + 0.8x", CancellationToken.None);
        Assert.Equal(["RedCat 51", "RedCat 0.8x reducer"], optics.All.Single(c => c.Camera == Camera).Presets.Select(p => p.Name));

        // Deleting the active preset falls back to the first remaining one.
        await optics.DeletePresetAsync(Camera, "RedCat 0.8x reducer", CancellationToken.None);
        info = optics.All.Single(c => c.Camera == Camera);
        Assert.Equal("RedCat 51", info.ActivePreset);
        Assert.Equal(255, info.FocalLength);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            optics.SavePresetAsync(Camera, new OpticsPreset(" ", 100, 50), null, CancellationToken.None));

        // Everything survives a restart.
        var reloaded = CreateService().All.Single(c => c.Camera == Camera);
        Assert.Equal("RedCat 51", reloaded.ActivePreset);
        Assert.Single(reloaded.Presets);
    }

    [Fact]
    public async Task CamerasShareThePresets()
    {
        const string Guide = "Simulated Guide Camera";
        var optics = CreateService();
        await optics.SavePresetAsync(Camera, new OpticsPreset("RedCat 51", 250, 51), null, CancellationToken.None);
        await optics.SavePresetAsync(Guide, new OpticsPreset("Guide scope", 120, 30), null, CancellationToken.None);

        // The other camera can pick it without typing it again; each keeps its own choice.
        await optics.SelectPresetAsync(Guide, "RedCat 51", CancellationToken.None);
        var guide = optics.All.Single(c => c.Camera == Guide);
        Assert.Equal(["RedCat 51", "Guide scope"], guide.Presets.Select(p => p.Name));
        Assert.Equal(250, guide.FocalLength);
        Assert.Equal([Camera], guide.Presets.Single(p => p.Name == "RedCat 51").UsedBy!);

        // Changing or renaming it changes it for both.
        await optics.SavePresetAsync(Camera, new OpticsPreset("RedCat 51 + reducer", 200, 51), "RedCat 51", CancellationToken.None);
        guide = optics.All.Single(c => c.Camera == Guide);
        Assert.Equal(("RedCat 51 + reducer", 200d), (guide.ActivePreset, guide.FocalLength!.Value));

        // Names are unique across every camera.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            optics.SavePresetAsync(Guide, new OpticsPreset("guide SCOPE", 100, 25), null, CancellationToken.None));

        // Deleted from one camera: gone for both, and the other is left with none rather than a stranger's optics.
        await optics.DeletePresetAsync(Camera, "RedCat 51 + reducer", CancellationToken.None);
        Assert.Equal("Guide scope", optics.All.Single(c => c.Camera == Camera).ActivePreset);
        Assert.Null(CreateService().All.Single(c => c.Camera == Guide).ActivePreset);
    }

    [Fact]
    public void PoolsPresetsSavedPerCamera()
    {
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_config, "optics.json"), $$"""
            {
              "{{Camera}}": { "Active": "Scope", "Presets": [ { "Name": "Scope", "FocalLength": 250, "Aperture": 51 },
                                                            { "Name": "Spare", "FocalLength": 400, "Aperture": 80 } ], "Gain": 100 },
              "Simulated Guide Camera": { "Active": "Scope", "Presets": [ { "Name": "Scope", "FocalLength": 120, "Aperture": 30 },
                                                                         { "Name": "Spare", "FocalLength": 400, "Aperture": 80 } ] }
            }
            """);

        var all = CreateService().All;
        var main = all.Single(c => c.Camera == Camera);
        var guide = all.Single(c => c.Camera == "Simulated Guide Camera");
        // The same name with the same optics is one preset; with different optics, each gets its camera's name.
        Assert.Equal(["Scope (Simulated Camera)", "Scope (Simulated Guide Camera)", "Spare"], main.Presets.Select(p => p.Name).Order());
        Assert.Equal(("Scope (Simulated Camera)", 250d, 100d), (main.ActivePreset, main.FocalLength!.Value, main.DefaultGain!.Value));
        Assert.Equal(("Scope (Simulated Guide Camera)", 120d), (guide.ActivePreset, guide.FocalLength!.Value));
    }

    [Fact]
    public void MigratesOptionsSavedBeforePresetsExisted()
    {
        Directory.CreateDirectory(_config);
        File.WriteAllText(Path.Combine(_config, "optics.json"), """{ "Simulated Guide Camera": { "FocalLength": 120, "Aperture": 30 } }""");

        var guide = CreateService().All.Single(c => c.Camera == "Simulated Guide Camera");
        Assert.Equal("Default", guide.ActivePreset);
        Assert.Equal((120d, 30d), (guide.FocalLength!.Value, guide.Aperture!.Value));
    }
}
