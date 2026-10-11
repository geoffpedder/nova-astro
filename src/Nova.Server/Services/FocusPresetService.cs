using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>A remembered focuser position, e.g. "Askar 71F" or "Askar 71F + reducer".</summary>
public sealed record FocusPreset(string Name, int Position);

/// <summary>
/// Named focuser positions per focuser, so swapping a reducer or flattener in starts close to focus
/// (autofocus then finishes the job). Kept in ~/.config/nova/focus-presets.json.
/// </summary>
public sealed class FocusPresetService
{
    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<FocusPreset>> _focusers;

    public FocusPresetService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "focus-presets.json");
        _focusers = Load();
    }

    public IReadOnlyList<FocusPreset> For(string focuser)
    {
        lock (_sync) return _focusers.TryGetValue(focuser, out var presets) ? presets.ToList() : [];
    }

    /// <summary>Adds or updates a preset (renaming it if <paramref name="originalName"/> differs).</summary>
    public void Save(string focuser, FocusPreset preset, string? originalName)
    {
        var name = preset.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the preset a name");
        if (name.Length > 60) throw new ArgumentException("Preset names can be at most 60 characters");
        if (_equipment.Provider.GetFocuser(focuser) is { IsConnected: true } f
            && f.MaxPosition > f.MinPosition && (preset.Position < f.MinPosition || preset.Position > f.MaxPosition))
            throw new ArgumentException($"Position must be between {f.MinPosition} and {f.MaxPosition}");
        if (preset.Position < 0) throw new ArgumentException("Position can't be negative");
        preset = preset with { Name = name };

        lock (_sync)
        {
            var presets = _focusers.TryGetValue(focuser, out var existing) ? existing : _focusers[focuser] = [];
            var replacing = originalName ?? name;
            if (presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 && !p.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There's already a preset called '{name}'");
            var index = presets.FindIndex(p => p.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) presets[index] = preset;
            else presets.Add(preset);
            presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }
        Save();
        _log.Info($"{focuser}: focus preset '{name}' = {preset.Position}");
    }

    public void Delete(string focuser, string name)
    {
        lock (_sync)
        {
            if (!_focusers.TryGetValue(focuser, out var presets) || presets.RemoveAll(p => p.Name == name) == 0)
                throw new ArgumentException($"{focuser} has no focus preset '{name}'");
        }
        Save();
        _log.Info($"{focuser}: deleted focus preset '{name}'");
    }

    private Dictionary<string, List<FocusPreset>> Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<Dictionary<string, List<FocusPreset>>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_focusers, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }
}
