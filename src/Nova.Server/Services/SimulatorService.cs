using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <param name="Applicable">False on NOVA's built-in simulator backend, where everything is simulated.</param>
/// <param name="DriverControl">True when NOVA can stop/start drivers in indiserver; otherwise it only hides them.</param>
public sealed record SimulatorStatus(bool Applicable, bool Enabled, bool DriverControl);

/// <summary>
/// The "Simulators" switch. Off: INDI's simulator drivers are stopped in indiserver (so PHD2 and other
/// clients stop seeing them too) and hidden from NOVA's lists and automatic choices. On: the drivers NOVA
/// stopped are started again. Remembered across restarts; simulators that start with indiserver while the
/// switch is off are stopped as soon as they appear.
/// </summary>
public sealed class SimulatorService
{
    private sealed record Settings(bool Enabled = true, List<string>? StoppedDrivers = null);

    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private Settings _settings;
    private readonly HashSet<string> _stopping = new(StringComparer.Ordinal);

    public SimulatorService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "simulators.json");
        _settings = Load();
        equipment.DeviceFilter = device => Enabled || !IsSimulator(device.Name);
        equipment.DevicesChanged += (_, _) => StopAppearingSimulators();
    }

    public bool Enabled
    {
        get { lock (_sync) return _settings.Enabled; }
    }

    public SimulatorStatus Status => new(
        _equipment.Provider.Drivers is not null, Enabled, _equipment.Provider.Drivers?.Available == true);

    /// <summary>INDI's simulator drivers are indi_simulator_*; their devices are named "… Simulator".</summary>
    public bool IsSimulator(string device) =>
        _equipment.Provider.Drivers is { } drivers
        && (drivers.DriverOf(device)?.StartsWith("indi_simulator_", StringComparison.Ordinal) == true
            || device.EndsWith(" Simulator", StringComparison.Ordinal));

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        List<string> toStart;
        lock (_sync)
        {
            toStart = enabled ? _settings.StoppedDrivers ?? [] : [];
            _settings = new Settings(enabled, enabled ? null : _settings.StoppedDrivers);
        }
        Save();
        _log.Info(enabled ? "Simulators on" : "Simulators off");

        if (!enabled)
        {
            await StopSimulatorsAsync(ct);
        }
        else if (_equipment.Provider.Drivers is { Available: true } drivers)
        {
            foreach (var driver in toStart)
            {
                try
                {
                    await drivers.StartAsync(driver, ct);
                    _log.Info($"Started {driver}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"Couldn't start {driver}: {ex.Message}");
                }
            }
        }
        _equipment.NotifyDevicesChanged();
    }

    private void StopAppearingSimulators()
    {
        if (Enabled || _equipment.Provider.Drivers is not { Available: true }) return;
        _ = StopSimulatorsAsync(CancellationToken.None);
    }

    private async Task StopSimulatorsAsync(CancellationToken ct)
    {
        if (_equipment.Provider.Drivers is not { } drivers) return;
        if (!drivers.Available)
        {
            _log.Warn("Simulators hidden, but still running: re-run deploy/install.sh so NOVA can stop drivers");
            return;
        }

        // One driver can serve several devices; stop each driver once.
        var simulatorDrivers = _equipment.Provider.Devices
            .Where(d => IsSimulator(d.Name))
            .Select(d => drivers.DriverOf(d.Name))
            .OfType<string>()
            .Distinct()
            .ToList();

        foreach (var driver in simulatorDrivers)
        {
            lock (_sync)
            {
                if (!_stopping.Add(driver)) continue; // already on its way out
            }
            try
            {
                await drivers.StopAsync(driver, ct);
                lock (_sync)
                {
                    var stopped = _settings.StoppedDrivers ?? [];
                    if (!stopped.Contains(driver)) stopped.Add(driver);
                    _settings = _settings with { StoppedDrivers = stopped };
                }
                Save();
                _log.Info($"Stopped {driver}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"Couldn't stop {driver}: {ex.Message}");
            }
            finally
            {
                // Allow another attempt if the driver comes back (e.g. indiserver restarted).
                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => { lock (_sync) _stopping.Remove(driver); });
            }
        }
    }

    private Settings Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new Settings();
    }

    private void Save()
    {
        try
        {
            Settings snapshot;
            lock (_sync) snapshot = _settings;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }
}
