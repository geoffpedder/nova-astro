using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>A named optical configuration, e.g. "RedCat 51" or "RedCat 51 + 0.8× reducer".</summary>
public sealed record OpticsPreset(string Name, double FocalLength, double Aperture)
{
    public Optics ToOptics() => new(FocalLength, Aperture);
}

/// <summary>
/// A camera's chosen preset (one of the shared ones), its sensor as last seen (for framing while it's away), and the
/// gain and offset to set whenever it connects (null: leave the driver's own).
/// </summary>
public sealed record CameraOptics(
    string? Active, int? SensorWidth = null, int? SensorHeight = null, double? PixelSize = null,
    double? Gain = null, double? Offset = null);

/// <summary>optics.json: the presets, shared by every camera, and each camera's settings.</summary>
public sealed record OpticsFile(List<OpticsPreset> Presets, Dictionary<string, CameraOptics> Cameras);

/// <param name="ImageScale">Arcseconds per unbinned pixel with this camera, when both focal length and pixel size are known.</param>
/// <param name="UsedBy">The other cameras using this preset.</param>
public sealed record OpticsPresetInfo(string Name, double FocalLength, double Aperture, double FocalRatio, double? ImageScale,
    IReadOnlyList<string>? UsedBy = null);

/// <param name="ActivePreset">The preset sent to the driver; its values are repeated at the top level for convenience.</param>
public sealed record CameraOpticsInfo(
    string Camera, bool Connected, string? ActivePreset, double? FocalLength, double? Aperture,
    double? PixelSize, double? FocalRatio, double? ImageScale, IReadOnlyList<OpticsPresetInfo> Presets,
    int? SensorWidth = null, int? SensorHeight = null, double? DefaultGain = null, double? DefaultOffset = null,
    bool BuiltInDefaults = false);

/// <summary>
/// Remembers named optics presets, shared by every camera (two cameras on one telescope, or a camera swapped for
/// another, use the same one), and gives each camera's chosen preset to its driver every time it connects — whoever
/// connects it, NOVA or PHD2 — so FITS headers are complete and INDI stops warning "Telescope focal length is missing".
/// </summary>
public sealed class OpticsService
{
    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly List<OpticsPreset> _presets;
    private readonly Dictionary<string, CameraOptics> _cameras;
    // Cameras whose current connection has already received their optics.
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);

    public OpticsService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "optics.json");
        (_presets, _cameras) = Load();
        equipment.DevicesChanged += (_, _) => ApplyToNewConnections();
    }

    /// <summary>Every camera that's present or has saved presets.</summary>
    public IReadOnlyList<CameraOpticsInfo> All
    {
        get
        {
            var cameras = _equipment.VisibleDevices.Where(d => d.Kinds.Contains(DeviceKind.Camera)).ToList();
            RememberSensors(cameras.Where(c => c.IsConnected).Select(c => c.Name));
            Dictionary<string, CameraOptics> saved;
            List<OpticsPreset> library;
            lock (_sync) (saved, library) = (new(_cameras), [.. _presets]);

            var hidden = _equipment.Provider.Devices.Select(d => d.Name).Except(cameras.Select(c => c.Name)).ToHashSet();
            return cameras.Select(c => c.Name).Union(saved.Keys.Where(k => !hidden.Contains(k))).Order().Select(name =>
            {
                saved.TryGetValue(name, out var presets);
                var builtIn = presets?.Gain is null && presets?.Offset is null ? BuiltInDefaults(name) : null;
                var pixel = _equipment.Provider.GetCamera(name)?.PixelSize ?? presets?.PixelSize;
                var active = library.FirstOrDefault(p => p.Name == presets?.Active);
                return new CameraOpticsInfo(
                    name,
                    cameras.Any(c => c.Name == name && c.IsConnected),
                    active?.Name,
                    active?.FocalLength,
                    active?.Aperture,
                    pixel,
                    active is null ? null : Math.Round(active.ToOptics().FocalRatio, 2),
                    active is not null && pixel is { } p ? Math.Round(active.ToOptics().ImageScale(p), 3) : null,
                    library.Select(x => new OpticsPresetInfo(x.Name, x.FocalLength, x.Aperture,
                        Math.Round(x.ToOptics().FocalRatio, 2),
                        pixel is { } px ? Math.Round(x.ToOptics().ImageScale(px), 3) : null,
                        saved.Where(c => c.Key != name && c.Value.Active == x.Name && !hidden.Contains(c.Key))
                            .Select(c => c.Key).Order().ToList())).ToList(),
                    presets?.SensorWidth,
                    presets?.SensorHeight,
                    presets?.Gain ?? builtIn?.Gain,
                    presets?.Offset ?? builtIn?.Offset,
                    builtIn is not null);
            }).ToList();
        }
    }

    /// <summary>Notes connected cameras' sensor size and pixel size, so framing works with the camera away.</summary>
    private void RememberSensors(IEnumerable<string> connected)
    {
        var changed = false;
        foreach (var name in connected)
        {
            if (_equipment.Provider.GetCamera(name) is not { SensorSize: { } size } camera) continue;
            lock (_sync)
            {
                var presets = _cameras.TryGetValue(name, out var existing) ? existing : new CameraOptics(null);
                if (presets.SensorWidth == size.Width && presets.SensorHeight == size.Height && presets.PixelSize == camera.PixelSize) continue;
                _cameras[name] = presets with { SensorWidth = size.Width, SensorHeight = size.Height, PixelSize = camera.PixelSize };
                changed = true;
            }
        }
        if (changed) Save();
    }

    /// <summary>
    /// Adds or updates a preset (renaming it if <paramref name="originalName"/> differs) and makes it this camera's.
    /// Changing one changes it for every camera using it.
    /// </summary>
    public async Task SavePresetAsync(string camera, OpticsPreset preset, string? originalName, CancellationToken ct)
    {
        var name = preset.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the preset a name");
        if (name.Length > 60) throw new ArgumentException("Preset names can be at most 60 characters");
        if (preset.FocalLength is <= 0 or > 20_000) throw new ArgumentException("Focal length must be between 1 and 20000 mm");
        if (preset.Aperture is <= 0 or > 3_000) throw new ArgumentException("Aperture must be between 1 and 3000 mm");
        preset = preset with { Name = name };

        List<string> users;
        lock (_sync)
        {
            // Without an original name it's a new one, so it can't quietly change another camera's.
            var index = originalName is null ? -1 : _presets.FindIndex(p => p.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
            if (_presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                               && (index < 0 || !p.Name.Equals(_presets[index].Name, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException($"There's already a preset called '{name}'");

            var oldName = index >= 0 ? _presets[index].Name : null;
            if (index >= 0) _presets[index] = preset;
            else _presets.Add(preset);
            // Every camera using it follows a rename, and this one takes it.
            foreach (var (other, settings) in _cameras.ToList())
                if (oldName is not null && settings.Active == oldName) _cameras[other] = settings with { Active = name };
            _cameras[camera] = (_cameras.TryGetValue(camera, out var existing) ? existing : new CameraOptics(null)) with { Active = name };
            users = _cameras.Where(c => c.Value.Active == name).Select(c => c.Key).ToList();
            foreach (var user in users) _applied.Remove(user);
        }
        Save();
        _log.Info($"{camera}: optics '{name}' ({preset.FocalLength:0.#} mm, f/{preset.ToOptics().FocalRatio:0.0#})");
        foreach (var user in users) await ApplyIfConnectedAsync(user, preset.ToOptics(), ct);
    }

    public async Task SelectPresetAsync(string camera, string name, CancellationToken ct)
    {
        OpticsPreset preset;
        lock (_sync)
        {
            if (_presets.FirstOrDefault(p => p.Name == name) is not { } found)
                throw new ArgumentException($"There's no optics preset '{name}'");
            preset = found;
            _cameras[camera] = (_cameras.TryGetValue(camera, out var existing) ? existing : new CameraOptics(null)) with { Active = name };
            _applied.Remove(camera);
        }
        Save();
        _log.Info($"{camera}: using optics '{name}'");
        await ApplyIfConnectedAsync(camera, preset.ToOptics(), ct);
    }

    /// <summary>
    /// Deletes a preset, for every camera. If it was this camera's, the first remaining one takes its place; other
    /// cameras using it are left with none.
    /// </summary>
    public async Task DeletePresetAsync(string camera, string name, CancellationToken ct)
    {
        OpticsPreset? nowActive = null;
        lock (_sync)
        {
            if (_presets.RemoveAll(p => p.Name == name) == 0) throw new ArgumentException($"There's no optics preset '{name}'");
            foreach (var (other, settings) in _cameras.ToList())
                if (settings.Active == name)
                    _cameras[other] = settings with { Active = other == camera ? _presets.FirstOrDefault()?.Name : null };
            if (_cameras.TryGetValue(camera, out var mine)) nowActive = _presets.FirstOrDefault(p => p.Name == mine.Active);
            _applied.Remove(camera);
        }
        Save();
        _log.Info($"{camera}: deleted optics '{name}'");
        if (nowActive is not null) await ApplyIfConnectedAsync(camera, nowActive.ToOptics(), ct);
    }

    /// <summary>
    /// The gain and offset to set every time this camera connects (drivers otherwise start with their own, e.g. the
    /// INDI ASI driver's 200). Null clears both; they aren't applied now, only at the next connection.
    /// </summary>
    public void SetConnectDefaults(string camera, double? gain, double? offset)
    {
        if (gain is < 0 or > 10_000 || offset is < 0 or > 10_000) throw new ArgumentException("Gain and offset must be between 0 and 10000");
        lock (_sync)
        {
            var presets = _cameras.TryGetValue(camera, out var existing) ? existing : new CameraOptics(null);
            _cameras[camera] = presets with { Gain = gain, Offset = offset };
        }
        Save();
        _log.Info(gain is null && offset is null
            ? $"{camera}: no default gain and offset (the driver's own are used)"
            : $"{camera}: connects with gain {gain:0.#} and offset {offset:0.#}");
    }

    /// <summary>
    /// NOVA's own gain and offset for a ZWO imaging camera that has none chosen: 100 and 50 rather than the driver's
    /// 200. At 100 the newer ZWO sensors (ASI2600, ASI533, ASI2400…) switch to their low-noise mode, at about unity
    /// gain. Only the imaging camera: a ZWO guide camera is left to PHD2.
    /// </summary>
    private (double Gain, double Offset)? BuiltInDefaults(string camera) =>
        camera.StartsWith("ZWO CCD", StringComparison.OrdinalIgnoreCase) && camera == _equipment.Camera?.Name ? (100, 50) : null;

    private Task ApplyIfConnectedAsync(string camera, Optics optics, CancellationToken ct) =>
        _equipment.Provider.Devices.Any(d => d.Name == camera && d.IsConnected)
            ? ApplyAsync(camera, optics, null, ct)
            : Task.CompletedTask;

    private void ApplyToNewConnections()
    {
        var connected = _equipment.Provider.Devices
            .Where(d => d.IsConnected && d.Kinds.Contains(DeviceKind.Camera))
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);

        List<(string Camera, Optics? Optics, CameraSettingsUpdate? Settings)> due = [];
        lock (_sync)
        {
            _applied.RemoveWhere(name => !connected.Contains(name)); // disconnected: re-apply next time
            foreach (var name in connected)
            {
                _cameras.TryGetValue(name, out var presets);
                var settings = presets?.Gain is not null || presets?.Offset is not null
                    ? new CameraSettingsUpdate(Gain: presets.Gain, Offset: presets.Offset)
                    : BuiltInDefaults(name) is { } builtIn ? new CameraSettingsUpdate(Gain: builtIn.Gain, Offset: builtIn.Offset) : null;
                var active = _presets.FirstOrDefault(p => p.Name == presets?.Active);
                if ((active is not null || settings is not null) && _applied.Add(name))
                    due.Add((name, active?.ToOptics(), settings));
            }
        }

        foreach (var (camera, optics, settings) in due)
            _ = ApplyAsync(camera, optics, settings, CancellationToken.None);
    }

    private async Task ApplyAsync(string camera, Optics? optics, CameraSettingsUpdate? settings, CancellationToken ct)
    {
        if (_equipment.Provider.GetCamera(camera) is not { } device) return;
        try
        {
            if (optics is not null) await device.SetOpticsAsync(optics, ct);
            lock (_sync) _applied.Add(camera);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _applied.Remove(camera);
            _log.Warn($"Couldn't give {camera} its optics: {ex.Message}");
        }
        if (settings is null) return;
        try
        {
            // Drivers define gain and offset just after connecting (ZWO's in CCD_CONTROLS): wait for them.
            for (var i = 0; i < 50 && device.IsConnected
                 && ((settings.Gain is not null && device.Controls.Gain is null) || (settings.Offset is not null && device.Controls.Offset is null)); i++)
                await Task.Delay(200, ct);
            var controls = device.Controls;
            settings = settings with { Gain = controls.Gain is null ? null : settings.Gain, Offset = controls.Offset is null ? null : settings.Offset };
            if (settings.Gain is null && settings.Offset is null) return;
            await device.ApplyAsync(settings, ct);
            _log.Info($"{camera}: gain {settings.Gain:0.#}, offset {settings.Offset:0.#} (its defaults)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Couldn't set {camera}'s default gain and offset: {ex.Message}");
        }
    }

    private (List<OpticsPreset>, Dictionary<string, CameraOptics>) Load()
    {
        try
        {
            if (!File.Exists(_file)) return ([], new());
            var root = JsonNode.Parse(File.ReadAllText(_file))?.AsObject();
            if (root?["Cameras"] is JsonObject && root["Presets"] is JsonArray)
            {
                var file = root.Deserialize<OpticsFile>();
                return ([.. file?.Presets ?? []], new(file?.Cameras ?? []));
            }
            return Migrate(root);
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
            return ([], new());
        }
    }

    /// <summary>
    /// From when each camera had its own presets: pooled into one list. A name two cameras used for different
    /// optics gets the camera's name added, e.g. "Default (ZWO CCD ASI120MM Mini)".
    /// </summary>
    private static (List<OpticsPreset>, Dictionary<string, CameraOptics>) Migrate(JsonObject? root)
    {
        var owned = new List<(string Camera, OpticsPreset Preset, bool Active)>();
        var cameras = new Dictionary<string, CameraOptics>();
        foreach (var (camera, node) in root ?? [])
        {
            if (node is not JsonObject entry) continue;
            if (entry["Presets"] is { } list)
            {
                var settings = entry.Deserialize<CameraOptics>() ?? new(null);
                cameras[camera] = settings;
                owned.AddRange((list.Deserialize<List<OpticsPreset>>() ?? []).Select(p => (camera, p, p.Name == settings.Active)));
            }
            else if (entry.Deserialize<Optics>() is { FocalLength: > 0 } single)
            {
                // Before presets existed, one camera had one set of optics: keep it as "Default".
                cameras[camera] = new CameraOptics("Default");
                owned.Add((camera, new OpticsPreset("Default", single.FocalLength, single.Aperture), true));
            }
        }

        var shared = new List<OpticsPreset>();
        foreach (var group in owned.GroupBy(o => o.Preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            var differ = group.Select(o => (o.Preset.FocalLength, o.Preset.Aperture)).Distinct().Count() > 1;
            foreach (var (camera, preset, active) in group)
            {
                var name = differ ? $"{preset.Name} ({camera})" : preset.Name;
                if (!shared.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) shared.Add(preset with { Name = name });
                if (active) cameras[camera] = cameras[camera] with { Active = name };
            }
        }
        return (shared, cameras);
    }

    // Readable when opened by hand: indented, and "+" or "×" in preset names left as they are.
    private static readonly JsonSerializerOptions FileFormat = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Save()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(new OpticsFile(_presets, _cameras), FileFormat);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save optics to {_file}", ex);
        }
    }
}
