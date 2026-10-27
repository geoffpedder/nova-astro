namespace Nova.Server.Services;

/// <summary>
/// Notices USB serial adapters being plugged in (a new /dev/ttyUSB… or /dev/ttyACM…), so a serial device chosen in
/// NOVA (a flat panel, say) connects by itself when it's plugged in after Connect, without pressing its driver's Scan
/// Ports. Linux only.
/// </summary>
public sealed class SerialPortWatcher(EquipmentService equipment, ActivityLog log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);
    // A new port belongs to root for a moment, until udev gives it to the dialout group.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>The serial ports there now (replaceable in tests).</summary>
    public Func<IReadOnlySet<string>> ListPorts { get; set; } = SystemPorts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        var known = ListPorts();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, stoppingToken);
                var now = ListPorts();
                var added = Added(known, now);
                known = now;
                if (added.Count == 0) continue;
                await Task.Delay(Settle, stoppingToken);
                await equipment.SerialPortsAddedAsync(added, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn($"Couldn't connect a newly plugged-in serial device: {ex.Message}");
            }
        }
    }

    /// <summary>Ports in <paramref name="now"/> that weren't in <paramref name="before"/>, sorted.</summary>
    public static IReadOnlyList<string> Added(IReadOnlySet<string> before, IReadOnlySet<string> now) =>
        now.Where(p => !before.Contains(p)).Order(StringComparer.Ordinal).ToList();

    private static IReadOnlySet<string> SystemPorts()
    {
        try
        {
            return Directory.EnumerateFiles("/dev", "ttyUSB*").Concat(Directory.EnumerateFiles("/dev", "ttyACM*"))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }
}
