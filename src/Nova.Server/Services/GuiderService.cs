using Microsoft.Extensions.Options;
using Nova.Core.Guiding;

namespace Nova.Server.Services;

/// <param name="ScreenPort">Port of the browser view of PHD2's screen (noVNC), or null if not configured.</param>
/// <param name="AutoConnect">NOVA keeps trying to reach PHD2 (false after the user pressed Disconnect).</param>
public sealed record GuiderStatus(
    string Host, bool Connected, GuiderState State, bool Settling, string? Version,
    double? PixelScale, double? Snr, double? Hfd, GuideStats Stats, IReadOnlyList<GuidePoint> History,
    IReadOnlyList<Phd2Profile> Profiles, Phd2Profile? Profile, bool EquipmentConnected, int? ScreenPort, bool AutoConnect,
    DecDrift? Drift);

/// <summary>
/// Drives PHD2 over its event-server port. PHD2 does the actual guiding (with the guide camera and
/// mount connected to it through INDI); NOVA starts, stops and dithers it and shows how well it's doing.
/// </summary>
public sealed class GuiderService : IAsyncDisposable
{
    private readonly ActivityLog _log;
    private readonly NovaOptions _options;
    private readonly Phd2Client _phd2 = new();
    private CancellationTokenSource? _operation;
    // Auto-connect unless the user pressed Disconnect in the Guider panel.
    private bool _userDisconnected;

    public GuiderService(IOptions<NovaOptions> options, ActivityLog log)
    {
        _log = log;
        _options = options.Value;
        _phd2.Message += (_, message) => _log.Warn(message);
        _phd2.Disconnected += (_, _) =>
        {
            if (!_userDisconnected) _log.Warn("Disconnected from PHD2");
        };
    }

    private string Host => $"{_options.Phd2Host}:{_options.Phd2Port}";

    public GuiderStatus Status => new(
        Host, _phd2.IsConnected, _phd2.State, _phd2.IsSettling, _phd2.Version,
        _phd2.PixelScale, _phd2.Snr, _phd2.Hfd, _phd2.Stats, _phd2.History,
        _phd2.Profiles, _phd2.Profile, _phd2.EquipmentConnected, _options.Phd2ScreenPort > 0 ? _options.Phd2ScreenPort : null,
        WantsConnection, _phd2.Drift);

    public bool IsConnected => _phd2.IsConnected;

    /// <summary>Whether the background connector should keep trying to reach PHD2.</summary>
    public bool WantsConnection => _options.Phd2AutoConnect && !_userDisconnected;

    /// <summary>Background reconnect attempt: quiet, since PHD2 may simply not be running.</summary>
    public async Task TryAutoConnectAsync(CancellationToken ct)
    {
        await _phd2.ConnectAsync(_options.Phd2Host, _options.Phd2Port, ct);
        _log.Info($"Connected to PHD2 {_phd2.Version} at {Host} (profile '{_phd2.Profile?.Name}')");
    }

    /// <summary>Keeps profile and equipment state fresh; PHD2 has no events for these.</summary>
    public Task RefreshAsync(CancellationToken ct) => _phd2.RefreshEquipmentAsync(ct);

    public async Task SelectProfileAsync(int id, CancellationToken ct)
    {
        EnsureConnected();
        await _phd2.SetProfileAsync(id, ct);
        _log.Info($"PHD2 profile: {_phd2.Profile?.Name}");
    }

    /// <summary>Connects or disconnects PHD2's camera and mount (PHD2's "Connect All").</summary>
    public async Task SetEquipmentConnectedAsync(bool connected, CancellationToken ct)
    {
        EnsureConnected();
        if (!connected) _operation?.Cancel();
        await _phd2.SetEquipmentConnectedAsync(connected, ct);
        _log.Info(connected ? $"PHD2 equipment connected ({_phd2.Profile?.Name})" : "PHD2 equipment disconnected");
    }

    private void EnsureConnected()
    {
        if (!_phd2.IsConnected) throw new InvalidOperationException("Not connected to PHD2");
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _userDisconnected = false;
        await _phd2.ConnectAsync(_options.Phd2Host, _options.Phd2Port, ct);
        _log.Info($"Connected to PHD2 {_phd2.Version} at {Host} ({_phd2.State})");
    }

    public async Task DisconnectAsync()
    {
        _userDisconnected = true;
        _operation?.Cancel();
        await _phd2.DisconnectAsync();
        _log.Info("Disconnected from PHD2");
    }

    /// <summary>Whether PHD2 is guiding right now (not just connected).</summary>
    public bool IsGuiding => _phd2.IsConnected && _phd2.State == GuiderState.Guiding;

    /// <summary>Starts guiding and waits until it has settled (for automation such as sequences).</summary>
    public async Task GuideAndWaitAsync(SettleSettings settle, CancellationToken ct)
    {
        if (!_phd2.IsConnected) throw new InvalidOperationException("Not connected to PHD2");
        _operation?.Cancel();
        _log.Info("Starting guiding…");
        await _phd2.GuideAsync(settle, recalibrate: false, ct);
        _log.Info("Guiding and settled");
    }

    /// <summary>Dithers and waits until guiding has settled again.</summary>
    public async Task DitherAndWaitAsync(double pixels, bool raOnly, SettleSettings settle, CancellationToken ct)
    {
        if (!_phd2.IsConnected) throw new InvalidOperationException("Not connected to PHD2");
        _log.Info($"Dithering {pixels:0.#} px…");
        await _phd2.DitherAsync(pixels, raOnly, settle, ct);
        _log.Info("Dither settled");
    }

    public bool StartGuiding(SettleSettings settle, bool recalibrate, out string? error) =>
        Start("Starting guiding", ct => _phd2.GuideAsync(settle, recalibrate, ct), "Guiding and settled", out error);

    public bool Dither(double pixels, bool raOnly, SettleSettings settle, out string? error) =>
        Start($"Dithering {pixels:0.#} px", ct => _phd2.DitherAsync(pixels, raOnly, settle, ct), "Dither settled", out error);

    public async Task StopAsync(CancellationToken ct)
    {
        _operation?.Cancel();
        if (!_phd2.IsConnected) return;
        await _phd2.StopAsync(ct);
        _log.Info("Guiding stopped");
    }

    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel();
        await _phd2.DisposeAsync();
    }

    private bool Start(string description, Func<CancellationToken, Task> operation, string doneMessage, out string? error)
    {
        if (!_phd2.IsConnected)
        {
            error = "Not connected to PHD2";
            return false;
        }

        _operation?.Cancel();
        var cts = new CancellationTokenSource();
        _operation = cts;
        error = null;

        _ = Task.Run(async () =>
        {
            _log.Info($"{description}…");
            try
            {
                await operation(cts.Token);
                _log.Info(doneMessage);
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"{description}: cancelled");
            }
            catch (Exception ex)
            {
                _log.Error($"{description} failed", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref _operation, null, cts);
                cts.Dispose();
            }
        });
        return true;
    }
}
