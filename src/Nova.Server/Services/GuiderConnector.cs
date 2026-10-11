namespace Nova.Server.Services;

/// <summary>
/// Keeps NOVA connected to PHD2, which runs as a headless service on the mini PC, and keeps its
/// profile and equipment state fresh. Retries quietly: PHD2 may start after NOVA or not be installed.
/// </summary>
public sealed class GuiderConnector(GuiderService guider, ILogger<GuiderConnector> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (guider.IsConnected)
                    await guider.RefreshAsync(stoppingToken);
                else if (guider.WantsConnection)
                    await guider.TryAutoConnectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "PHD2 not reachable");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
