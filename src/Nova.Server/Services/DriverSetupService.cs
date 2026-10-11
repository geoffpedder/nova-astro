using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>An INDI driver: what INDI's driver list calls it, and how it stands here.</summary>
/// <param name="Exec">The program, e.g. "indi_lx200am5".</param>
/// <param name="Labels">The devices it drives, as INDI lists them (e.g. "ZWO AM5").</param>
/// <param name="Group">INDI's kind: Telescopes, CCDs, Focusers, Filter Wheels…; "Other" when unknown.</param>
/// <param name="Selected">Listed in indi.env, so indiserver starts it.</param>
/// <param name="Devices">Devices it's serving now (a running driver with nothing plugged in has none).</param>
/// <param name="Package">For a selected driver that isn't installed: the package that probably provides it.</param>
public sealed record DriverEntry(
    string Exec, IReadOnlyList<string> Labels, string? Manufacturer, string Group, bool Installed, bool Selected,
    IReadOnlyList<string> Devices, string? Package);

/// <param name="LiveControl">Drivers can be started and stopped now (indiserver's control pipe); otherwise a restart applies changes.</param>
/// <param name="Writable">NOVA can save the list (install.sh gives it the file).</param>
public sealed record DriverSetup(bool Applicable, bool LiveControl, string EnvFile, bool Writable, string? Problem, IReadOnlyList<DriverEntry> Drivers);

/// <param name="Note">What still needs doing, e.g. restarting indiserver when it couldn't be done live.</param>
public sealed record DriverChange(IReadOnlyList<string> Started, IReadOnlyList<string> Stopped, IReadOnlyList<string> Missing, string? Note);

/// <summary>
/// Choosing which INDI drivers indiserver runs, from the web page: lists what's installed (with INDI's own names for
/// them, from its driver XML files), what's chosen in /etc/nova/indi.env and what's running; saves the choice there
/// (for the next start) and starts or stops drivers straight away through indiserver's control pipe.
/// </summary>
public sealed partial class DriverSetupService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
{
    private readonly NovaOptions _options = options.Value;
    private readonly SemaphoreSlim _applying = new(1, 1);

    private string EnvFile => _options.IndiEnvFile;

    /// <summary>Only for INDI on this machine: the simulator backend has no drivers.</summary>
    public bool Applicable => _options.Backend.Equals("indi", StringComparison.OrdinalIgnoreCase);

    public DriverSetup Setup()
    {
        if (!Applicable) return new DriverSetup(false, false, EnvFile, false, "NOVA is using its own simulator, not INDI", []);
        var catalog = Catalog();
        var selected = SelectedDrivers(out var problem);
        var running = Running();

        var entries = catalog.Values
            .Select(c => Entry(c.Exec, c.Labels, c.Manufacturer, c.Group, selected, running))
            .ToList();
        // Installed drivers INDI's lists don't mention, and chosen ones that aren't installed.
        foreach (var exec in InstalledExecs().Concat(selected).Distinct().Where(e => !catalog.ContainsKey(e)))
            entries.Add(Entry(exec, [], null, "Other", selected, running));

        return new DriverSetup(true, equipment.Provider.Drivers?.Available == true, EnvFile, IsWritable(EnvFile), problem,
            // Kinds in a useful order, each kind together (INDI has more kinds than the usual ones), then by name.
            entries.OrderBy(e => GroupOrder(e.Group)).ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Labels.FirstOrDefault() ?? e.Exec, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private DriverEntry Entry(string exec, IReadOnlyList<string> labels, string? manufacturer, string group,
        IReadOnlyCollection<string> selected, IReadOnlyDictionary<string, List<string>> running)
    {
        var installed = IsInstalled(exec);
        var isSelected = selected.Contains(exec);
        return new DriverEntry(exec, labels, manufacturer, group, installed, isSelected,
            running.TryGetValue(exec, out var devices) ? devices : [], isSelected && !installed ? PackageFor(exec) : null);
    }

    /// <summary>Saves the chosen drivers and starts or stops them now when indiserver allows it.</summary>
    public async Task<DriverChange> ApplyAsync(IReadOnlyList<string> drivers, CancellationToken ct)
    {
        if (!Applicable) throw new InvalidOperationException("NOVA is using its own simulator, not INDI");
        var wanted = drivers.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct().ToList();
        if (wanted.FirstOrDefault(d => !DriverName().IsMatch(d)) is { } bad) throw new ArgumentException($"'{bad}' isn't an INDI driver name");

        await _applying.WaitAsync(ct);
        try
        {
            var before = SelectedDrivers(out _);
            Save(wanted);
            log.Info($"Drivers saved to {EnvFile}: {(wanted.Count == 0 ? "none" : string.Join(" ", wanted))}");

            var missing = wanted.Where(d => !IsInstalled(d)).ToList();
            var toStart = wanted.Where(d => !before.Contains(d) && IsInstalled(d)).ToList();
            var toStop = before.Where(d => !wanted.Contains(d)).ToList();
            if (toStart.Count + toStop.Count == 0) return new DriverChange([], [], missing, null);

            if (equipment.Provider.Drivers is not { Available: true } control)
                return new DriverChange([], [], missing,
                    "Saved. Restart indiserver to use them: sudo systemctl restart indiserver (its control pipe isn't available to do it now)");

            var started = new List<string>();
            var stopped = new List<string>();
            foreach (var driver in toStop)
            {
                await control.StopAsync(driver, ct);
                stopped.Add(driver);
                log.Info($"Stopped {driver}");
            }
            foreach (var driver in toStart)
            {
                await control.StartAsync(driver, ct);
                started.Add(driver);
                log.Info($"Started {driver}");
            }
            return new DriverChange(started, stopped, missing, null);
        }
        finally
        {
            _applying.Release();
        }
    }

    // ---- indi.env ----

    /// <summary>The drivers listed in INDI_DRIVERS="…".</summary>
    public IReadOnlyList<string> SelectedDrivers(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(EnvFile))
            {
                problem = $"{EnvFile} doesn't exist yet: choosing drivers creates it";
                return [];
            }
            foreach (var line in File.ReadAllLines(EnvFile))
                if (DriversLine().Match(line) is { Success: true } m)
                    return m.Groups["list"].Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Couldn't read {EnvFile}: {ex.Message}";
            return [];
        }
    }

    /// <summary>Rewrites the INDI_DRIVERS line, keeping everything else in the file (comments, device names).</summary>
    private void Save(IReadOnlyList<string> drivers)
    {
        var line = $"INDI_DRIVERS=\"{string.Join(' ', drivers)}\"";
        try
        {
            var lines = File.Exists(EnvFile) ? File.ReadAllLines(EnvFile).ToList() : ["# INDI drivers started by indiserver.service. Chosen in NOVA (Equipment → Drivers…)."];
            var at = lines.FindIndex(l => DriversLine().IsMatch(l));
            if (at >= 0) lines[at] = line;
            else lines.Add(line);
            File.WriteAllLines(EnvFile, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"NOVA can't save {EnvFile} ({ex.Message}). Re-run deploy/install.sh, which lets NOVA save it, or: sudo chown $USER {EnvFile}");
        }
    }

    private static bool IsWritable(string path)
    {
        try
        {
            if (!File.Exists(path)) return Directory.Exists(Path.GetDirectoryName(path)) && !OperatingSystem.IsWindows();
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- What's installed and running ----

    private sealed record CatalogEntry(string Exec, List<string> Labels, string? Manufacturer, string Group);

    /// <summary>INDI's driver lists (/usr/share/indi/*.xml: drivers.xml and one per third-party package).</summary>
    private Dictionary<string, CatalogEntry> Catalog()
    {
        var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(_options.IndiDriversFolder)) return catalog;
        foreach (var file in Directory.GetFiles(_options.IndiDriversFolder, "*.xml"))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                continue; // not a driver list (or unreadable): skip it
            }
            if (doc.Root?.Name.LocalName != "driversList") continue;
            foreach (var group in doc.Root.Elements("devGroup"))
            {
                var groupName = (string?)group.Attribute("group") ?? "Other";
                foreach (var device in group.Elements("device"))
                {
                    var exec = device.Element("driver")?.Value.Trim();
                    if (string.IsNullOrEmpty(exec) || !DriverName().IsMatch(exec)) continue;
                    var label = ((string?)device.Attribute("label"))?.Trim();
                    var manufacturer = ((string?)device.Attribute("manufacturer"))?.Trim();
                    if (!catalog.TryGetValue(exec, out var entry))
                        catalog[exec] = entry = new CatalogEntry(exec, [], string.IsNullOrEmpty(manufacturer) ? null : manufacturer, groupName);
                    if (!string.IsNullOrEmpty(label) && !entry.Labels.Contains(label)) entry.Labels.Add(label);
                }
            }
        }
        return catalog;
    }

    private IEnumerable<string> InstalledExecs() =>
        _options.IndiBinFolders.Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "indi_*"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => DriverName().IsMatch(name) && !NotDrivers.Contains(name));

    // INDI's command-line tools share the prefix but aren't drivers.
    private static readonly HashSet<string> NotDrivers = ["indi_getprop", "indi_setprop", "indi_eval", "indi_watchdog"];

    public bool IsInstalled(string exec) => _options.IndiBinFolders.Any(folder => File.Exists(Path.Combine(folder, exec)));

    /// <summary>Driver program → the devices it's serving now (each INDI device names its driver).</summary>
    private Dictionary<string, List<string>> Running()
    {
        var running = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (equipment.Provider.Drivers is not { } control) return running;
        foreach (var device in equipment.Provider.Devices)
            if (control.DriverOf(device.Name) is { } exec)
            {
                if (!running.TryGetValue(exec, out var list)) running[exec] = list = [];
                list.Add(device.Name);
            }
        return running;
    }

    /// <summary>Arch (pacman and the AUR) rather than Debian, Raspberry Pi OS or Ubuntu (apt).</summary>
    public static readonly bool IsArch = File.Exists("/usr/bin/pacman");

    /// <summary>
    /// The package that provides a driver. On Arch, INDI itself is <c>libindi</c> and each maker's drivers are an AUR
    /// package (as deploy/setup.sh and doctor.sh name them); on Debian, Raspberry Pi OS and Ubuntu all third-party
    /// drivers come in one package.
    /// </summary>
    public static string PackageFor(string exec) => PackageFor(exec, IsArch);

    public static string PackageFor(string exec, bool arch)
    {
        var aur = AurPackage(exec);
        var core = aur is null && IsCore(exec);
        if (arch)
            return aur is not null ? $"{aur} (AUR)" : core ? "libindi" : "libindi, or its maker's libindi-… package (AUR)";
        return aur is not null ? "indi-3rdparty-drivers" : core ? "indi-bin" : "indi-bin or indi-3rdparty-drivers";
    }

    /// <summary>The command that installs a driver, for the setup guide and doctor.sh's fixes.</summary>
    public static string InstallCommand(string exec, bool arch)
    {
        // A partly updated Arch can't install AUR drivers pinned to the current libindi, hence -Syu first.
        if (arch) return AurPackage(exec) is { } aur ? $"sudo pacman -Syu && yay -S {aur}" : "sudo pacman -Syu libindi";
        return AurPackage(exec) is not null || !IsCore(exec) ? "sudo apt install indi-3rdparty-drivers" : "sudo apt install indi-bin";
    }

    private static bool IsCore(string exec) =>
        exec.StartsWith("indi_pegasus") || exec.StartsWith("indi_simulator") || exec.StartsWith("indi_lx200")
        || exec.StartsWith("indi_celestron") || exec.StartsWith("indi_ioptron");

    /// <summary>The AUR package with a third-party driver (on Debian and Ubuntu they're all in indi-3rdparty-drivers).</summary>
    private static string? AurPackage(string exec) => exec switch
    {
        _ when exec.StartsWith("indi_asi") => "libindi-asi-git",
        _ when exec.StartsWith("indi_qhy") => "libindi-qhy",
        _ when exec.StartsWith("indi_playerone") || exec.StartsWith("indi_pomodoro") => "libindi-playerone",
        _ when exec.StartsWith("indi_svbony") || exec.StartsWith("indi_sv305") => "libindi-svbony",
        _ when exec.StartsWith("indi_eqmod") || exec.StartsWith("indi_azgti") || exec.StartsWith("indi_staradventurer") || exec.StartsWith("indi_skywatcherAltAz") => "libindi-eqmod",
        _ when exec.StartsWith("indi_gphoto") || exec.StartsWith("indi_canon") || exec.StartsWith("indi_nikon") || exec.StartsWith("indi_sony") || exec.StartsWith("indi_pentax_ccd") => "libindi-gphoto",
        _ when exec.StartsWith("indi_toupcam") => "libindi-toupcam",
        _ when exec.StartsWith("indi_altair") => "libindi-altaircam",
        _ when exec.StartsWith("indi_ogmacam") => "libindi-ogmacam",
        _ when exec.StartsWith("indi_omegonpro") => "libindi-omegonprocam",
        _ when exec.StartsWith("indi_starshootg") => "libindi-starshootg",
        _ when exec.StartsWith("indi_tscam") => "libindi-tscam",
        _ when exec.StartsWith("indi_mallincam") => "libindi-mallincam",
        _ when exec.StartsWith("indi_nncam") => "libindi-nncam",
        _ when exec.StartsWith("indi_atik") => "libindi-atik",
        _ when exec.StartsWith("indi_mi_") => "libindi-mi",
        _ when exec.StartsWith("indi_sx") => "libindi-sx",
        _ when exec.StartsWith("indi_fli") => "libindi-fli",
        _ when exec.StartsWith("indi_sbig") => "libindi-sbig",
        _ when exec.StartsWith("indi_celestron_aux") => "libindi-celestronaux",
        _ => null,
    };

    private static int GroupOrder(string group) => group switch
    {
        "Telescopes" => 0,
        "CCDs" => 1,
        "Focusers" => 2,
        "Filter Wheels" => 3,
        "Rotators" => 4,
        "Auxiliary" => 5,
        "Domes" => 6,
        "Weather" => 7,
        "Other" => 99,
        _ => 50,
    };

    [GeneratedRegex(@"^indi_[A-Za-z0-9_\-]+$")]
    private static partial Regex DriverName();

    [GeneratedRegex(@"^\s*(?:export\s+)?INDI_DRIVERS\s*=\s*[""']?(?<list>[^""'#]*)[""']?\s*(?:#.*)?$")]
    private static partial Regex DriversLine();
}
