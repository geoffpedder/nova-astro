using Nova.Core.Equipment;

namespace Nova.Indi;

/// <summary>
/// Starts and stops drivers through indiserver's control pipe (indiserver -f), one command per line:
/// "start indi_simulator_ccd" / "stop indi_simulator_ccd". deploy/indiserver-start.sh creates the pipe.
/// </summary>
public sealed class IndiDriverControl(IndiClient client, string fifoPath) : IDriverControl
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // The pipe only works on the machine running indiserver (NOVA's usual setup on the mini PC).
    public bool Available => !OperatingSystem.IsWindows() && File.Exists(fifoPath);

    public string? DriverOf(string device) =>
        client.GetProperty(device, "DRIVER_INFO")?["DRIVER_EXEC"]?.Text is { Length: > 0 } exec ? exec : null;

    public Task StartAsync(string driver, CancellationToken ct) => SendAsync($"start {driver}", ct);
    public Task StopAsync(string driver, CancellationToken ct) => SendAsync($"stop {driver}", ct);

    private async Task SendAsync(string command, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException($"indiserver's control pipe ({fifoPath}) isn't available");
        if (command.Any(char.IsControl)) throw new ArgumentException("Invalid driver name");

        await _writeLock.WaitAsync(ct);
        try
        {
            // Opening a pipe for writing waits for a reader; indiserver always has it open, but don't hang if not.
            await Task.Run(() =>
            {
                using var pipe = new FileStream(fifoPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                pipe.Write(System.Text.Encoding.ASCII.GetBytes(command + "\n"));
            }, ct).WaitAsync(WriteTimeout, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
