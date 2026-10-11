namespace Nova.Server.Services;

/// <summary>
/// Keeps NOVA connected to the INDI server (or simulator) from startup, reconnecting if it drops,
/// so the device list is always available for choosing equipment before pressing Connect.
/// </summary>
public sealed class ProviderConnector(EquipmentService equipment, ActivityLog log) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastError = null;
        var wasConnected = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var provider = equipment.Provider;
            if (!provider.IsConnected)
            {
                if (wasConnected) log.Warn($"Lost connection to {provider.Name}, retrying every {RetryInterval.TotalSeconds:0}s");
                wasConnected = false;
                try
                {
                    await equipment.EnsureProviderConnectedAsync(stoppingToken);
                    log.Info($"Connected to {provider.Name}");
                    lastError = null;
                    // If indiserver restarted mid-session its drivers restarted too: reconnect our devices.
                    if (equipment.EquipmentConnected) await equipment.ConnectAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Log once per distinct problem rather than every few seconds.
                    if (ex.Message != lastError) log.Warn($"Can't reach {provider.Name}: {ex.Message}. Retrying…");
                    lastError = ex.Message;
                }
            }
            wasConnected = provider.IsConnected;

            try
            {
                await Task.Delay(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
