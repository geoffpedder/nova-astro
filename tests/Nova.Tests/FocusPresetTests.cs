using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class FocusPresetTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private FocusPresetService Create()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        return new FocusPresetService(new EquipmentService(options, log), options, log);
    }

    [Fact]
    public void RemembersPositionsPerFocuserAcrossRestarts()
    {
        var presets = Create();
        presets.Save("EAF", new FocusPreset("Askar 71F", 12_400), null);
        presets.Save("EAF", new FocusPreset(" Askar 71F + reducer ", 9_850), null);
        presets.Save("Other", new FocusPreset("Askar 71F", 500), null);

        var reloaded = Create();
        Assert.Equal([new("Askar 71F", 12_400), new("Askar 71F + reducer", 9_850)], reloaded.For("EAF"));
        Assert.Single(reloaded.For("Other"));
        Assert.Empty(reloaded.For("Nobody"));
    }

    [Fact]
    public void UpdatesRenamesAndDeletes()
    {
        var presets = Create();
        presets.Save("EAF", new FocusPreset("Reducer", 9_000), null);
        presets.Save("EAF", new FocusPreset("reducer", 9_120), null); // same name, any case: update
        Assert.Equal([new("reducer", 9_120)], presets.For("EAF"));

        presets.Save("EAF", new FocusPreset("0.8× reducer", 9_120), originalName: "reducer");
        Assert.Equal([new("0.8× reducer", 9_120)], presets.For("EAF"));

        presets.Save("EAF", new FocusPreset("Native", 12_000), null);
        Assert.Throws<ArgumentException>(() => presets.Save("EAF", new FocusPreset("Native", 1), originalName: "0.8× reducer"));
        Assert.Throws<ArgumentException>(() => presets.Save("EAF", new FocusPreset("  ", 1), null));

        presets.Delete("EAF", "Native");
        Assert.Single(presets.For("EAF"));
        Assert.Throws<ArgumentException>(() => presets.Delete("EAF", "Native"));
    }
}
