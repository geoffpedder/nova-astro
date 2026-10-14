using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>The hotspot's name and password, and when it starts and stops by itself.</summary>
/// <param name="Fallback">Start it when no Wi-Fi (or cable with a route to the router) is found for a while.</param>
/// <param name="Retry">While nobody is on it, try joining Wi-Fi again every few minutes.</param>
public sealed record HotspotSettings(string Ssid, string Password, bool Fallback = true, bool Retry = true);

/// <param name="State">"Off", "Starting", "On", "Stopping" or "Error".</param>
/// <param name="Configured">A password is set, so it can start.</param>
/// <param name="Stations">Phones and laptops joined to it now (when on and known).</param>
/// <param name="Method">How this machine runs it: "networkd", "networkmanager", "iwd", "simulator" or "none".</param>
/// <param name="Problem">Why it can't run here (no Wi-Fi adapter, hostapd missing, not installed), or null.</param>
/// <param name="Reason">Why it's on: no Wi-Fi found, or started from the page.</param>
/// <param name="StartsInSeconds">With no connection and the fallback on: how long until it starts.</param>
public sealed record HotspotStatus(
    string State, string Ssid, bool Configured, bool Fallback, bool Retry, int? Stations, string Method, string? Problem,
    string? Error, string Address, string? Reason, DateTime? OnSinceUtc, bool Manual, int? StartsInSeconds);

/// <summary>What this machine offers for a hotspot: the way it's run, the Wi-Fi adapter, and any reason it can't.</summary>
public sealed record HotspotEnvironment(string Method, string? Interface, string? Problem);

/// <summary>Starts and stops the hotspot, and says whether it's up and who's on it.</summary>
public interface IHotspotControl
{
    Task<HotspotEnvironment> DescribeAsync(CancellationToken ct);
    Task<bool> IsActiveAsync(CancellationToken ct);
    /// <summary>How many devices are joined, or null if it can't tell.</summary>
    Task<int?> StationsAsync(string? device, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// The Wi-Fi hotspot fallback: when the mini PC can't find a known Wi-Fi network (at a dark site, say) it starts its own
/// hotspot, with the name and password set in NOVA, so a phone or laptop can still reach it at 10.42.0.1. While nobody
/// is on it, it tries Wi-Fi again every few minutes; started from the page and joined by nobody, it goes back to Wi-Fi
/// by itself, so trying it out at home can't strand the mini PC. The switching is done as root by
/// nova-hotspot.service (deploy/nova-hotspot.sh), which NOVA may start and stop through a polkit rule.
/// </summary>
public sealed class HotspotService : BackgroundService
{
    public const string Address = "10.42.0.1";
    public static readonly TimeSpan FallbackAfter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ManualGrace = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DescribeEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(10);

    private readonly ActivityLog _log;
    private readonly NetworkService? _network;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private HotspotSettings _settings;

    private string _state = "Off";
    private string? _error;
    private string? _reason;
    private DateTime? _onSince;
    private DateTime? _lastBusy; // last time someone was on it (or when it started)
    private bool _manual;
    private bool _anyoneJoined;
    private bool _retrying;
    private DateTime? _disconnectedSince;
    private DateTime _lastFailure = DateTime.MinValue;
    private int? _stations;
    private HotspotEnvironment _environment = new("none", null, null);
    private DateTime _describedUtc = DateTime.MinValue;

    public HotspotService(IOptions<NovaOptions> options, ActivityLog log, NetworkService? network = null)
    {
        _log = log;
        _network = network;
        var directory = options.Value.ResolveConfigDirectory();
        _file = Path.Combine(directory, "hotspot.json");
        var simulator = !options.Value.Backend.Equals("Indi", StringComparison.OrdinalIgnoreCase);
        Control = simulator || !OperatingSystem.IsLinux() ? new SimulatedHotspot() : new SystemdHotspot();
        // The root helper reads the name and password from here; the install makes it NOVA's to write.
        ConfFile = OperatingSystem.IsLinux() && !simulator ? "/etc/nova/hotspot.conf" : Path.Combine(directory, "hotspot.conf");
        _settings = Load();
    }

    public IHotspotControl Control { get; set; }
    public string ConfFile { get; set; }
    /// <summary>The clock (replaceable in tests).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    /// <summary>How the mini PC is connected now (replaceable in tests).</summary>
    public Func<CancellationToken, Task<NetworkStatus>>? ReadNetwork { get; set; }

    public HotspotSettings Settings
    {
        get
        {
            lock (_sync) return _settings;
        }
    }

    public static string DefaultSsid()
    {
        var name = Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(Environment.MachineName)).Replace('?', '-');
        var ssid = $"NOVA-{name}";
        return ssid.Length > 32 ? ssid[..32] : ssid;
    }

    public HotspotStatus Status
    {
        get
        {
            lock (_sync)
            {
                var s = _settings;
                int? startsIn = null;
                if (_state is "Off" or "Error" && s.Fallback && Configured(s) && _disconnectedSince is { } since && _environment.Problem is null)
                    startsIn = (int)Math.Max(0, Math.Ceiling(((_retrying ? RetryWait : FallbackAfter) - (Now() - since)).TotalSeconds));
                return new HotspotStatus(_state, s.Ssid, Configured(s), s.Fallback, s.Retry, _state == "On" ? _stations : null,
                    _environment.Method, _environment.Problem, _error, Address, _state == "On" ? _reason : null, _onSince, _manual, startsIn);
            }
        }
    }

    private static bool Configured(HotspotSettings s) => s.Password.Length >= 8;

    // ---- Settings ----

    /// <summary>Checks and saves the name and password (they take effect the next time it starts).</summary>
    public void UpdateSettings(HotspotSettings settings)
    {
        var ssid = settings.Ssid.Trim();
        if (ssid.Length is 0 or > 32 || !Printable(ssid)) throw new ArgumentException("The hotspot's name must be 1 to 32 ordinary characters (letters, numbers, spaces, - and so on)");
        if (settings.Password.Length > 0 && (settings.Password.Length is < 8 or > 63 || !Printable(settings.Password)))
            throw new ArgumentException("The password must be 8 to 63 ordinary characters (no accents or emoji)");
        var next = settings with { Ssid = ssid };
        WriteConf(next); // first: if the helper can't be told, don't pretend it's saved
        lock (_sync) _settings = next;
        Save(next);
    }

    /// <summary>Plain ASCII, what every Wi-Fi tool takes in a name or a passphrase.</summary>
    private static bool Printable(string text) => text.All(c => c is >= ' ' and <= '~');

    private HotspotSettings Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<HotspotSettings>(File.ReadAllText(_file), Json) is { } saved)
                return saved with { Ssid = string.IsNullOrWhiteSpace(saved.Ssid) ? DefaultSsid() : saved.Ssid, Password = saved.Password ?? "" };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new HotspotSettings(DefaultSsid(), "");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private void Save(HotspotSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save the hotspot settings to {_file}", ex);
        }
    }

    /// <summary>The name and password for the root helper, as plain KEY=value lines (it reads them as data).</summary>
    public static string ConfText(HotspotSettings s) => $"# NOVA's hotspot (set in NOVA: the network badge in the header)\nSSID={s.Ssid}\nPASSPHRASE={s.Password}\n";

    private void WriteConf(HotspotSettings settings)
    {
        try
        {
            File.WriteAllText(ConfFile, ConfText(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Couldn't save the hotspot's settings for the mini PC ({ex.Message}): run nova update --force once (it gives NOVA {ConfFile})");
        }
    }

    // ---- Starting and stopping ----

    /// <summary>Starts the hotspot from the page (it goes back to Wi-Fi if nobody joins within 3 minutes).</summary>
    public async Task StartHotspotAsync(CancellationToken ct)
    {
        var s = Settings;
        if (!Configured(s)) throw new InvalidOperationException("Set the hotspot's password first");
        await DescribeAsync(ct, force: true);
        if (_environment.Problem is { } problem) throw new InvalidOperationException(problem);
        await _busy.WaitAsync(ct);
        try
        {
            await StartCoreAsync(manual: true, "Started from the page", ct);
            if (_state == "Error") throw new InvalidOperationException(_error ?? "The hotspot didn't start");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Stops the hotspot and goes back to joining Wi-Fi (if none is found, the fallback starts it again).</summary>
    public async Task StopHotspotAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            await StopCoreAsync("Back to Wi-Fi (from the page)", retrying: false, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task StartCoreAsync(bool manual, string reason, CancellationToken ct)
    {
        var s = Settings;
        lock (_sync)
        {
            _state = "Starting";
            _error = null;
        }
        try
        {
            WriteConf(s); // the helper reads the latest name and password
            await Control.StartAsync(ct);
            var now = Now();
            lock (_sync)
            {
                _state = "On";
                _reason = reason;
                _manual = manual;
                _anyoneJoined = false;
                _onSince = now;
                _lastBusy = now;
                _stations = 0;
                _disconnectedSince = null;
            }
            _log.Info($"{reason}: started the hotspot {s.Ssid}. Join it and open http://{Address}:5000 (or http://{Environment.MachineName}.local:5000)" +
                      (manual ? ". If nobody joins within 3 minutes it goes back to Wi-Fi" : ""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            lock (_sync)
            {
                _state = "Error";
                _error = ex.Message;
                _lastFailure = Now();
            }
            _log.Error($"The hotspot didn't start: {ex.Message}");
        }
    }

    private async Task StopCoreAsync(string reason, bool retrying, CancellationToken ct)
    {
        lock (_sync) _state = "Stopping";
        try
        {
            await Control.StopAsync(ct);
            _log.Info($"{reason}: hotspot off, joining Wi-Fi");
        }
        catch (InvalidOperationException ex)
        {
            _log.Error($"The hotspot didn't stop: {ex.Message}");
            lock (_sync)
            {
                _state = "Error";
                _error = ex.Message;
            }
            return;
        }
        lock (_sync)
        {
            _state = "Off";
            _error = null;
            _reason = null;
            _onSince = null;
            _manual = false;
            _stations = null;
            _retrying = retrying;
            _disconnectedSince = null; // counted from the next look that finds no Wi-Fi: then the fallback starts it again
        }
    }

    // ---- Deciding ----

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Settings saved before the install gave NOVA the helper's file: write it now.
        if (Configured(Settings))
            try
            {
                WriteConf(Settings);
            }
            catch (InvalidOperationException ex)
            {
                _log.Warn(ex.Message);
            }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"Hotspot check failed: {ex.Message}");
            }
            await Task.Delay(TickEvery, stoppingToken);
        }
    }

    /// <summary>One look at the network and the hotspot, and what to do about it (every 10 s; called directly in tests).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return; // a start or stop from the page is under way
        try
        {
            await DescribeAsync(ct);
            var now = Now();
            var s = Settings;
            if (await Control.IsActiveAsync(ct))
            {
                var stations = await Control.StationsAsync(_environment.Interface, ct) ?? 0;
                bool manualTimedOut, retryDue;
                lock (_sync)
                {
                    if (_state != "On")
                    {
                        // On already (NOVA restarted while it ran): carry on from now.
                        _state = "On";
                        _reason ??= "No Wi-Fi found";
                        _onSince ??= now;
                        _lastBusy ??= now;
                    }
                    _stations = stations;
                    if (stations > 0)
                    {
                        _anyoneJoined = true;
                        _lastBusy = now;
                    }
                    manualTimedOut = _manual && !_anyoneJoined && now - _onSince >= ManualGrace;
                    retryDue = !manualTimedOut && s.Retry && stations == 0 && (!_manual || _anyoneJoined) && now - _lastBusy >= RetryEvery;
                }
                if (manualTimedOut) await StopCoreAsync("Nobody joined the hotspot within 3 minutes", retrying: false, ct);
                else if (retryDue) await StopCoreAsync("Nobody on the hotspot: trying Wi-Fi again", retrying: true, ct);
                return;
            }

            lock (_sync)
            {
                if (_state is "On" or "Starting")
                {
                    // It stopped without NOVA asking (hostapd failed, the adapter went away): Wi-Fi is back (the service
                    // restores it); start again only after a pause.
                    _state = "Error";
                    _error = "The hotspot stopped by itself (details: journalctl -u nova-hotspot)";
                    _lastFailure = now;
                    _onSince = null;
                    _manual = false;
                    _stations = null;
                    _log.Warn(_error);
                }
            }

            var network = ReadNetwork is { } read ? await read(ct) : _network is { } service ? await service.RefreshAsync(ct) : await NetworkService.ReadAsync(ct);
            if (Connected(network))
            {
                lock (_sync)
                {
                    _disconnectedSince = null;
                    _retrying = false;
                    if (_state == "Error" && now - _lastFailure > AfterFailure) _state = "Off";
                }
                return;
            }
            bool start;
            lock (_sync)
            {
                _disconnectedSince ??= now;
                var wait = _retrying ? RetryWait : FallbackAfter;
                start = s.Fallback && Configured(s) && _environment.Problem is null && now - _disconnectedSince >= wait && now - _lastFailure >= AfterFailure;
            }
            if (start) await StartCoreAsync(manual: false, _retrying ? "Wi-Fi still not found" : "No Wi-Fi found", ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Joined to a Wi-Fi network, or a cable with a route to the router: no hotspot needed.</summary>
    public static bool Connected(NetworkStatus network) =>
        network.Interfaces.Any(i => (i.Kind == "wifi" && i.Mode != "hotspot" && i.Ssid is not null) || (i.Kind == "wired" && i.Gateway));

    private async Task DescribeAsync(CancellationToken ct, bool force = false)
    {
        if (!force && Now() - _describedUtc < DescribeEvery && _describedUtc != DateTime.MinValue) return;
        var environment = await Control.DescribeAsync(ct);
        lock (_sync)
        {
            _environment = environment;
            _describedUtc = Now();
        }
    }
}

/// <summary>The real hotspot: nova-hotspot.service, started and stopped with systemctl (a polkit rule allows it).</summary>
public sealed class SystemdHotspot : IHotspotControl
{
    public const string Unit = "nova-hotspot.service";

    public async Task<HotspotEnvironment> DescribeAsync(CancellationToken ct)
    {
        var device = Directory.Exists("/sys/class/net")
            ? Directory.GetDirectories("/sys/class/net").Select(Path.GetFileName).FirstOrDefault(d => Directory.Exists($"/sys/class/net/{d}/wireless"))
            : null;
        var method = await Active("NetworkManager", ct) ? "networkmanager"
            : await Active("iwd", ct) ? "iwd"
            : await Active("systemd-networkd", ct) ? "networkd"
            : "none";
        string? problem = null;
        if (device is null) problem = "No Wi-Fi adapter found on the mini PC";
        else if (method == "none") problem = "The mini PC's Wi-Fi isn't run by NetworkManager, iwd or systemd-networkd, so NOVA can't start a hotspot";
        else if (method == "networkd" && !new[] { "/usr/sbin/hostapd", "/usr/bin/hostapd", "/sbin/hostapd" }.Any(File.Exists))
            problem = "hostapd isn't installed: nova setup (or: sudo apt install hostapd)";
        else if (!File.Exists($"/etc/systemd/system/{Unit}"))
            problem = "The hotspot isn't installed yet: nova update --force";
        // Whether polkit allows it can't be asked beforehand: a check naming the unit (its "details") is only accepted
        // from root. Starting it says so, if not.
        return new HotspotEnvironment(method, device, problem);
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct) => await Active(Unit, ct);

    public async Task<int?> StationsAsync(string? device, CancellationToken ct)
    {
        if (device is null || await Run("iw", $"dev {device} station dump", ct) is not { Code: 0 } result) return null;
        return result.Output.Split('\n').Count(l => l.StartsWith("Station ", StringComparison.Ordinal));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var result = await Run("systemctl", $"start --no-ask-password {Unit}", ct);
        if (result is not { Code: 0 }) throw new InvalidOperationException(Refused(result?.Output));
        // Give it a moment: a hotspot that can't start (hostapd failing) stops again straight away.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        if (!await IsActiveAsync(ct)) throw new InvalidOperationException("The hotspot stopped as soon as it started (details: journalctl -u nova-hotspot)");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var result = await Run("systemctl", $"stop --no-ask-password {Unit}", ct);
        if (result is not { Code: 0 }) throw new InvalidOperationException(Refused(result?.Output));
    }

    private static string Refused(string? output) =>
        output is not null && (output.Contains("authentication required", StringComparison.OrdinalIgnoreCase) || output.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            ? "NOVA isn't allowed to start or stop the hotspot yet: nova update --force (it installs the permission)"
            : $"The mini PC refused: {output?.Trim()}";

    private static async Task<bool> Active(string unit, CancellationToken ct) => await Run("systemctl", $"is-active --quiet {unit}", ct) is { Code: 0 };

    private static async Task<(int Code, string Output)?> Run(string file, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await errors);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // stuck
        }
    }
}

/// <summary>The simulator's hotspot (and on a Windows PC): pretends, so the page can be tried out.</summary>
public sealed class SimulatedHotspot : IHotspotControl
{
    public bool Active { get; set; }
    public int Stations { get; set; }
    public string? FailWith { get; set; }

    public Task<HotspotEnvironment> DescribeAsync(CancellationToken ct) => Task.FromResult(new HotspotEnvironment("simulator", "wlan0", null));
    public Task<bool> IsActiveAsync(CancellationToken ct) => Task.FromResult(Active);
    public Task<int?> StationsAsync(string? device, CancellationToken ct) => Task.FromResult<int?>(Active ? Stations : null);

    public Task StartAsync(CancellationToken ct)
    {
        if (FailWith is { } message) throw new InvalidOperationException(message);
        Active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Active = false;
        Stations = 0;
        return Task.CompletedTask;
    }
}
