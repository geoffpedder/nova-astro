using System.Text.RegularExpressions;

namespace Nova.Server.Services;

/// <summary>
/// Watches the kernel's messages for a USB device being reset or dropped over and over, a sign its link is failing
/// (a guide camera on the main camera's hub was reset every second and a half), and says so in the activity log,
/// naming the device. Linux only; needs read access to the kernel log (the systemd-journal group, which the install
/// gives NOVA's user).
/// </summary>
public sealed partial class UsbHealthService(ActivityLog log) : BackgroundService
{
    /// <summary>This many resets or drops in a minute is a failing link, not a one-off.</summary>
    public const int Threshold = 5;

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10); // between warnings about one device
    private readonly Dictionary<string, DateTime> _warned = new();

    /// <summary>Reads the kernel messages since a time (replaceable in tests).</summary>
    public Func<DateTimeOffset, CancellationToken, Task<string>> ReadKernel { get; set; } = ReadKernelAsync;

    /// <summary>A device's name, from its USB port, e.g. "1-2.2" → "ZWO ASI120MM Mini" (replaceable in tests).</summary>
    public Func<string, string?> DeviceName { get; set; } = NameFromSysfs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        var since = DateTimeOffset.UtcNow;
        var unreadable = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, stoppingToken);
                var now = DateTimeOffset.UtcNow;
                var text = await ReadKernel(since, stoppingToken);
                since = now;
                if (text.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase))
                {
                    if (!unreadable) log.Warn("NOVA can't read the kernel log, so it can't spot failing USB devices: reinstall to fix (nova update --force)");
                    unreadable = true;
                    continue;
                }
                Check(text, now.UtcDateTime);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn($"Couldn't check USB devices: {ex.Message}");
            }
        }
    }

    /// <summary>Counts resets and drops per USB port in the kernel's messages, and warns about any over the threshold.</summary>
    public void Check(string kernelMessages, DateTime utc)
    {
        foreach (var (port, count) in Count(kernelMessages))
        {
            if (count < Threshold) continue;
            if (_warned.TryGetValue(port, out var last) && utc - last < Quiet) continue;
            _warned[port] = utc;
            var name = DeviceName(port) is { } n ? $"{n} (USB port {port})" : $"The USB device on port {port}";
            log.Warn($"{name} was reset or dropped {count} times in the last minute: its USB link is failing. Check its " +
                     "cable and connectors, and that it isn't sharing a hub with the main camera (its own cable is best).");
        }
    }

    /// <summary>Resets and disconnects per USB port, e.g. "usb 1-2.2: reset high-speed USB device number 4".</summary>
    public static Dictionary<string, int> Count(string kernelMessages)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in Event().Matches(kernelMessages))
        {
            var port = m.Groups["port"].Value;
            counts[port] = counts.GetValueOrDefault(port) + 1;
        }
        return counts;
    }

    [GeneratedRegex(@"usb (?<port>\d+-[\d.]+): (reset \S+ USB device|USB disconnect)")]
    private static partial Regex Event();

    private static async Task<string> ReadKernelAsync(DateTimeOffset since, CancellationToken ct)
    {
        var info = new System.Diagnostics.ProcessStartInfo("journalctl")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-k", "--no-pager", "-o", "cat", "--since", $"@{since.ToUnixTimeSeconds()}" }) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("journalctl didn't start");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return await output + await errors;
    }

    private static string? NameFromSysfs(string port)
    {
        string? Read(string file)
        {
            try
            {
                var path = Path.Combine("/sys/bus/usb/devices", port, file);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        var product = Read("product");
        var maker = Read("manufacturer");
        return product is null ? null : maker is null || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? product : $"{maker} {product}";
    }
}
