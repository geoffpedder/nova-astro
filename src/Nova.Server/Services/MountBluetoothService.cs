using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>A mount found by a Bluetooth search. Rssi: signal strength in dBm (-40 strong, -90 weak).</summary>
public sealed record BluetoothMount(string Name, string Address, int? Rssi);

/// <param name="Applicable">An INDI backend with the ZWO mount driver: the only mounts this works for.</param>
/// <param name="Enabled">The mount is connected over Bluetooth rather than USB.</param>
/// <param name="Mount">The chosen mount's Bluetooth name (null: the first ZWO mount found).</param>
/// <param name="State">Off, Idle (waiting for Connect equipment), Searching, Connecting, Connected or Error.</param>
public sealed record MountBluetoothStatus(
    bool Applicable, bool Enabled, string? Mount, string? Address, string State, string? Detail,
    bool Scanning, IReadOnlyList<BluetoothMount> Found);

/// <summary>
/// Connects ZWO AM mounts over Bluetooth. NOVA runs deploy/nova-mount-ble.py, which keeps the mount's Bluetooth link
/// up and offers it as a serial port at <see cref="NovaOptions.MountBluetoothLink"/>; the INDI driver is pointed at
/// that port just before the mount connects. The helper runs while equipment is connected and stops on Disconnect,
/// which frees the mount for ZWO's phone app. Settings in mount-bluetooth.json.
/// </summary>
public sealed class MountBluetoothService : IAsyncDisposable
{
    public const string ZwoMountDriver = "indi_lx200am5";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(6);

    private sealed record Settings(bool Enabled = false, string? Mount = null, string? Address = null);

    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly string _script;
    private readonly string _python;
    private readonly object _sync = new();
    private Settings _settings;
    private Process? _bridge;
    private string _state = "Idle";
    private string? _detail;
    private TaskCompletionSource<bool>? _linkUp;
    private readonly List<BluetoothMount> _found = [];
    private bool _scanning;

    public MountBluetoothService(EquipmentService equipment, IOptions<NovaOptions> options, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        var o = options.Value;
        _file = Path.Combine(o.ResolveConfigDirectory(), "mount-bluetooth.json");
        _script = Path.Combine(AppContext.BaseDirectory, "nova-mount-ble.py");
        _python = string.IsNullOrWhiteSpace(o.Python) ? (OperatingSystem.IsWindows() ? "python" : "python3") : o.Python;
        LinkPath = o.MountBluetoothLink;
        _settings = Load();
        equipment.BeforeConnect = PrepareAsync;
        // Disconnect equipment: let the mount go, so the phone app can have it.
        equipment.DevicesChanged += (_, _) =>
        {
            if (!equipment.EquipmentConnected) StopBridge();
        };
    }

    /// <summary>The serial port the INDI driver opens: a symlink the helper points at its pseudo-terminal.</summary>
    public string LinkPath { get; }

    public MountBluetoothStatus Status
    {
        get
        {
            lock (_sync)
            {
                var state = !_settings.Enabled ? "Off" : _bridge is null ? (_state == "Error" ? "Error" : "Idle") : _state;
                return new MountBluetoothStatus(Applicable, _settings.Enabled, _settings.Mount, _settings.Address, state,
                    _settings.Enabled ? _detail : null, _scanning, _found.ToList());
            }
        }
    }

    /// <summary>INDI with the ZWO mount driver in use (or no mount yet, so it can be chosen before connecting).</summary>
    private bool Applicable
    {
        get
        {
            if (_equipment.Provider.Drivers is not { } drivers) return false;
            var mount = _equipment.Mount;
            return mount is null || drivers.DriverOf(mount.Name) is null or ZwoMountDriver;
        }
    }

    /// <summary>Turns Bluetooth on (for one mount, or the first found when both are empty) or off (USB again).</summary>
    public async Task SetAsync(bool enabled, string? mount, string? address, CancellationToken ct)
    {
        if (mount is { Length: > 64 } || address is { Length: > 64 }) throw new ArgumentException("That's not a Bluetooth mount name");
        Settings before;
        lock (_sync)
        {
            before = _settings;
            _settings = enabled ? new Settings(true, Blank(mount), Blank(address)) : before with { Enabled = false };
            _detail = null;
            if (_state == "Error") _state = "Idle";
        }
        Save();
        _log.Info(enabled ? $"Mount connection: Bluetooth, {_settings.Mount ?? "the first ZWO mount found"}" : "Mount connection: USB");
        if (before == _settings) return;
        // Off, or another mount: let go of the old one.
        if (!enabled || before.Mount != _settings.Mount || before.Address != _settings.Address) StopBridge();
        // While equipment is connected, reconnect the mount the new way (USB: the driver finds its port again).
        if (_equipment.EquipmentConnected && _equipment.Mount is { } device)
        {
            if (device.IsConnected) await device.DisconnectAsync(ct);
            await PrepareAsync(device, ct);
            await device.ConnectAsync(ct);
            _equipment.NotifyDevicesChanged();
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Looks for ZWO mounts in range for a few seconds. A mount that's connected (to NOVA or a phone) doesn't show.</summary>
    public async Task<IReadOnlyList<BluetoothMount>> ScanAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_scanning) throw new InvalidOperationException("Already searching");
            _scanning = true;
            _found.Clear();
        }
        try
        {
            using var process = StartHelper(["scan", "--timeout", ScanTime.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)], stdin: false);
            string? error = null;
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var e = ParseEvent(line);
                if (e?.Event == "found" && e.Address is { } address)
                {
                    lock (_sync)
                    {
                        _found.RemoveAll(m => m.Address == address);
                        _found.Add(new BluetoothMount(e.Name ?? address, address, e.Rssi));
                    }
                }
                else if (e?.Event == "error") error = e.Message;
            }
            await process.WaitForExitAsync(ct);
            if (error is not null) throw new InvalidOperationException(error);
            lock (_sync) return _found.OrderByDescending(m => m.Rssi ?? -999).ToList();
        }
        finally
        {
            lock (_sync) _scanning = false;
        }
    }

    /// <summary>
    /// EquipmentService calls this before connecting any device. For the mount, when Bluetooth is on: start the
    /// helper, wait for the mount's link, and point the driver at the helper's serial port.
    /// </summary>
    private async Task PrepareAsync(IDevice device, CancellationToken ct)
    {
        if (device is not IMount || !_settings.Enabled || !Applicable) return;
        await EnsureLinkAsync(ct);
        if (_equipment.Provider.Settings is not { } settings) return;
        var properties = settings.GetProperties(device.Name);
        if (properties.FirstOrDefault(p => p.Name == "CONNECTION_MODE") is { } mode && mode.Elements.Any(e => e.Name == "CONNECTION_SERIAL"))
            await settings.SetPropertyAsync(device.Name, "CONNECTION_MODE", new Dictionary<string, string> { ["CONNECTION_SERIAL"] = "On" }, ct);
        if (properties.Any(p => p.Name == "DEVICE_PORT"))
            await settings.SetPropertyAsync(device.Name, "DEVICE_PORT", new Dictionary<string, string> { ["PORT"] = LinkPath }, ct);
    }

    private async Task EnsureLinkAsync(CancellationToken ct)
    {
        Task<bool> up;
        lock (_sync)
        {
            if (_bridge is null || _bridge.HasExited) StartBridge();
            up = _linkUp!.Task;
        }
        var finished = await Task.WhenAny(up, Task.Delay(ConnectTimeout, ct));
        ct.ThrowIfCancellationRequested();
        if (finished != up)
            throw new InvalidOperationException(
                $"No Bluetooth link to {_settings.Mount ?? "a ZWO mount"} after {ConnectTimeout.TotalSeconds:0} s. Is it on and in range, and is ZWO's phone app closed? (Still trying.)");
        if (!await up) throw new InvalidOperationException(_detail ?? "The Bluetooth helper stopped");
    }

    // Called with _sync held.
    private void StartBridge()
    {
        var args = new List<string> { "bridge", "--link", LinkPath };
        if (_settings.Address is { } address) args.AddRange(["--address", address]);
        else if (_settings.Mount is { } mount) args.AddRange(["--name", mount]);
        _linkUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _state = "Searching";
        _detail = null;
        try
        {
            var process = StartHelper(args, stdin: true);
            _bridge = process;
            _ = Task.Run(() => ReadBridgeAsync(process));
        }
        catch (Exception ex)
        {
            _bridge = null;
            _state = "Error";
            _detail = ex.Message;
            _linkUp.TrySetResult(false);
        }
    }

    private async Task ReadBridgeAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (ParseEvent(line) is { } e) Apply(process, e);
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // stopped
        }
        lock (_sync)
        {
            if (_bridge != process) return;
            _bridge = null;
            if (_state != "Error") _state = "Idle";
            _linkUp?.TrySetResult(false);
        }
        process.Dispose();
    }

    private void Apply(Process process, BridgeEvent e)
    {
        string? message = null;
        bool warn = false;
        lock (_sync)
        {
            if (_bridge != process) return;
            var was = _state;
            switch (e.Event)
            {
                case "searching": _state = "Searching"; break;
                case "connecting": _state = "Connecting"; _detail = e.Name; break;
                case "connected":
                    _state = "Connected";
                    _detail = e.Name;
                    _linkUp?.TrySetResult(true);
                    message = $"Bluetooth: connected to {e.Name ?? "the mount"}";
                    break;
                case "disconnected":
                    if (was == "Connected")
                    {
                        message = "Bluetooth link to the mount lost, reconnecting";
                        warn = true;
                        // Commands time out until it's back; a connect waiting now waits for the new link.
                        _linkUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    _state = "Searching";
                    break;
                case "error":
                    _detail = e.Message;
                    if (e.Fatal)
                    {
                        _state = "Error";
                        _linkUp?.TrySetResult(false);
                        message = $"Bluetooth: {e.Message}";
                        warn = true;
                    }
                    break;
            }
        }
        if (message is null) return;
        if (warn) _log.Warn(message);
        else _log.Info(message);
    }

    private void StopBridge()
    {
        Process? process;
        lock (_sync)
        {
            process = _bridge;
            _bridge = null;
            _state = "Idle";
            _linkUp?.TrySetResult(false);
        }
        if (process is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Closing its stdin lets it disconnect from the mount and remove the port; then make sure.
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // gone already
                }
            }
        });
    }

    private Process StartHelper(IEnumerable<string> args, bool stdin)
    {
        if (!File.Exists(_script)) throw new InvalidOperationException($"NOVA's Bluetooth helper is missing ({_script})");
        var info = new ProcessStartInfo(_python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(_script);
        foreach (var a in args) info.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("The Bluetooth helper didn't start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Bluetooth needs Python 3 ({_python} not found). Run: nova setup");
        }
        // Bleak's own warnings: not needed, but read so it never blocks on a full pipe.
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    public sealed record BridgeEvent(string Event, string? Name, string? Address, int? Rssi, string? Message, bool Fatal);

    /// <summary>One line from the helper ({"event": "found", "name": ..., ...}); null for anything else.</summary>
    public static BridgeEvent? ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String)
                return null;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new BridgeEvent(ev.GetString()!, Text("name"), Text("address"),
                root.TryGetProperty("rssi", out var r) && r.TryGetInt32(out var rssi) ? rssi : null,
                Text("message"),
                root.TryGetProperty("fatal", out var f) && f.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
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
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            lock (_sync) File.WriteAllText(_file, JsonSerializer.Serialize(_settings));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        StopBridge();
        return ValueTask.CompletedTask;
    }
}
