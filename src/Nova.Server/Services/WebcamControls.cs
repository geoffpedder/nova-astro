using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nova.Server.Services;

/// <summary>
/// One of a webcam's controls, as V4L2 describes it: an integer (slider), a bool (switch) or a menu (choice). Inactive
/// ones are set by something else for now (exposure time while auto exposure is on, say).
/// </summary>
/// <param name="Name">V4L2's name (brightness, exposure_time_absolute…), what v4l2-ctl sets.</param>
/// <param name="Label">A readable name.</param>
/// <param name="Group">Its group ("User Controls", "Camera Controls").</param>
/// <param name="Type">"int", "bool" or "menu" (others aren't offered).</param>
public sealed record WebcamControl(
    string Name, string Label, string Group, string Type, int Min, int Max, int Step, int Default, int Value, bool Inactive,
    IReadOnlyList<WebcamMenuItem> Menu);

public sealed record WebcamMenuItem(int Value, string Label);

/// <summary>A webcam's controls, or why they can't be read.</summary>
public sealed record WebcamControls(string? Device, IReadOnlyList<WebcamControl> Controls, string? Error);

/// <summary>Reads and sets a webcam's controls.</summary>
public interface IWebcamControls
{
    Task<IReadOnlyList<WebcamControl>> ListAsync(string device, CancellationToken ct);
    Task SetAsync(string device, string name, int value, CancellationToken ct);
}

/// <summary>A webcam's controls through v4l2-ctl (v4l-utils).</summary>
public sealed partial class V4l2Controls : IWebcamControls
{
    public async Task<IReadOnlyList<WebcamControl>> ListAsync(string device, CancellationToken ct) =>
        Parse(await RunAsync(["-d", device, "--list-ctrls-menus"], ct));

    public async Task SetAsync(string device, string name, int value, CancellationToken ct) =>
        await RunAsync(["-d", device, $"--set-ctrl={name}={value.ToString(CultureInfo.InvariantCulture)}"], ct);

    private static async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo("v4l2-ctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("v4l2-ctl didn't start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("v4l-utils isn't installed, so the webcam's controls can't be shown: nova setup (or: sudo apt install v4l-utils, sudo pacman -S v4l-utils)");
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new InvalidOperationException("v4l2-ctl didn't answer");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } e ? e.Split('\n')[^1].Trim() : $"v4l2-ctl failed ({process.ExitCode})");
            return await output;
        }
    }

    [GeneratedRegex(@"^\s*(\w+)\s+0x[0-9a-f]+\s+\((\w+)\)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ControlLine();

    [GeneratedRegex(@"^\s+(-?\d+):\s+(.*)$")]
    private static partial Regex MenuLine();

    [GeneratedRegex(@"(\w+)=(-?\w+)")]
    private static partial Regex Pair();

    /// <summary>
    /// v4l2-ctl --list-ctrls-menus: group headings ("User Controls"), a line per control ("brightness 0x00980900 (int)
    /// : min=-64 max=64 step=1 default=0 value=0", "flags=inactive"), menu items below a menu ("    1: 50 Hz").
    /// </summary>
    public static IReadOnlyList<WebcamControl> Parse(string text)
    {
        var controls = new List<WebcamControl>();
        var group = "Controls";
        List<WebcamMenuItem>? menu = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (ControlLine().Match(line) is { Success: true } m)
            {
                var type = m.Groups[2].Value.ToLowerInvariant() switch
                {
                    "int" or "integer" => "int",
                    "bool" or "boolean" => "bool",
                    "menu" or "intmenu" => "menu",
                    _ => null,
                };
                menu = null;
                if (type is null) continue; // buttons, strings, 64-bit: not offered
                var pairs = Pair().Matches(m.Groups[3].Value).ToDictionary(p => p.Groups[1].Value, p => p.Groups[2].Value);
                int Num(string key, int fallback) => pairs.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : fallback;
                var items = new List<WebcamMenuItem>();
                if (type == "menu") menu = items;
                var name = m.Groups[1].Value;
                controls.Add(new WebcamControl(name, Label(name), group, type,
                    Num("min", type == "bool" ? 0 : 0), Num("max", type == "bool" ? 1 : 0), Num("step", 1), Num("default", 0), Num("value", 0),
                    m.Groups[3].Value.Contains("inactive", StringComparison.OrdinalIgnoreCase), items));
            }
            else if (menu is not null && MenuLine().Match(line) is { Success: true } item)
            {
                menu.Add(new WebcamMenuItem(int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture), item.Groups[2].Value.Trim()));
            }
            else if (!line.StartsWith(' ') && !line.StartsWith('\t'))
            {
                group = line.Trim();
                menu = null;
            }
        }
        return controls;
    }

    /// <summary>"exposure_time_absolute" → "Exposure time absolute"; a few common ones made friendlier.</summary>
    public static string Label(string name) => name switch
    {
        "auto_exposure" or "exposure_auto" => "Auto exposure",
        "exposure_time_absolute" or "exposure_absolute" => "Exposure time",
        "white_balance_automatic" or "white_balance_temperature_auto" => "Auto white balance",
        "white_balance_temperature" => "White balance (K)",
        "power_line_frequency" => "Power line frequency",
        "backlight_compensation" => "Backlight compensation",
        "focus_automatic_continuous" or "focus_auto" => "Auto focus",
        "focus_absolute" => "Focus",
        "exposure_dynamic_framerate" or "exposure_auto_priority" => "Lower frame rate in the dark",
        _ => char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' '),
    };
}

/// <summary>The simulator's webcam controls: a few in memory, the test card follows brightness.</summary>
public sealed class SimulatedWebcamControls : IWebcamControls
{
    private readonly Dictionary<string, WebcamControl> _controls = new()
    {
        ["brightness"] = new("brightness", "Brightness", "User Controls", "int", -64, 64, 1, 0, 0, false, []),
        ["contrast"] = new("contrast", "Contrast", "User Controls", "int", 0, 95, 1, 32, 32, false, []),
        ["power_line_frequency"] = new("power_line_frequency", "Power line frequency", "User Controls", "menu", 0, 2, 1, 1, 1, false,
            [new(0, "Disabled"), new(1, "50 Hz"), new(2, "60 Hz")]),
        ["auto_exposure"] = new("auto_exposure", "Auto exposure", "Camera Controls", "menu", 1, 3, 1, 3, 3, false,
            [new(1, "Manual Mode"), new(3, "Aperture Priority Mode")]),
        ["exposure_time_absolute"] = new("exposure_time_absolute", "Exposure time", "Camera Controls", "int", 1, 5000, 1, 157, 157, true, []),
    };

    /// <summary>The brightness setting now (−64…64), for the test card.</summary>
    public int Brightness
    {
        get
        {
            lock (_controls) return _controls["brightness"].Value;
        }
    }

    public Task<IReadOnlyList<WebcamControl>> ListAsync(string device, CancellationToken ct)
    {
        lock (_controls) return Task.FromResult<IReadOnlyList<WebcamControl>>(_controls.Values.ToList());
    }

    public Task SetAsync(string device, string name, int value, CancellationToken ct)
    {
        lock (_controls)
        {
            if (!_controls.TryGetValue(name, out var c)) throw new InvalidOperationException($"No control {name}");
            _controls[name] = c with { Value = value };
            // As a real webcam does: exposure time only counts in manual exposure.
            if (name == "auto_exposure") _controls["exposure_time_absolute"] = _controls["exposure_time_absolute"] with { Inactive = value != 1 };
        }
        return Task.CompletedTask;
    }
}
