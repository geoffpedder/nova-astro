using System.Diagnostics;
using System.Globalization;

namespace Nova.Server.Services;

/// <param name="Utc">The mini PC's clock now.</param>
/// <param name="Synchronized">Whether it's being set from the internet (NTP); null when that can't be told.</param>
/// <param name="CanSet">Whether NOVA can set it here.</param>
/// <param name="Problem">Why not, when it can't.</param>
public sealed record ClockStatus(DateTime Utc, bool? Synchronized, bool CanSet, string? Problem);

/// <summary>
/// The mini PC's clock. Everything NOVA works out about the sky (targets' altitudes, twilight, the time sent to the
/// mount) comes from it, and with no internet at a dark site nothing corrects it. The web page compares it with the
/// clock of the phone, tablet or computer it's shown on and offers to set it from that: <c>timedatectl set-time</c>,
/// which a polkit rule from deploy/install.sh (49-nova-clock.rules) allows NOVA's user. A clock that's set from the
/// internet is left alone: then it's the other device that's wrong.
/// </summary>
public sealed class ClockService(EquipmentService equipment, ActivityLog log)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _setting = new(1, 1);
    private bool? _synchronized;
    private DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>Runs a command and returns its exit code and output (replaceable in tests).</summary>
    public Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> Run { get; set; } = RunProcessAsync;

    /// <summary>Why the clock shouldn't jump just now (a sequence is running), or null.</summary>
    public Func<string?> Busy { get; set; } = () => null;

    /// <summary>Whether this machine has timedatectl to ask (replaceable in tests).</summary>
    public Func<bool> Supported { get; set; } = OperatingSystem.IsLinux;

    public async Task<ClockStatus> StatusAsync(CancellationToken ct)
    {
        if (!Supported()) return new ClockStatus(DateTime.UtcNow, null, false, "The clock can be set on the Linux mini PC only");
        var synchronized = await SynchronizedAsync(ct);
        return new ClockStatus(DateTime.UtcNow, synchronized, synchronized != true,
            synchronized == true ? "The mini PC's clock is set from the internet" : null);
    }

    private async Task<bool?> SynchronizedAsync(CancellationToken ct)
    {
        // (A clock just set back makes the last check look like it's in the future: ask again.)
        var since = DateTime.UtcNow - _checkedUtc;
        if (since >= TimeSpan.Zero && since < RecheckEvery) return _synchronized;
        try
        {
            var (code, output) = await Run("timedatectl", ["show", "-p", "NTPSynchronized", "--value"], ct);
            _synchronized = code == 0 ? output.Trim() == "yes" : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _synchronized = null;
        }
        _checkedUtc = DateTime.UtcNow;
        return _synchronized;
    }

    /// <summary>
    /// Sets the clock to a time read from the browser's device. Returns why not, or null when it's set. Network time is
    /// switched off for the moment it takes (timedatectl refuses otherwise) and back on, so the internet corrects the
    /// clock again whenever there is some.
    /// </summary>
    public async Task<string?> SetAsync(DateTimeOffset time, CancellationToken ct)
    {
        var received = Stopwatch.StartNew();
        if (!Supported()) return "The clock can be set on the Linux mini PC only";
        var utc = time.UtcDateTime;
        if (utc.Year is < 2025 or > 2100) return $"That clock says {utc:yyyy-MM-dd}, which can't be right";
        if (Busy() is { } busy) return busy;

        await _setting.WaitAsync(ct);
        try
        {
            _checkedUtc = DateTime.MinValue;
            if (await SynchronizedAsync(ct) == true) return "The mini PC's clock is set from the internet, so it's left alone";

            var ntp = (await Run("timedatectl", ["show", "-p", "NTP", "--value"], ct)).Output.Trim() == "yes";
            if (ntp && await Run("timedatectl", ["set-ntp", "false", "--no-ask-password"], ct) is { ExitCode: not 0 } off)
                return Refused(off.Output);
            try
            {
                var was = DateTime.UtcNow;
                (int ExitCode, string Output) set = (0, "");
                // Network time takes a moment to stop, and until it has, the clock can't be set.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    var now = utc + received.Elapsed;
                    set = await Run("timedatectl",
                        ["set-time", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC", "--no-ask-password"], ct);
                    if (set.ExitCode == 0 || !set.Output.Contains("synchronization", StringComparison.OrdinalIgnoreCase)) break;
                    await Task.Delay(300, ct);
                }
                if (set.ExitCode != 0) return Refused(set.Output);
                var to = utc + received.Elapsed;
                log.Warn($"The mini PC's clock was {Describe(was - to)}: set from the browser's device to {to:yyyy-MM-dd HH:mm:ss} UTC");
            }
            finally
            {
                if (ntp) await Run("timedatectl", ["set-ntp", "true", "--no-ask-password"], CancellationToken.None);
                _checkedUtc = DateTime.MinValue;
            }

            // The mount was given the old time when it connected.
            if (equipment.Mount is { IsConnected: true } mount)
            {
                try
                {
                    await mount.SetClockAsync(DateTimeOffset.UtcNow, ct);
                    log.Info($"{mount.Name}: time sent");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warn($"Couldn't send the new time to {mount.Name} ({ex.Message}): reconnect it");
                }
            }
            return null;
        }
        finally
        {
            _setting.Release();
        }
    }

    private static string Refused(string output) =>
        output.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || output.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? "NOVA isn't allowed to set the clock yet: run sudo deploy/install.sh out/linux-x64 again (it installs the permission)"
            : $"The mini PC refused to set its clock: {output.Trim()}";

    /// <summary>"3 h 12 min behind", "40 s ahead".</summary>
    public static string Describe(TimeSpan ahead)
    {
        var size = ahead.Duration();
        var text = size.TotalDays >= 2 ? $"{size.TotalDays:0} days"
            : size.TotalHours >= 1 ? $"{(int)size.TotalHours} h {size.Minutes} min"
            : size.TotalMinutes >= 1 ? $"{size.Minutes} min {size.Seconds} s"
            : $"{size.TotalSeconds:0} s";
        return $"{text} {(ahead > TimeSpan.Zero ? "ahead" : "behind")}";
    }

    private static async Task<(int, string)> RunProcessAsync(string file, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't run {file}");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await output + await error);
    }
}
