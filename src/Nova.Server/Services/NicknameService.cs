using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>
/// Friendly names for devices ("Guide cam" for "ZWO CCD ASI120MM Mini"), shown in the page instead of the driver's
/// name. Display only: settings, logs and the drivers keep using the real names. Kept in nicknames.json.
/// </summary>
public sealed class NicknameService
{
    public const int MaxLength = 40;
    private readonly string _file;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    private Dictionary<string, string> _names;

    public NicknameService(IOptions<NovaOptions> options, ActivityLog log)
    {
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "nicknames.json");
        _log = log;
        _names = Load();
    }

    public IReadOnlyDictionary<string, string> All
    {
        get
        {
            lock (_sync) return new Dictionary<string, string>(_names);
        }
    }

    /// <summary>Sets a device's nickname; empty (or the device's own name) removes it.</summary>
    public void Set(string device, string? nickname)
    {
        if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("Which device?");
        var name = nickname?.Trim() ?? "";
        if (name.Length > MaxLength) throw new ArgumentException($"Nicknames can be at most {MaxLength} characters");
        lock (_sync)
        {
            if (name.Length == 0 || name == device) _names.Remove(device);
            else _names[device] = name;
        }
        Save();
        _log.Info(name.Length == 0 || name == device ? $"{device}: nickname removed" : $"{device} is now called '{name}'");
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [];
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return [];
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_names, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save nicknames to {_file}", ex);
        }
    }
}
