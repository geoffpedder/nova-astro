using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>Something on the mini PC that stops part of NOVA working, with the command that fixes it.</summary>
/// <param name="Optional">NOVA works without it (e.g. PHD2): shown as a note, not a problem.</param>
public sealed record SetupProblem(string Text, string? Fix, bool Optional);

/// <param name="SolvedOnce">A plate solve has worked here at least once (the guide's last required step).</param>
/// <param name="Hidden">The user put the guide away; it stays a small "Setup" link.</param>
/// <param name="DriversApplicable">INDI on this machine (NOVA's own simulator needs no drivers).</param>
/// <param name="DriversChosen">Drivers chosen in Equipment → Drivers….</param>
/// <param name="DriversInstalled">How many of those are installed.</param>
public sealed record SetupCheck(
    bool SolvedOnce, bool Hidden, bool DriversApplicable, int DriversChosen, int DriversInstalled,
    IReadOnlyList<SetupProblem> Problems);

/// <summary>
/// The first-run guide's view of the mini PC: what only the server can see (installed drivers and programs), much as
/// deploy/doctor.sh checks it, plus two things it remembers in setup.json: that a plate solve has worked, and whether
/// the guide was put away. Everything else the guide ticks (site, connection, optics) is in the status already.
/// </summary>
public sealed class SetupGuideService
{
    private readonly NovaOptions _options;
    private readonly DriverSetupService _drivers;
    private readonly PlateSolveService _solver;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private State _state;

    private sealed record State(bool SolvedOnce = false, bool Hidden = false);

    public SetupGuideService(IOptions<NovaOptions> options, DriverSetupService drivers, PlateSolveService solver, ActivityLog log)
    {
        _options = options.Value;
        _drivers = drivers;
        _solver = solver;
        _log = log;
        _file = Path.Combine(_options.ResolveConfigDirectory(), "setup.json");
        _state = Load();
        solver.Solved += () =>
        {
            if (!_state.SolvedOnce) Update(_state with { SolvedOnce = true });
        };
    }

    public bool SolvedOnce => _state.SolvedOnce;

    public void SetHidden(bool hidden) => Update(_state with { Hidden = hidden });

    public SetupCheck Check()
    {
        var problems = new List<SetupProblem>();
        var setup = _drivers.Setup();
        var chosen = setup.Drivers.Where(d => d.Selected).ToList();
        var arch = DriverSetupService.IsArch;

        if (setup.Applicable)
        {
            foreach (var driver in chosen.Where(d => !d.Installed))
                problems.Add(new SetupProblem(
                    $"{driver.Labels.FirstOrDefault() ?? driver.Exec} ({driver.Exec}) is chosen but not installed",
                    $"{DriverSetupService.InstallCommand(driver.Exec, arch)}   (or untick it in Drivers…)", false));
            if (setup.Problem is { } driverProblem)
                problems.Add(new SetupProblem(driverProblem, null, false));
            else if (!setup.Writable)
                problems.Add(new SetupProblem($"NOVA can't save the driver choice to {setup.EnvFile}",
                    $"sudo chown $USER {setup.EnvFile}   (or: nova update --force)", false));
        }

        if (_solver.Status.Problem is { } solverProblem)
            problems.Add(new SetupProblem(solverProblem, null, false)); // its text says how to fix it

        // Guiding and the simulator's stars matter only on the mini PC (Linux).
        if (OperatingSystem.IsLinux())
        {
            if (!OnPath("phd2"))
                problems.Add(new SetupProblem("PHD2 isn't installed, so there's no guiding", "nova setup   (answer yes to PHD2)", true));
            else if (_options.Phd2ScreenPort > 0 && Api.Phd2ScreenApi.NoVncFolder(_options) is null)
                problems.Add(new SetupProblem("noVNC isn't installed, so PHD2's screen can't be shown", "nova setup", true));

            if (chosen.Any(d => d.Exec == "indi_simulator_ccd") && !HasGsc())
                problems.Add(new SetupProblem("The CCD Simulator draws no stars without the GSC star catalog",
                    arch ? "see \"GSC by hand\" in NOVA's docs/INSTALL.md" : "sudo apt install gsc", true));
        }

        return new SetupCheck(_state.SolvedOnce, _state.Hidden, setup.Applicable, chosen.Count, chosen.Count(d => d.Installed), problems);
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':').Append("/usr/bin")
            .Any(folder => File.Exists(Path.Combine(folder, program)));

    private static bool HasGsc() =>
        Directory.Exists("/usr/share/GSC") && Directory.EnumerateDirectories("/usr/share/GSC", "N*").Any();

    private void Update(State state)
    {
        lock (_sync)
        {
            _state = state;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                _log.Warn($"Couldn't save {_file}: {ex.Message}");
            }
        }
    }

    private State Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) ?? new State();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new State();
    }
}
