using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class DriverSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ListsInstalledDriversByNameAndSavesTheChoice()
    {
        var share = Directory.CreateDirectory(Path.Combine(_root, "share")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        File.WriteAllText(Path.Combine(share, "drivers.xml"), """
            <driversList>
              <devGroup group="Telescopes">
                <device label="ZWO AM5" manufacturer="ZWO"><driver name="ZWO AM5">indi_lx200am5</driver><version>1.0</version></device>
                <device label="Celestron NexStar" manufacturer="Celestron"><driver name="Celestron GPS">indi_celestron_gps</driver></device>
              </devGroup>
              <devGroup group="CCDs">
                <device label="ZWO CCD" manufacturer="ZWO"><driver name="ZWO CCD">indi_asi_ccd</driver></device>
              </devGroup>
              <devGroup group="Spectrographs">
                <device label="Alpha Spectro"><driver name="Alpha">indi_alpha_spec</driver></device>
                <device label="Zeta Spectro"><driver name="Zeta">indi_zeta_spec</driver></device>
              </devGroup>
              <devGroup group="Detectors">
                <device label="Middle Detector"><driver name="Middle">indi_middle_det</driver></device>
              </devGroup>
            </driversList>
            """);
        File.WriteAllText(Path.Combine(share, "not-drivers.xml"), "<somethingElse/>");
        foreach (var exec in new[] { "indi_lx200am5", "indi_asi_ccd", "indi_getprop", "indi_homemade_dome" })
            File.WriteAllText(Path.Combine(bin, exec), "");
        var env = Path.Combine(_root, "indi.env");
        File.WriteAllText(env, """
            # INDI drivers started by indiserver.service.
            INDI_DRIVERS="indi_lx200am5 indi_asi_wheel"
            #INDI_DEVICE_NAMES="indi_lx200am5=ZWO AM3"
            """);

        var options = Opts.Create(new NovaOptions
        {
            Backend = "Indi", ConfigDirectory = Path.Combine(_root, "config"), IndiEnvFile = env, IndiDriversFolder = share,
            IndiBinFolders = [bin], IndiPort = 1, // nothing listening: no devices
        });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var setup = new DriverSetupService(new EquipmentService(options, log), options, log);

        var drivers = setup.Setup().Drivers.ToDictionary(d => d.Exec);
        Assert.Equal(["ZWO AM5"], drivers["indi_lx200am5"].Labels);
        Assert.True(drivers["indi_lx200am5"] is { Installed: true, Selected: true, Group: "Telescopes", Manufacturer: "ZWO" });
        Assert.False(drivers["indi_celestron_gps"].Installed); // listed by INDI, not installed here
        Assert.True(drivers["indi_asi_ccd"] is { Installed: true, Selected: false });
        Assert.True(drivers["indi_asi_wheel"] is { Installed: false, Selected: true, Package: var package } && package == DriverSetupService.PackageFor("indi_asi_wheel"));
        Assert.Equal("Other", drivers["indi_homemade_dome"].Group); // installed, not in INDI's lists
        Assert.DoesNotContain("indi_getprop", drivers.Keys); // a tool, not a driver
        Assert.Equal("Telescopes", setup.Setup().Drivers[0].Group); // mounts first
        // Each kind together, even the less common ones (a kind split in two broke the page's list).
        var kinds = setup.Setup().Drivers.Select(d => d.Group).ToList();
        var runs = kinds.Where((k, i) => i == 0 || kinds[i - 1] != k).ToList();
        Assert.Equal(runs.Distinct().Count(), runs.Count);

        await Assert.ThrowsAsync<ArgumentException>(() => setup.ApplyAsync(["rm -rf /"], CancellationToken.None));
        var change = await setup.ApplyAsync(["indi_lx200am5", "indi_asi_ccd"], CancellationToken.None);
        var lines = File.ReadAllLines(env);
        Assert.Contains("INDI_DRIVERS=\"indi_lx200am5 indi_asi_ccd\"", lines);
        Assert.Contains("#INDI_DEVICE_NAMES=\"indi_lx200am5=ZWO AM3\"", lines); // the rest kept
        Assert.Empty(change.Missing);
        Assert.Equal(["indi_lx200am5", "indi_asi_ccd"], setup.SelectedDrivers(out _));
    }

    [Theory]
    [InlineData("indi_eqmod_telescope", true, "libindi-eqmod (AUR)")]
    [InlineData("indi_altair_ccd", true, "libindi-altaircam (AUR)")]
    [InlineData("indi_celestron_aux", true, "libindi-celestronaux (AUR)")]
    [InlineData("indi_celestron_gps", true, "libindi")]
    [InlineData("indi_asi_ccd", true, "libindi-asi-git (AUR)")]
    [InlineData("indi_eqmod_telescope", false, "indi-3rdparty-drivers")]
    [InlineData("indi_lx200am5", false, "indi-bin")]
    public void NamesThePackageForThisSystem(string exec, bool arch, string package) =>
        Assert.Equal(package, DriverSetupService.PackageFor(exec, arch));
}
