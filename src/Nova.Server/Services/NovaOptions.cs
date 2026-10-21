namespace Nova.Server.Services;

/// <summary>Bound from the "Nova" section of appsettings.json (or env vars like Nova__Backend=Indi).</summary>
public sealed class NovaOptions
{
    /// <summary>"Simulator" (built-in, no INDI needed) or "Indi".</summary>
    public string Backend { get; set; } = "Simulator";
    public string IndiHost { get; set; } = "localhost";
    public int IndiPort { get; set; } = 7624;
    /// <summary>indiserver's control pipe for starting/stopping drivers (created by deploy/indiserver-start.sh).</summary>
    public string IndiControlFifo { get; set; } = "/run/nova-indi/control";
    /// <summary>The drivers indiserver.service starts (INDI_DRIVERS), chosen in Equipment → Drivers….</summary>
    public string IndiEnvFile { get; set; } = "/etc/nova/indi.env";
    /// <summary>INDI's driver lists (drivers.xml and one per third-party package), for the drivers' proper names.</summary>
    public string IndiDriversFolder { get; set; } = "/usr/share/indi";
    /// <summary>Where driver programs are installed.</summary>
    public string[] IndiBinFolders { get; set; } = ["/usr/bin", "/usr/local/bin"];
    /// <summary>Where FITS files are saved. Empty means ~/Pictures/Nova.</summary>
    public string? ImageDirectory { get; set; }
    /// <summary>
    /// For development without a USB drive: a folder that NOVA treats as a plugged-in USB drive (it "appears" while
    /// the folder exists). Empty on the mini PC, where real drives are found and mounted through udisks2.
    /// </summary>
    public string? SimulatedUsbDrive { get; set; }
    /// <summary>PHD2's event server, for the Guider panel (PHD2: Tools ▸ Enable Server).</summary>
    public string Phd2Host { get; set; } = "localhost";
    public int Phd2Port { get; set; } = 4400;
    /// <summary>Keep trying to connect to PHD2 in the background (it runs as a service on the mini PC).</summary>
    public bool Phd2AutoConnect { get; set; } = true;
    /// <summary>noVNC port for viewing PHD2's virtual screen in a browser (deploy/nova-screen.service); 0 hides the link.</summary>
    public int Phd2ScreenPort { get; set; } = 6080;
    /// <summary>x11vnc's port for PHD2's virtual screen (localhost only), relayed through NOVA for the PHD2 tab.</summary>
    public int Phd2VncPort { get; set; } = 5999;
    /// <summary>noVNC's web client folder; empty to look in the usual places (/usr/share/webapps/novnc…).</summary>
    public string? NoVncPath { get; set; }
    /// <summary>"auto" (simulator solver for the built-in simulator, else ASTAP), "astap" or "simulator".</summary>
    public string Solver { get; set; } = "auto";
    /// <summary>ASTAP program; empty to look in /opt/astap, /usr/bin and the usual Windows folder.</summary>
    public string? AstapPath { get; set; }
    /// <summary>Folder with ASTAP's star databases; empty for ASTAP's own folder.</summary>
    public string? AstapDatabaseFolder { get; set; }
    /// <summary>Database to use, e.g. "d20"; empty to let ASTAP pick from what's installed.</summary>
    public string? AstapDatabase { get; set; }
    /// <summary>How far from the hint (degrees) the solver searches.</summary>
    public double SolveSearchRadius { get; set; } = 30;
    /// <summary>Exposure for centring frames, seconds.</summary>
    public double SolveExposureSeconds { get; set; } = 5;
    /// <summary>Centring stops when the target is within this many arcminutes of the image centre.</summary>
    public double CenterToleranceArcmin { get; set; } = 1;
    public int CenterMaxAttempts { get; set; } = 5;

    /// <summary>
    /// TCP port where NOVA acts as a telescope for Stellarium's Telescope Control plugin ("External software
    /// or a remote computer"); 0 turns it off.
    /// </summary>
    public int StellariumPort { get; set; } = 10001;
    /// <summary>What a GoTo from Stellarium does: "Center" (slew, then plate solve until centred) or "Slew".</summary>
    public string StellariumGoto { get; set; } = "Center";
    /// <summary>The coordinate system set for the telescope in Stellarium: "J2000" (its default) or "JNow".</summary>
    public string StellariumEquinox { get; set; } = "J2000";
    /// <summary>
    /// The serial port NOVA's Bluetooth helper offers for a ZWO mount connected over Bluetooth (a symlink to a
    /// pseudo-terminal), which the INDI driver opens. /run/nova is nova.service's runtime folder.
    /// </summary>
    public string MountBluetoothLink { get; set; } = "/run/nova/mount-bluetooth";
    /// <summary>Python 3 for the Bluetooth helper; empty for python3 (python on Windows).</summary>
    public string? Python { get; set; }

    /// <summary>Longest side of the browser preview, in pixels.</summary>
    public int PreviewMaxSize { get; set; } = 1600;

    /// <summary>Where NOVA keeps its own state. Empty means ~/.config/nova on Linux.</summary>
    public string? ConfigDirectory { get; set; }

    public string ResolveConfigDirectory() =>
        string.IsNullOrWhiteSpace(ConfigDirectory) ? DefaultConfigDirectory() : ConfigDirectory;

    /// <summary>
    /// ~/.config/nova on Linux ($XDG_CONFIG_HOME), nova in %APPDATA% on Windows. Asked for without checking it exists:
    /// .NET otherwise answers "" when ~/.config isn't there yet (a fresh Ubuntu Server user), which made the
    /// settings folder a bare "nova" relative to /opt/nova, where nothing can be saved.
    /// </summary>
    public static string DefaultConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".config");
        return Path.Combine(appData, "nova");
    }

    public string ResolveImageDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(ImageDirectory)) return Path.Combine(home, "Pictures", "Nova");
        return ImageDirectory.StartsWith('~') ? Path.Combine(home, ImageDirectory[1..].TrimStart('/', '\\')) : ImageDirectory;
    }
}
