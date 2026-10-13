using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class SystemStatsTests
{
    [Fact]
    public void ParsesLinuxProcFiles()
    {
        // As a Linux mini PC writes them.
        var cpu = SystemStatsService.ParseCpu("cpu  4705 356 584 3699 23 0 23 0 0 0")!.Value;
        Assert.Equal(4705 + 356 + 584 + 3699 + 23 + 0 + 23 + 0, cpu.Total);
        Assert.Equal(4705 + 356 + 584 + 23, cpu.Busy); // idle (3699) and iowait (23) aren't busy
        Assert.Null(SystemStatsService.ParseCpu("cpu0 1 2 3 4"));
        Assert.Null(SystemStatsService.ParseCpu(null));

        Assert.Equal(0.42, SystemStatsService.ParseLoad("0.42 0.37 0.31 2/415 12345"));
        Assert.Null(SystemStatsService.ParseLoad(""));

        var (used, total) = SystemStatsService.ParseMemInfo([
            "MemTotal:        7917056 kB", "MemFree:          512000 kB", "MemAvailable:    5120000 kB", "Buffers:          100000 kB"]);
        Assert.Equal(7917056L * 1024, total);
        Assert.Equal((7917056L - 5120000) * 1024, used);
    }

    [Fact]
    public async Task ReadsTheMachinesStats()
    {
        var images = Path.Combine(Path.GetTempPath(), "nova-stats-" + Guid.NewGuid().ToString("N"), "not-made-yet");
        var stats = new SystemStatsService(Opts.Create(new NovaOptions { ImageDirectory = images }));

        var first = await stats.ReadAsync(CancellationToken.None);
        Assert.True(first.Cores > 0);
        Assert.True(first.NovaMemoryBytes > 0);
        Assert.NotNull(first.MemoryTotalBytes);
        Assert.False(string.IsNullOrEmpty(first.Host));

        // The image folder's disk, even before the folder exists.
        var disk = Assert.Single(first.Disks);
        Assert.Equal("Images", disk.Label);
        Assert.True(disk.TotalBytes > 0 && disk.FreeBytes > 0 && disk.FreeBytes <= disk.TotalBytes);

        if (OperatingSystem.IsLinux())
        {
            // From /proc: CPU use (0–100), load, memory in use, uptime.
            Assert.InRange(first.CpuPercent!.Value, 0, 100);
            Assert.NotNull(first.Load);
            Assert.InRange(first.MemoryUsedBytes!.Value, 1, first.MemoryTotalBytes!.Value);
            Assert.True(first.UptimeSeconds > 0);
            var again = await stats.ReadAsync(CancellationToken.None); // compared with the previous reading
            Assert.InRange(again.CpuPercent ?? 0, 0, 100);
        }
    }
}
