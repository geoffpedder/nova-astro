using Nova.Core.Equipment;

namespace Nova.Server.Services;

/// <summary>
/// Keeps every connected camera's FITS headers pointing at the chosen mount, filter wheel, focuser and
/// rotator (INDI's ACTIVE_DEVICES). Drivers default to INDI's simulators here, so without this real
/// frames lack FILTER, coordinates, pier side and focus position — which stacking software relies on.
/// Re-links whenever a camera connects (by NOVA or PHD2) or the chosen devices change.
/// </summary>
public sealed class CameraLinkService
{
    private readonly EquipmentService _equipment;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    // What each connected camera was last linked to, so we only send changes.
    private readonly Dictionary<string, LinkedDevices> _linked = new(StringComparer.Ordinal);

    public CameraLinkService(EquipmentService equipment, ActivityLog log)
    {
        _equipment = equipment;
        _log = log;
        equipment.DevicesChanged += (_, _) => Relink();
    }

    /// <summary>The links every camera should have right now.</summary>
    public LinkedDevices Current => new(
        Connected(_equipment.Mount), Connected(_equipment.FilterWheel), Connected(_equipment.Focuser), Connected(_equipment.Rotator));

    private void Relink()
    {
        var wanted = Current;
        var cameras = _equipment.Provider.Devices
            .Where(d => d.IsConnected && d.Kinds.Contains(DeviceKind.Camera))
            .Select(d => d.Name)
            .ToList();

        List<string> due;
        lock (_sync)
        {
            foreach (var gone in _linked.Keys.Except(cameras).ToList()) _linked.Remove(gone); // re-link on reconnect
            due = cameras.Where(c => !_linked.TryGetValue(c, out var links) || links != wanted).ToList();
            foreach (var camera in due) _linked[camera] = wanted;
        }

        foreach (var camera in due) _ = LinkAsync(camera, wanted);
    }

    private async Task LinkAsync(string camera, LinkedDevices links)
    {
        try
        {
            if (_equipment.Provider.GetCamera(camera) is not { } device) return;
            await device.LinkDevicesAsync(links, CancellationToken.None);
            _log.Info($"{camera}: FITS headers from {Describe(links)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _linked.Remove(camera); // try again on the next change
            _log.Warn($"Couldn't link {camera} to the chosen devices: {ex.Message}");
        }
    }

    // Only link devices that are actually there; a device that isn't connected gives the driver nothing to read.
    private static string? Connected(IDevice? device) => device is { IsConnected: true } ? device.Name : null;

    private static string Describe(LinkedDevices l)
    {
        var parts = new[] { l.Mount, l.FilterWheel, l.Focuser, l.Rotator }.Where(n => n is not null).ToList();
        return parts.Count == 0 ? "no other devices" : string.Join(", ", parts);
    }
}
