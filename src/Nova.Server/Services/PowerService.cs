using System.Diagnostics;
using Nova.Core.Equipment;

namespace Nova.Server.Services;

public sealed record PowerStatus(bool Available, string? Problem, string? Pending);

/// <summary>
/// Restarting or shutting down the mini PC from the web page. First it stops what's running (sequence,
/// autofocus, polar alignment, the exposure), then — if asked — parks the mount and turns the camera's cooler
/// off, then asks systemd. NOVA runs as an ordinary user, so the polkit rule that deploy/install.sh installs
/// (/etc/polkit-1/rules.d/49-nova-power.rules) is what allows it. Whether it's allowed is asked of polkit itself
/// (pkcheck): the rules folder isn't readable by ordinary users, so looking for the file can't tell.
/// </summary>
public sealed class PowerService(
    EquipmentService equipment, ImagingService imaging, SequencerService sequencer, AutofocusService autofocus,
    PolarAlignmentService polar, ActivityLog log)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);
    private string? _allowedProblem;
    private DateTime _checkedUtc = DateTime.MinValue;
    private int _checking;
    public const string RuleFile = "/etc/polkit-1/rules.d/49-nova-power.rules";
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Runs a command and returns its exit code and output (replaceable in tests).</summary>
    public Func<string, string, Task<(int ExitCode, string Output)>> Run { get; set; } = RunProcessAsync;

    /// <summary>Why this machine can't do it, or null (Linux, and polkit says yes); replaceable in tests.</summary>
    public Func<string?>? CheckProblem { get; set; }

    private string? Problem() => CheckProblem is { } check ? check() : DefaultProblem();

    public string? Pending { get; private set; }

    public PowerStatus Status
    {
        get
        {
            var problem = Problem();
            return new PowerStatus(problem is null, problem, Pending);
        }
    }

    /// <summary>
    /// On Linux: what polkit last said about NOVA powering off (asked in the background, at most once a minute,
    /// since the status is read every second). Until the first answer, assume yes; systemctl will say otherwise.
    /// </summary>
    private string? DefaultProblem()
    {
        if (!OperatingSystem.IsLinux()) return "Restart and shut down work on the Linux mini PC only";
        if (DateTime.UtcNow - _checkedUtc > RecheckEvery && Interlocked.Exchange(ref _checking, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _allowedProblem = await AskPolkitAsync();
                }
                finally
                {
                    _checkedUtc = DateTime.UtcNow;
                    Interlocked.Exchange(ref _checking, 0);
                }
            });
        }
        return _allowedProblem;
    }

    private async Task<string?> AskPolkitAsync()
    {
        try
        {
            var (code, output) = await RunProcessAsync("pkcheck",
                $"--action-id org.freedesktop.login1.power-off --process {Environment.ProcessId}");
            return code == 0
                ? null
                : "NOVA isn't allowed to restart or shut down yet: run sudo deploy/install.sh out/linux-x64 again (it installs the permission)" +
                  (string.IsNullOrWhiteSpace(output) ? "" : $" (polkit: {output.Trim()})");
        }
        catch (Exception)
        {
            return null; // no pkcheck to ask: let systemctl answer when it's tried
        }
    }

    /// <param name="reboot">Restart rather than power off.</param>
    /// <param name="park">Park the mount first.</param>
    /// <param name="coolerOff">Turn the camera's cooler off first.</param>
    public async Task<string?> ShutdownAsync(bool reboot, bool park, bool coolerOff, CancellationToken ct)
    {
        if (Problem() is { } problem) return problem;
        if (Pending is not null) return $"Already {Pending.ToLowerInvariant()}";
        var what = reboot ? "Restarting" : "Shutting down";
        Pending = what;
        log.Warn($"{what} the mini PC (asked from the web page)");
        try
        {
            // Stop everything that would otherwise be cut off mid-way.
            if (sequencer.Running) sequencer.Abort();
            if (autofocus.Running) autofocus.Cancel();
            if (polar.Running) polar.Stop();
            imaging.Abort();
            await Task.Delay(500, ct); // let them wind down

            if (park && equipment.Mount is { IsConnected: true } mount && mount.State != MountState.Parked)
            {
                log.Info("Parking the mount before powering down");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ParkTimeout);
                    await mount.ParkAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    log.Warn($"Couldn't park the mount ({ex.Message}); carrying on");
                }
            }
            if (coolerOff && equipment.Camera is { IsConnected: true } camera && camera.Controls.Cooler is { On: true })
            {
                log.Info("Turning the camera's cooler off");
                try
                {
                    await camera.ApplyAsync(new CameraSettingsUpdate(CoolerOn: false), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warn($"Couldn't turn the cooler off ({ex.Message}); carrying on");
                }
            }

            var (code, output) = await Run("systemctl", reboot ? "reboot" : "poweroff");
            if (code != 0)
            {
                Pending = null;
                var message = $"The mini PC refused to {(reboot ? "restart" : "shut down")}: {output.Trim()}";
                log.Error(message);
                return message;
            }
            log.Warn($"{what} now");
            return null;
        }
        catch (Exception ex)
        {
            Pending = null;
            log.Error($"{what} failed", ex);
            return ex.Message;
        }
    }

    private static async Task<(int, string)> RunProcessAsync(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Couldn't run {file}");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + error);
    }
}
