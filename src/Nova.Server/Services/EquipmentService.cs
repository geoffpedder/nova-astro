using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;
using Nova.Core.Simulation;
using Nova.Indi;

namespace Nova.Server.Services;

/// <summary>
/// Which device to use for each role. Null means "automatic": the first one found; <see cref="None"/> means none, so
/// the role stays empty (no simulated rotator connected just because a simulator driver is running).
/// </summary>
public sealed record EquipmentSelection(
    string? Camera = null, string? Mount = null, string? Focuser = null, string? FilterWheel = null, string? Rotator = null,
    string? FlatPanel = null)
{
    public const string None = "(none)";
}

/// <summary>
/// Owns the equipment provider, remembers which devices the user chose,
/// and runs long device operations (slews, focuser moves) in the background.
/// </summary>
public sealed class EquipmentService : IAsyncDisposable
{
    private static readonly DeviceKind[] SelectableKinds =
        [DeviceKind.Camera, DeviceKind.Mount, DeviceKind.Focuser, DeviceKind.FilterWheel, DeviceKind.Rotator, DeviceKind.FlatPanel];
    // Camera and mount are expected; the rest are optional and only warned about if explicitly chosen.
    private static readonly DeviceKind[] RequiredKinds = [DeviceKind.Camera, DeviceKind.Mount];

    private readonly ActivityLog _log;
    private readonly string _selectionFile;
    private readonly SemaphoreSlim _providerLock = new(1, 1);
    // One running background operation per device kind; a new one cancels the old.
    private readonly Dictionary<DeviceKind, CancellationTokenSource> _operations = new();

    // Hot-plug tracking: which devices the provider currently lists, and which we're connecting now.
    private readonly object _devicesLock = new();
    private HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal); // every device seen since NOVA started
    private readonly HashSet<string> _connecting = new(StringComparer.Ordinal);
    private static readonly TimeSpan ReappearSettleTime = TimeSpan.FromSeconds(2);

    public EquipmentService(IOptions<NovaOptions> options, ActivityLog log)
    {
        _log = log;
        var o = options.Value;
        Provider = o.Backend.Equals("Indi", StringComparison.OrdinalIgnoreCase)
            ? new IndiEquipmentProvider(o.IndiHost, o.IndiPort, o.IndiControlFifo)
            : new SimulatorProvider();
        Provider.Message += (_, message) => _log.Info(message);
        Provider.Changed += (_, _) =>
        {
            OnDevicesChanged();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };

        // Separate files per backend, so simulator choices don't clobber real ones.
        _selectionFile = Path.Combine(o.ResolveConfigDirectory(), $"equipment.{o.Backend.ToLowerInvariant()}.json");
        Selection = LoadSelection();
    }

    public IEquipmentProvider Provider { get; }

    /// <summary>The devices NOVA offers: the provider's, minus any filtered out.</summary>
    public IReadOnlyList<DeviceInfo> VisibleDevices =>
        Provider.Devices;

    /// <summary>Runs just before a device connects (e.g. pointing the mount's driver at its Bluetooth link).</summary>
    public Func<IDevice, CancellationToken, Task>? BeforeConnect { get; set; }

    public void NotifyDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Devices appeared, disappeared or changed connection. Unlike Provider.Changed this also fires when
    /// NOVA itself connects or disconnects a device, which the simulator doesn't announce.
    /// </summary>
    public event EventHandler? DevicesChanged;
    public EquipmentSelection Selection { get; private set; }

    /// <summary>
    /// The camera to image with. If the user picked one that isn't present, this is null rather
    /// than silently falling back to another camera (which could be the guide camera).
    /// </summary>
    public ICamera? Camera => Resolve(DeviceKind.Camera, Selection.Camera, Provider.GetCamera);
    public IMount? Mount => Resolve(DeviceKind.Mount, Selection.Mount, Provider.GetMount);
    public IFocuser? Focuser => Resolve(DeviceKind.Focuser, Selection.Focuser, Provider.GetFocuser);
    public IFilterWheel? FilterWheel => Resolve(DeviceKind.FilterWheel, Selection.FilterWheel, Provider.GetFilterWheel);
    public IRotator? Rotator => Resolve(DeviceKind.Rotator, Selection.Rotator, Provider.GetRotator);
    public IFlatPanel? FlatPanel => Resolve(DeviceKind.FlatPanel, Selection.FlatPanel, Provider.GetFlatPanel);

    /// <summary>
    /// True once the user has pressed Connect: the chosen devices should be connected.
    /// The connection to the INDI server itself is separate and kept up by ProviderConnector,
    /// so devices can be listed and chosen before connecting them.
    /// </summary>
    public bool EquipmentConnected { get; private set; }

    /// <summary>Connects the provider if needed (one attempt), used by ProviderConnector and Connect.</summary>
    public async Task EnsureProviderConnectedAsync(CancellationToken ct)
    {
        await _providerLock.WaitAsync(ct);
        try
        {
            if (!Provider.IsConnected) await Provider.ConnectAsync(ct);
        }
        finally
        {
            _providerLock.Release();
        }
    }

    /// <summary>Connects the selected devices.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await EnsureProviderConnectedAsync(ct);
        EquipmentConnected = true;

        // Camera last, so it's linked to the other devices (for FITS headers) once, after they're up.
        foreach (var kind in SelectableKinds.OrderBy(k => k == DeviceKind.Camera))
        {
            var device = DeviceFor(kind);
            await ConnectDeviceAsync(device, ct);
            var chosen = Chosen(kind);
            if (chosen == EquipmentSelection.None) continue;
            if (device is null && (RequiredKinds.Contains(kind) || chosen is not null))
                _log.Warn(chosen is null ? $"No {Label(kind)} found" : $"{Capitalized(kind)} '{chosen}' not found");
        }
    }

    /// <summary>Disconnects the selected devices. The INDI server connection stays up.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        CancelOperations();
        EquipmentConnected = false;
        foreach (var device in SelectableKinds.Select(DeviceFor))
        {
            if (device is not { IsConnected: true }) continue;
            try
            {
                await device.DisconnectAsync(ct);
                _log.Info($"{device.Name} disconnected");
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Error($"Could not disconnect {device.Name}", ex);
            }
        }
    }

    /// <summary>
    /// Chooses the device for a role (null name = automatic) and saves the choice.
    /// If equipment is already connected, the new device is connected straight away.
    /// The previous device is left connected: another program (e.g. PHD2) may be using it.
    /// </summary>
    public async Task SelectAsync(DeviceKind kind, string? name, CancellationToken ct)
    {
        if (!SelectableKinds.Contains(kind))
            throw new ArgumentException($"Selecting a {kind} is not supported yet");
        if (name is not null and not EquipmentSelection.None)
        {
            var device = VisibleDevices.FirstOrDefault(d => d.Name == name)
                ?? throw new ArgumentException($"No device named '{name}'");
            if (!device.Kinds.Contains(kind))
                throw new ArgumentException($"'{name}' is not a {Label(kind)}");
        }

        CancelOperation(kind);
        Selection = kind switch
        {
            DeviceKind.Camera => Selection with { Camera = name },
            DeviceKind.Mount => Selection with { Mount = name },
            DeviceKind.Focuser => Selection with { Focuser = name },
            DeviceKind.FilterWheel => Selection with { FilterWheel = name },
            DeviceKind.FlatPanel => Selection with { FlatPanel = name },
            _ => Selection with { Rotator = name },
        };
        SaveSelection();
        _log.Info($"{kind}: {(name == EquipmentSelection.None ? "none" : name ?? "automatic")}");

        if (EquipmentConnected) await ConnectDeviceAsync(DeviceFor(kind), ct);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The device in use for each role: the chosen one, or the one "automatic" picked; <see cref="EquipmentSelection.None"/>
    /// for a role with nothing in it.
    /// </summary>
    public EquipmentSelection InUse => new(
        Camera?.Name ?? Selection.Camera ?? EquipmentSelection.None, Mount?.Name ?? Selection.Mount ?? EquipmentSelection.None,
        Focuser?.Name ?? Selection.Focuser ?? EquipmentSelection.None, FilterWheel?.Name ?? Selection.FilterWheel ?? EquipmentSelection.None,
        Rotator?.Name ?? Selection.Rotator ?? EquipmentSelection.None, FlatPanel?.Name ?? Selection.FlatPanel ?? EquipmentSelection.None);

    /// <summary>
    /// Puts back a saved choice (an equipment profile). Unlike <see cref="SelectAsync"/> the devices needn't be there
    /// yet: their drivers may still be starting. Only while equipment is disconnected.
    /// </summary>
    public void RestoreSelection(EquipmentSelection selection)
    {
        if (EquipmentConnected) throw new InvalidOperationException("Disconnect equipment first");
        CancelOperations();
        Selection = selection;
        SaveSelection();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool StartMountOperation(string description, Func<IMount, CancellationToken, Task> operation, out string? error) =>
        StartOperation(DeviceKind.Mount, Mount, description, operation, out error);

    public bool StartFocuserOperation(string description, Func<IFocuser, CancellationToken, Task> operation, out string? error) =>
        StartOperation(DeviceKind.Focuser, Focuser, description, operation, out error);

    public bool StartFilterWheelOperation(string description, Func<IFilterWheel, CancellationToken, Task> operation, out string? error) =>
        StartOperation(DeviceKind.FilterWheel, FilterWheel, description, operation, out error);

    public bool StartFlatPanelOperation(string description, Func<IFlatPanel, CancellationToken, Task> operation, out string? error) =>
        StartOperation(DeviceKind.FlatPanel, FlatPanel, description, operation, out error);

    public bool StartRotatorOperation(string description, Func<IRotator, CancellationToken, Task> operation, out string? error) =>
        StartOperation(DeviceKind.Rotator, Rotator, description, operation, out error);

    public async Task HaltRotatorAsync(CancellationToken ct)
    {
        CancelOperation(DeviceKind.Rotator);
        if (Rotator is { IsConnected: true } rotator)
        {
            await rotator.HaltAsync(ct);
            _log.Warn("Rotator halted");
        }
    }

    /// <summary>Cancels whatever mount operation is running (without telling the mount to stop).</summary>
    public void CancelMountOperation() => CancelOperation(DeviceKind.Mount);

    public async Task AbortMountAsync(CancellationToken ct)
    {
        CancelOperation(DeviceKind.Mount);
        if (Mount is { IsConnected: true } mount)
        {
            await mount.AbortAsync(ct);
            _log.Warn("Mount motion aborted");
        }
    }

    public async Task HaltFocuserAsync(CancellationToken ct)
    {
        CancelOperation(DeviceKind.Focuser);
        if (Focuser is { IsConnected: true } focuser)
        {
            await focuser.HaltAsync(ct);
            _log.Warn("Focuser halted");
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelOperations();
        await Provider.DisposeAsync();
    }

    /// <summary>
    /// Starts a device operation in the background and returns immediately. Any operation already
    /// running on that kind of device is cancelled first. Progress shows up in status broadcasts.
    /// </summary>
    private bool StartOperation<T>(DeviceKind kind, T? device, string description,
        Func<T, CancellationToken, Task> operation, out string? error) where T : class, IDevice
    {
        if (device is null || !device.IsConnected)
        {
            error = $"No {Label(kind)} connected";
            return false;
        }

        var cts = new CancellationTokenSource();
        lock (_operations)
        {
            if (_operations.Remove(kind, out var previous)) previous.Cancel();
            _operations[kind] = cts;
        }
        error = null;

        _ = Task.Run(async () =>
        {
            _log.Info($"{description}…");
            try
            {
                await operation(device, cts.Token);
                _log.Info($"{description}: done");
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"{description}: cancelled");
            }
            catch (Exception ex)
            {
                _log.Error($"{description} failed", ex);
            }
            finally
            {
                lock (_operations)
                {
                    if (_operations.TryGetValue(kind, out var current) && current == cts) _operations.Remove(kind);
                }
                cts.Dispose();
            }
        });
        return true;
    }

    private void CancelOperation(DeviceKind kind)
    {
        lock (_operations)
        {
            if (_operations.Remove(kind, out var cts)) cts.Cancel();
        }
    }

    private void CancelOperations()
    {
        foreach (var kind in SelectableKinds) CancelOperation(kind);
    }

    private IDevice? DeviceFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => Camera,
        DeviceKind.Mount => Mount,
        DeviceKind.Focuser => Focuser,
        DeviceKind.FilterWheel => FilterWheel,
        DeviceKind.Rotator => Rotator,
        DeviceKind.FlatPanel => FlatPanel,
        _ => null,
    };

    private string? Chosen(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => Selection.Camera,
        DeviceKind.Mount => Selection.Mount,
        DeviceKind.Focuser => Selection.Focuser,
        DeviceKind.FilterWheel => Selection.FilterWheel,
        DeviceKind.Rotator => Selection.Rotator,
        DeviceKind.FlatPanel => Selection.FlatPanel,
        _ => null,
    };

    private static string Label(DeviceKind kind) => kind switch
    {
        DeviceKind.FilterWheel => "filter wheel",
        DeviceKind.FlatPanel => "flat panel",
        _ => kind.ToString().ToLowerInvariant(),
    };
    private static string Capitalized(DeviceKind kind) => kind switch
    {
        DeviceKind.FilterWheel => "Filter wheel",
        DeviceKind.FlatPanel => "Flat panel",
        _ => kind.ToString(),
    };

    private T? Resolve<T>(DeviceKind kind, string? selected, Func<string, T?> get) where T : class
    {
        var visible = VisibleDevices;
        if (selected == EquipmentSelection.None) return null;
        if (selected is not null) return visible.Any(d => d.Name == selected) ? get(selected) : null;
        var first = visible.FirstOrDefault(d => d.Kinds.Contains(kind));
        return first is null ? null : get(first.Name);
    }

    private async Task ConnectDeviceAsync(IDevice? device, CancellationToken ct)
    {
        if (device is null || device.IsConnected) return;
        // Server reconnects, hot-plug and the Connect button can all race to connect the same device.
        lock (_devicesLock)
        {
            if (!_connecting.Add(device.Name)) return;
        }
        try
        {
            if (BeforeConnect is { } prepare) await prepare(device, ct);
            await device.ConnectAsync(ct);
            _log.Info($"{device.Name} connected");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not connect {device.Name}", ex);
        }
        finally
        {
            lock (_devicesLock) _connecting.Remove(device.Name);
        }
    }

    /// <summary>
    /// Hot-plug: when a chosen device reappears (e.g. a USB camera or EAF unplugged and plugged back in,
    /// which makes the ZWO driver delete and recreate the device) while equipment is connected, reconnect it.
    /// A new device that takes an empty role (e.g. a filter wheel driver started from Drivers…) is connected too.
    /// Only appearing triggers this, so deliberately disconnecting a device in the driver settings sticks.
    /// </summary>
    private void OnDevicesChanged()
    {
        var current = Provider.Devices.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        List<string> appeared, vanished;
        HashSet<string> returning;
        lock (_devicesLock)
        {
            appeared = current.Except(_present).ToList();
            vanished = _present.Except(current).ToList();
            _present = current;
            returning = appeared.Where(_seen.Contains).ToHashSet(StringComparer.Ordinal);
            _seen.UnionWith(appeared);
        }
        if (!EquipmentConnected) return;

        foreach (var name in vanished.Where(IsChosen))
            _log.Warn($"{name} disappeared (unplugged?). It will reconnect when it comes back.");

        foreach (var name in appeared)
        {
            _ = Task.Run(async () =>
            {
                // Give the driver a moment to define the device's properties (including what kind of device it is)
                // before deciding whether it's one NOVA uses.
                await Task.Delay(ReappearSettleTime);
                if (!EquipmentConnected) return;
                var device = SelectableKinds.Select(DeviceFor).FirstOrDefault(d => d?.Name == name);
                if (device is null || device.IsConnected) return;
                _log.Info(returning.Contains(name) ? $"{name} is back, reconnecting" : $"{name} appeared, connecting");
                await ConnectDeviceAsync(device, CancellationToken.None);
            });
        }
    }

    /// <summary>Whether this device is the one in use for any role (explicitly chosen or automatic).</summary>
    private bool IsChosen(string name) =>
        SelectableKinds.Any(kind => name == (Chosen(kind) ?? DeviceFor(kind)?.Name));

    private EquipmentSelection LoadSelection()
    {
        try
        {
            if (File.Exists(_selectionFile))
                return JsonSerializer.Deserialize<EquipmentSelection>(File.ReadAllText(_selectionFile)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_selectionFile}: {ex.Message}");
        }
        return new EquipmentSelection();
    }

    private void SaveSelection()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_selectionFile)!);
            File.WriteAllText(_selectionFile, JsonSerializer.Serialize(Selection));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save equipment selection to {_selectionFile}", ex);
        }
    }
}
