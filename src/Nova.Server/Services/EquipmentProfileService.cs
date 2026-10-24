using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>
/// A saved equipment setup: which device for each role (<see cref="EquipmentSelection.None"/> for an empty one), the
/// INDI drivers that run, the Simulators switch, how the mount connects, and the guider and its camera. Null for
/// anything that doesn't apply (e.g. drivers with NOVA's own simulator) or wasn't saved by an older NOVA.
/// </summary>
public sealed record EquipmentProfile(
    string Name, EquipmentSelection Devices, List<string>? Drivers = null, bool? Simulators = null,
    MountConnectionSetting? MountConnection = null, string? Guider = null, string? GuideCamera = null);

/// <summary>The mount over Bluetooth (to this mount) or over its USB cable.</summary>
public sealed record MountConnectionSetting(bool Bluetooth, string? Mount = null, string? Address = null);

public sealed record EquipmentProfilesStatus(string? Active, IReadOnlyList<EquipmentProfile> Profiles);

/// <summary>
/// Named equipment profiles, like the site and optics presets: save what's in use now, and load one later to put it
/// all back before Connect equipment. Saved per backend (equipment-profiles.indi.json), like the device choices.
/// </summary>
public sealed class EquipmentProfileService
{
    private sealed record Saved(string? Active, List<EquipmentProfile> Profiles);

    private readonly EquipmentService _equipment;
    private readonly DriverSetupService _drivers;
    private readonly SimulatorService _simulators;
    private readonly MountBluetoothService _bluetooth;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Saved _saved;

    private readonly GuiderService? _guider;
    private readonly NovaGuider? _novaGuider;

    public EquipmentProfileService(EquipmentService equipment, DriverSetupService drivers, SimulatorService simulators,
        MountBluetoothService bluetooth, IOptions<NovaOptions> options, ActivityLog log, GuiderService? guider = null,
        NovaGuider? novaGuider = null)
    {
        _guider = guider;
        _novaGuider = novaGuider;
        _equipment = equipment;
        _drivers = drivers;
        _simulators = simulators;
        _bluetooth = bluetooth;
        _log = log;
        var o = options.Value;
        _file = Path.Combine(o.ResolveConfigDirectory(), $"equipment-profiles.{o.Backend.ToLowerInvariant()}.json");
        _saved = Load();
    }

    public EquipmentProfilesStatus Status
    {
        get
        {
            lock (_sync) return new EquipmentProfilesStatus(_saved.Active, _saved.Profiles.ToList());
        }
    }

    /// <summary>What's in use now, as a profile.</summary>
    public EquipmentProfile Current(string name)
    {
        var drivers = _drivers.Applicable ? _drivers.SelectedDrivers(out _).ToList() : null;
        var simulators = _simulators.Status.Applicable ? _simulators.Enabled : (bool?)null;
        var bt = _bluetooth.Status;
        var mount = bt.Applicable || bt.Enabled ? new MountConnectionSetting(bt.Enabled, bt.Mount, bt.Address) : null;
        return new EquipmentProfile(name, _equipment.InUse, drivers, simulators, mount,
            _guider?.Backend, _novaGuider?.Camera?.Name ?? _novaGuider?.Settings.Camera);
    }

    /// <summary>
    /// Saves what's in use now under a name (replacing a profile of that name, or renaming <paramref name="originalName"/>),
    /// and makes it the active one.
    /// </summary>
    public EquipmentProfile Save(string name, string? originalName)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the profile a name");
        if (name.Length > 60) throw new ArgumentException("Profile names can be at most 60 characters");
        var profile = Current(name);
        lock (_sync)
        {
            var replacing = originalName ?? name;
            var profiles = _saved.Profiles.ToList();
            var index = profiles.FindIndex(p => p.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase));
            if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                  && !p.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There's already a profile called '{name}'");
            if (index >= 0) profiles[index] = profile;
            else profiles.Add(profile);
            _saved = new Saved(name, profiles);
        }
        Write();
        _log.Info($"Equipment profile '{name}' saved: {Describe(profile.Devices)}");
        return profile;
    }

    /// <summary>
    /// Puts a profile back: the drivers it needs (started now), the Simulators switch, the mount's connection and the
    /// device for each role. Then Connect equipment connects them. Only while equipment is disconnected.
    /// </summary>
    public async Task LoadAsync(string name, CancellationToken ct)
    {
        if (_equipment.EquipmentConnected) throw new InvalidOperationException("Disconnect equipment first, then load the profile");
        EquipmentProfile profile;
        lock (_sync)
            profile = _saved.Profiles.FirstOrDefault(p => p.Name == name) ?? throw new ArgumentException($"There's no profile '{name}'");

        await _loading.WaitAsync(ct);
        try
        {
            // The Simulators switch first, as it was saved (while it's off, NOVA stops any simulator driver that starts,
            // and the driver list keeps them either way, so it says nothing about the switch).
            if (profile.Simulators is { } simulators && _simulators.Status.Applicable && simulators != _simulators.Enabled)
                await _simulators.SetEnabledAsync(simulators, ct);
            if (profile.Drivers is { } drivers && _drivers.Applicable)
            {
                var change = await _drivers.ApplyAsync(drivers, ct);
                if (change.Missing.Count > 0) _log.Warn($"Profile '{name}': not installed: {string.Join(", ", change.Missing)}");
            }
            if (profile.MountConnection is { } mount && (!mount.Bluetooth || mount.Mount is not null || mount.Address is not null))
                await _bluetooth.SetAsync(mount.Bluetooth, mount.Mount, mount.Address, ct);
            _equipment.RestoreSelection(profile.Devices);
            if (profile.Guider is { } guider && _guider is not null && guider != _guider.Backend)
                await _guider.SetBackendAsync(guider, ct);
            if (profile.GuideCamera is { } guideCamera && _novaGuider is not null && _novaGuider.Settings.Camera != guideCamera)
                _novaGuider.UpdateSettings(_novaGuider.Settings with { Camera = guideCamera });
            lock (_sync) _saved = _saved with { Active = name };
            Write();
            _log.Info($"Equipment profile '{name}' loaded: {Describe(profile.Devices)}. Connect equipment to connect them.");
        }
        finally
        {
            _loading.Release();
        }
    }

    public void Delete(string name)
    {
        lock (_sync)
        {
            var profiles = _saved.Profiles.Where(p => p.Name != name).ToList();
            if (profiles.Count == _saved.Profiles.Count) throw new ArgumentException($"There's no profile '{name}'");
            _saved = new Saved(_saved.Active == name ? null : _saved.Active, profiles);
        }
        Write();
        _log.Info($"Equipment profile '{name}' deleted");
    }

    private static string Describe(EquipmentSelection d)
    {
        var parts = new[] { d.Camera, d.Mount, d.Focuser, d.FilterWheel, d.Rotator, d.FlatPanel }
            .Where(n => n is not null and not EquipmentSelection.None).ToList();
        return parts.Count == 0 ? "automatic" : string.Join(", ", parts);
    }

    private Saved Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) ?? new(null, []);
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new(null, []);
    }

    private static readonly JsonSerializerOptions FileFormat = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Write()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_saved, FileFormat);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save equipment profiles to {_file}", ex);
        }
    }
}
