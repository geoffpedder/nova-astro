using System.Globalization;
using Nova.Core.Equipment;

namespace Nova.Indi;

/// <summary>Exposes the devices of one INDI server through Nova's equipment interfaces.</summary>
/// <param name="controlFifo">indiserver's control pipe for starting/stopping drivers, if it was started with one.</param>
public sealed class IndiEquipmentProvider(string host, int port, string? controlFifo = null) : IEquipmentProvider
{
    // DRIVER_INTERFACE bit flags, from libindi's basedevice.h.
    [Flags]
    private enum DriverInterface
    {
        Telescope = 1 << 0,
        Ccd = 1 << 1,
        Guider = 1 << 2,
        Focuser = 1 << 3,
        Filter = 1 << 4,
        Dome = 1 << 5,
        Weather = 1 << 7,
        DustCap = 1 << 9,
        LightBox = 1 << 10,
        Rotator = 1 << 12,
    }

    private readonly IndiClient _client = new();
    private readonly Dictionary<string, IndiCamera> _cameras = new();
    private readonly Dictionary<string, IndiMount> _mounts = new();
    private readonly Dictionary<string, IndiFocuser> _focusers = new();
    private readonly Dictionary<string, IndiFilterWheel> _wheels = new();
    private readonly Dictionary<string, IndiRotator> _rotators = new();
    private readonly Dictionary<string, IndiFlatPanel> _panels = new();
    private IndiDeviceSettings? _settings;
    private IndiDriverControl? _drivers;
    private bool _subscribed;

    public string Name => $"INDI {host}:{port}";
    public bool IsConnected => _client.IsConnected;

    public event EventHandler? Changed;
    public event EventHandler<string>? Message;

    // Some drivers with no hardware attached publish a property or two without a device name. That isn't a
    // device anyone can use, so it's left out of the list (and mentioned once, naming the driver if it says).
    private bool _reportedNameless;

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            var names = _client.DeviceNames;
            if (!_reportedNameless && names.Any(string.IsNullOrWhiteSpace))
            {
                _reportedNameless = true;
                var driver = names.Where(string.IsNullOrWhiteSpace)
                    .Select(n => _client.GetProperty(n, "DRIVER_INFO")?["DRIVER_EXEC"]?.Text)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                Message?.Invoke(this, $"Ignoring properties sent without a device name{(driver is null ? "" : $" by {driver}")} " +
                                      "(usually a driver with no hardware plugged in; remove unused drivers from /etc/nova/indi.env)");
            }
            return names
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => new DeviceInfo(d, KindsOf(d), _client.GetProperty(d, "CONNECTION")?["CONNECT"]?.IsOn == true))
                .OrderBy(d => d.Name)
                .ToList();
        }
    }

    public ICamera? GetCamera(string name) => Get(name, DeviceKind.Camera, _cameras, n => new IndiCamera(_client, n));
    public IMount? GetMount(string name) => Get(name, DeviceKind.Mount, _mounts, n => new IndiMount(_client, n));
    public IFocuser? GetFocuser(string name) => Get(name, DeviceKind.Focuser, _focusers, n => new IndiFocuser(_client, n));
    public IFilterWheel? GetFilterWheel(string name) => Get(name, DeviceKind.FilterWheel, _wheels, n => new IndiFilterWheel(_client, n));
    public IRotator? GetRotator(string name) => Get(name, DeviceKind.Rotator, _rotators, n => new IndiRotator(_client, n));
    public IFlatPanel? GetFlatPanel(string name) => Get(name, DeviceKind.FlatPanel, _panels, n => new IndiFlatPanel(_client, n));

    IDeviceSettings? IEquipmentProvider.Settings => Settings;
    public IDriverControl? Drivers => _drivers ??= controlFifo is null ? null : new IndiDriverControl(_client, controlFifo);
    public IndiDeviceSettings Settings => _settings ??= new IndiDeviceSettings(_client);

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (!_subscribed)
        {
            _client.PropertyDefined += (_, p) => { if (p.Name is "DRIVER_INFO" or "CONNECTION") RaiseChanged(); };
            _client.PropertyUpdated += (_, p) => { if (p.Name == "CONNECTION") RaiseChanged(); };
            _client.PropertyDeleted += (_, _) => RaiseChanged();
            _client.MessageReceived += (_, m) => Message?.Invoke(this, $"[{m.Device}] {m.Message}");
            _client.Disconnected += (_, error) =>
            {
                Message?.Invoke(this, error is null ? "INDI server disconnected" : $"INDI connection lost: {error.Message}");
                RaiseChanged();
            };
            _subscribed = true;
        }

        await _client.ConnectAsync(host, port, ct);
        // INDI has no "all properties sent" marker, so give drivers a moment to describe themselves.
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        RaiseChanged();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _client.DisconnectAsync();
        _cameras.Clear();
        _mounts.Clear();
        _focusers.Clear();
        _wheels.Clear();
        _rotators.Clear();
        _panels.Clear();
        RaiseChanged();
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private T? Get<T>(string name, DeviceKind kind, Dictionary<string, T> cache, Func<string, T> create) where T : class
    {
        if (!KindsOf(name).Contains(kind)) return null;
        lock (cache)
        {
            if (!cache.TryGetValue(name, out var device))
            {
                cache[name] = device = create(name);
                if (device is IndiDevice indi)
                    indi.PortFound += (_, port) => Message?.Invoke(this, $"[{name}] Not on its usual port: found it on {port}");
            }
            return device;
        }
    }

    private IReadOnlyList<DeviceKind> KindsOf(string device)
    {
        var text = _client.GetProperty(device, "DRIVER_INFO")?["DRIVER_INTERFACE"]?.Text;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)) return [DeviceKind.Other];

        var flags = (DriverInterface)bits;
        var kinds = new List<DeviceKind>();
        if (flags.HasFlag(DriverInterface.Ccd)) kinds.Add(DeviceKind.Camera);
        if (flags.HasFlag(DriverInterface.Telescope)) kinds.Add(DeviceKind.Mount);
        if (flags.HasFlag(DriverInterface.Focuser)) kinds.Add(DeviceKind.Focuser);
        if (flags.HasFlag(DriverInterface.Filter)) kinds.Add(DeviceKind.FilterWheel);
        if (flags.HasFlag(DriverInterface.Rotator)) kinds.Add(DeviceKind.Rotator);
        if (flags.HasFlag(DriverInterface.Guider)) kinds.Add(DeviceKind.Guider);
        if (flags.HasFlag(DriverInterface.Dome)) kinds.Add(DeviceKind.Dome);
        if (flags.HasFlag(DriverInterface.Weather)) kinds.Add(DeviceKind.Weather);
        if (flags.HasFlag(DriverInterface.LightBox) || flags.HasFlag(DriverInterface.DustCap)) kinds.Add(DeviceKind.FlatPanel);
        return kinds.Count > 0 ? kinds : [DeviceKind.Other];
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
