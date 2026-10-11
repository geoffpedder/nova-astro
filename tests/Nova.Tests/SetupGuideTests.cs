using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class SetupGuideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ListsChosenDriversThatArentInstalledWithTheFix()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "indi_lx200am5"), "");
        var env = Path.Combine(_root, "indi.env");
        File.WriteAllText(env, "INDI_DRIVERS=\"indi_lx200am5 indi_eqmod_telescope\"\n");
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Indi", ConfigDirectory = Path.Combine(_root, "config"), IndiEnvFile = env,
            IndiDriversFolder = Path.Combine(_root, "share"), IndiBinFolders = [bin], IndiPort = 1, Solver = "simulator",
        });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, new NullHubContext(), options);
        var guide = new SetupGuideService(options, new DriverSetupService(equipment, options, log),
            new PlateSolveService(equipment, imaging, optics, log, options), log);

        var check = guide.Check();

        Assert.True(check.DriversApplicable);
        Assert.Equal(2, check.DriversChosen);
        Assert.Equal(1, check.DriversInstalled);
        var missing = Assert.Single(check.Problems, p => p.Text.Contains("indi_eqmod_telescope"));
        Assert.False(missing.Optional);
        Assert.StartsWith(DriverSetupService.InstallCommand("indi_eqmod_telescope", DriverSetupService.IsArch), missing.Fix);
        Assert.False(check.SolvedOnce);
    }

    [Fact]
    public async Task RemembersThatASolveWorkedAndThatTheGuideWasPutAway()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _root, SolveExposureSeconds = 0 });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var drivers = new DriverSetupService(equipment, options, log);
        var guide = new SetupGuideService(options, drivers, solver, log);
        Assert.False(guide.Check().DriversApplicable); // NOVA's own simulator needs no drivers

        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        await imaging.CaptureAsync(0.5, save: false, CancellationToken.None);
        Assert.True((await solver.SolveLatestAsync(CancellationToken.None)).Solved);
        guide.SetHidden(true);

        // A fresh start (as after a restart) still knows.
        var again = new SetupGuideService(options, drivers, solver, log).Check();
        Assert.True(again.SolvedOnce);
        Assert.True(again.Hidden);
    }

    [Theory]
    [InlineData("indi_eqmod_telescope", true, "sudo pacman -Syu && yay -S libindi-eqmod")]
    [InlineData("indi_lx200am5", true, "sudo pacman -Syu libindi")]
    [InlineData("indi_eqmod_telescope", false, "sudo apt install indi-3rdparty-drivers")]
    [InlineData("indi_simulator_ccd", false, "sudo apt install indi-bin")]
    public void InstallCommandsSuitTheSystem(string exec, bool arch, string command) =>
        Assert.Equal(command, DriverSetupService.InstallCommand(exec, arch));
}
