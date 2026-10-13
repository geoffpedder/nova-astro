using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>A disk NOVA saves to: where, how big and how much is free.</summary>
public sealed record DiskStats(string Label, string Path, long TotalBytes, long FreeBytes);

/// <summary>
/// The mini PC at a glance, for the Activity tab. Null members aren't available here (most come from Linux's /proc and
/// /sys; on another system only memory and disks are shown).
/// </summary>
/// <param name="CpuPercent">All cores together, since the previous reading (or over a quarter of a second).</param>
/// <param name="Load">The 1-minute load average.</param>
/// <param name="CpuTemperature">°C, the hottest thermal zone.</param>
/// <param name="NovaMemoryBytes">NOVA's own memory use (its working set).</param>
public sealed record SystemStats(
    double? CpuPercent, int Cores, double? Load, double? CpuTemperature,
    long? MemoryUsedBytes, long? MemoryTotalBytes, long NovaMemoryBytes,
    IReadOnlyList<DiskStats> Disks, double? UptimeSeconds, string Host);

/// <summary>Reads CPU, memory, temperature, uptime and the image disks' space when asked (nothing runs in between).</summary>
public sealed class SystemStatsService(IOptions<NovaOptions> options, StorageService? storage = null)
{
    private readonly object _sync = new();
    private (long Busy, long Total, DateTime At)? _lastCpu;

    public async Task<SystemStats> ReadAsync(CancellationToken ct)
    {
        var cpu = await CpuPercentAsync(ct);
        var (used, total) = Memory();
        return new SystemStats(
            cpu is { } c ? Math.Round(c, 1) : null, Environment.ProcessorCount, Load(), Temperature(),
            used, total, Process.GetCurrentProcess().WorkingSet64, Disks(), Uptime(), Environment.MachineName);
    }

    // ---- CPU: /proc/stat's first line, busy vs total jiffies between two readings ----

    private async Task<double?> CpuPercentAsync(CancellationToken ct)
    {
        if (ReadCpu() is not { } now) return null;
        (long Busy, long Total, DateTime At)? before;
        lock (_sync) before = _lastCpu;
        if (before is null || (DateTime.UtcNow - before.Value.At).TotalSeconds > 30)
        {
            // No recent reading to compare with: take two a moment apart.
            await Task.Delay(250, ct);
            before = now;
            now = ReadCpu() ?? now;
        }
        lock (_sync) _lastCpu = now;
        var total = now.Total - before.Value.Total;
        return total > 0 ? 100.0 * (now.Busy - before.Value.Busy) / total : null;
    }

    private static (long Busy, long Total, DateTime At)? ReadCpu() =>
        ParseCpu(ReadFirstLine("/proc/stat")) is { } cpu ? (cpu.Busy, cpu.Total, DateTime.UtcNow) : null;

    /// <summary>/proc/stat's "cpu" line: jiffies busy and in all (idle and iowait count as not busy).</summary>
    public static (long Busy, long Total)? ParseCpu(string? line)
    {
        if (line is null || !line.StartsWith("cpu ")) return null;
        var v = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
        if (v.Length < 4) return null;
        var idle = v[3] + (v.Length > 4 ? v[4] : 0); // idle + iowait
        var total = v.Take(Math.Min(v.Length, 8)).Sum(); // user nice system idle iowait irq softirq steal
        return (total - idle, total);
    }

    private static double? Load() => ParseLoad(ReadFirstLine("/proc/loadavg"));

    /// <summary>/proc/loadavg's first number: the 1-minute load average.</summary>
    public static double? ParseLoad(string? line) =>
        line is not null && double.TryParse(line.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static double? Uptime()
    {
        var line = ReadFirstLine("/proc/uptime");
        if (line is not null && double.TryParse(line.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return Environment.TickCount64 / 1000.0;
    }

    /// <summary>The hottest of /sys/class/thermal's zones (°C), which on a mini PC is the CPU package.</summary>
    private static double? Temperature()
    {
        try
        {
            if (!Directory.Exists("/sys/class/thermal")) return null;
            double? hottest = null;
            foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
                if (ReadFirstLine(Path.Combine(zone, "temp")) is { } t && long.TryParse(t, out var milli) && milli is > 0 and < 150_000)
                    hottest = Math.Max(hottest ?? 0, milli / 1000.0);
            return hottest is { } h ? Math.Round(h, 1) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Memory in use (total − available, as `free` shows it) and the total; elsewhere just the total.</summary>
    private static (long? Used, long? Total) Memory()
    {
        try
        {
            if (File.Exists("/proc/meminfo")) return ParseMemInfo(File.ReadAllLines("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var info = GC.GetGCMemoryInfo();
        return (null, info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : null);
    }

    /// <summary>/proc/meminfo: memory in use (MemTotal − MemAvailable) and MemTotal, in bytes.</summary>
    public static (long? Used, long? Total) ParseMemInfo(IEnumerable<string> lines)
    {
        var values = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => long.TryParse(p[1].Trim().Split(' ')[0], out var kb) ? kb * 1024 : (long?)null);
        var total = values.GetValueOrDefault("MemTotal");
        var available = values.GetValueOrDefault("MemAvailable");
        return (total - available, total);
    }

    /// <summary>The mini PC's image folder's disk, and the USB drive's when frames go there.</summary>
    private IReadOnlyList<DiskStats> Disks()
    {
        var disks = new List<DiskStats>();
        void Add(string label, string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var existing = full;
                while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
                var drive = new DriveInfo(existing);
                if (disks.Any(d => d.TotalBytes == drive.TotalSize && d.FreeBytes == drive.AvailableFreeSpace)) return; // same disk
                disks.Add(new DiskStats(label, full, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // not mounted, or no such drive
            }
        }

        Add("Images", options.Value.ResolveImageDirectory());
        try
        {
            if (storage?.Target == "usb" && storage.ImageRoot() is { } root) Add("USB drive", root);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        return disks;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
