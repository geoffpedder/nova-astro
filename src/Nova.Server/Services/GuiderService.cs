using Microsoft.Extensions.Options;
using Nova.Core.Guiding;

namespace Nova.Server.Services;

/// <param name="ScreenPort">Port of the browser view of PHD2's screen (noVNC), or null if not configured.</param>
/// <param name="AutoConnect">NOVA keeps trying to reach PHD2 (false after the user pressed Disconnect).</param>
/// <param name="Backend">Which guider is in use: "phd2" or "nova" (NOVA's own).</param>
/// <param name="Nova">NOVA's own guider: settings, choices, calibration and star (also while PHD2 is in use, for switching).</param>
public sealed record GuiderStatus(
    string Host, bool Connected, GuiderState State, bool Settling, string? Version,
    double? PixelScale, double? Snr, double? Hfd, GuideStats Stats, IReadOnlyList<GuidePoint> History,
    IReadOnlyList<Phd2Profile> Profiles, Phd2Profile? Profile, bool EquipmentConnected, int? ScreenPort, bool AutoConnect,
    DecDrift? Drift, string Backend = "phd2", NovaGuiderStatus? Nova = null, GuideDiagnosis? Diagnosis = null);

/// <summary>
/// The guider everything else uses (sequences, notifications, the Guider panel), in front of one of two backends:
/// PHD2, driven over its event-server port (it guides with the guide camera and mount connected to it through
/// INDI), or NOVA's own guider (<see cref="NovaGuider"/>). NOVA starts, stops and dithers either and shows how well
/// it's doing.
/// </summary>
public sealed class GuiderService : IAsyncDisposable
{
    private readonly ActivityLog _log;
    private readonly NovaOptions _options;
    private readonly Phd2Client _phd2 = new();
    private readonly NovaGuider? _nova;
    private CancellationTokenSource? _operation;
    // Auto-connect unless the user pressed Disconnect in the Guider panel.
    private bool _userDisconnected;

    public GuiderService(IOptions<NovaOptions> options, ActivityLog log, NovaGuider? nova = null)
    {
        _log = log;
        _options = options.Value;
        _nova = nova;
        _phd2.Message += (_, message) => _log.Warn(message);
        _phd2.Disconnected += (_, _) =>
        {
            if (!_userDisconnected) _log.Warn("Disconnected from PHD2");
        };
    }

    private string Host => $"{_options.Phd2Host}:{_options.Phd2Port}";

    /// <summary>Which guider is in use: "phd2" or "nova".</summary>
    public string Backend => _nova?.Backend ?? "phd2";

    private bool IsNova => _nova is not null && Backend == "nova";

    /// <summary>NOVA's own guider (for its settings, calibration and guide frame), or null when not available.</summary>
    public NovaGuider? Nova => _nova;

    public GuiderStatus Status => WithDiagnosis(IsNova
        ? new(
            "NOVA", _nova!.IsReady, _nova.State, _nova.IsSettling, null,
            _nova.PixelScale, _nova.Snr, _nova.Hfd, _nova.Stats, _nova.History,
            [], null, _nova.Camera?.IsConnected ?? false, null, false, _nova.Drift, "nova", _nova.Status)
        : new(
            Host, _phd2.IsConnected, _phd2.State, _phd2.IsSettling, _phd2.Version,
            _phd2.PixelScale, _phd2.Snr, _phd2.Hfd, _phd2.Stats, _phd2.History,
            _phd2.Profiles, _phd2.Profile, _phd2.EquipmentConnected, _options.Phd2ScreenPort > 0 ? _options.Phd2ScreenPort : null,
            WantsConnection, _phd2.Drift, "phd2", _nova?.Status));

    /// <summary>
    /// Adds the verdict on the recent guiding (seeing-limited, or settings worth changing): against the seeing NOVA's
    /// guiding assistant measured when NOVA guides, from the errors' pattern alone with PHD2.
    /// </summary>
    private GuiderStatus WithDiagnosis(GuiderStatus status)
    {
        var seeing = IsNova && status.Stats.Units == "arcsec" ? _nova!.Seeing : null;
        // NOVA's min moves (pixels) in the history's units; PHD2 doesn't say what it uses.
        double? scale = status.Stats.Units == "arcsec" ? status.PixelScale : 1;
        var a = _nova?.Settings.Algorithms;
        var (minRa, minDec) = IsNova && a is not null && scale is { } s ? (a.RaMinMove * s, a.DecMinMove * s) : ((double?)null, (double?)null);
        return status with { Diagnosis = GuidingDiagnosis.Diagnose(status.History, status.Stats.Units, seeing?.Ra, seeing?.Dec, minRa, minDec) };
    }

    /// <summary>NOVA's guider: run the guiding assistant (corrections off) for this long.</summary>
    public bool Assistant(double seconds, out string? error)
    {
        if (seconds is < 30 or > 900)
        {
            error = "Run the assistant for 30–900 seconds";
            return false;
        }
        return StartNova("Guiding assistant", ct => _nova!.AssistantAsync(seconds, ct), "Guiding assistant done", out error);
    }

    /// <summary>Whether guiding can be started: PHD2 is connected, or NOVA's guider has a guide camera.</summary>
    public bool IsConnected => IsNova ? _nova!.IsReady : _phd2.IsConnected;

    /// <summary>Whether the background connector should keep trying to reach PHD2.</summary>
    public bool WantsConnection => !IsNova && _options.Phd2AutoConnect && !_userDisconnected;

    /// <summary>Switches between PHD2 and NOVA's own guider, stopping whichever was guiding.</summary>
    public async Task SetBackendAsync(string backend, CancellationToken ct)
    {
        if (_nova is null) throw new InvalidOperationException("NOVA's own guider isn't available");
        var value = backend.Trim().ToLowerInvariant();
        if (value == Backend) return;
        if (value is not ("phd2" or "nova")) throw new ArgumentException("The guider is 'phd2' or 'nova'");
        _operation?.Cancel();
        if (IsNova) await _nova.StopAsync();
        else if (_phd2.IsConnected) await _phd2.StopAsync(ct);
        _nova.SetBackend(value);
        _log.Info(value == "nova" ? "Guiding with NOVA's own guider" : "Guiding with PHD2");
    }

    /// <summary>NOVA's guider: loop the guide camera (no corrections), to see the guide star.</summary>
    public bool Loop(out string? error) =>
        StartNova("Looping the guide camera", ct => _nova!.LoopAsync(ct), "Looping", out error);

    /// <summary>NOVA's guider: take a dark library for the guide camera (the scope must be covered).</summary>
    public bool BuildDarks(IReadOnlyList<double> exposures, int frames, out string? error)
    {
        if (IsNova && _nova!.DarkRequestProblem(exposures, frames) is { } problem)
        {
            error = problem;
            return false;
        }
        return StartNova("Taking guide darks", ct => _nova!.BuildDarksAsync(exposures, frames, ct), "Guide darks done", out error);
    }

    /// <summary>NOVA's guider: calibrate now.</summary>
    public bool Calibrate(out string? error) =>
        StartNova("Calibrating the guider", ct => _nova!.CalibrateAsync(ct), "Calibration done", out error);

    private bool StartNova(string description, Func<CancellationToken, Task> operation, string doneMessage, out string? error)
    {
        if (!IsNova)
        {
            error = "NOVA's own guider isn't the one in use";
            return false;
        }
        return Start(description, operation, doneMessage, out error);
    }

    /// <summary>Background reconnect attempt: quiet, since PHD2 may simply not be running.</summary>
    public async Task TryAutoConnectAsync(CancellationToken ct)
    {
        await _phd2.ConnectAsync(_options.Phd2Host, _options.Phd2Port, ct);
        _log.Info($"Connected to PHD2 {_phd2.Version} at {Host} (profile '{_phd2.Profile?.Name}')");
    }

    /// <summary>Keeps profile and equipment state fresh; PHD2 has no events for these.</summary>
    public Task RefreshAsync(CancellationToken ct) => !IsNova && _phd2.IsConnected ? _phd2.RefreshEquipmentAsync(ct) : Task.CompletedTask;

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

    /// <summary>Whether guiding is on right now (not just connected).</summary>
    public bool IsGuiding => IsNova ? _nova!.State == GuiderState.Guiding : _phd2.IsConnected && _phd2.State == GuiderState.Guiding;

    /// <summary>Starts guiding and waits until it has settled (for automation such as sequences).</summary>
    public async Task GuideAndWaitAsync(SettleSettings settle, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException(NotReady);
        _operation?.Cancel();
        _log.Info("Starting guiding…");
        if (IsNova) await _nova!.GuideAsync(settle, recalibrate: false, ct);
        else await _phd2.GuideAsync(settle, recalibrate: false, ct);
        _log.Info("Guiding and settled");
    }

    /// <summary>Dithers and waits until guiding has settled again.</summary>
    public async Task DitherAndWaitAsync(double pixels, bool raOnly, SettleSettings settle, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException(NotReady);
        _log.Info($"Dithering {pixels:0.#} px…");
        if (IsNova) await _nova!.DitherAsync(pixels, raOnly, settle, ct);
        else await _phd2.DitherAsync(pixels, raOnly, settle, ct);
        _log.Info("Dither settled");
    }

    public bool StartGuiding(SettleSettings settle, bool recalibrate, out string? error) =>
        Start("Starting guiding",
            ct => IsNova ? _nova!.GuideAsync(settle, recalibrate, ct) : _phd2.GuideAsync(settle, recalibrate, ct),
            "Guiding and settled", out error);

    public bool Dither(double pixels, bool raOnly, SettleSettings settle, out string? error) =>
        Start($"Dithering {pixels:0.#} px",
            ct => IsNova ? _nova!.DitherAsync(pixels, raOnly, settle, ct) : _phd2.DitherAsync(pixels, raOnly, settle, ct),
            "Dither settled", out error);

    private string NotReady => IsNova ? "No guide camera: choose one in the Guider panel" : "Not connected to PHD2";

    public async Task StopAsync(CancellationToken ct)
    {
        _operation?.Cancel();
        if (IsNova)
        {
            if (!_nova!.IsRunning) return;
            await _nova.StopAsync();
            _log.Info("Guiding stopped");
            return;
        }
        if (!_phd2.IsConnected) return;
        await _phd2.StopAsync(ct);
        _log.Info("Guiding stopped");
    }

    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel();
        if (_nova is not null) await _nova.DisposeAsync();
        await _phd2.DisposeAsync();
    }

    private bool Start(string description, Func<CancellationToken, Task> operation, string doneMessage, out string? error)
    {
        if (!IsConnected)
        {
            error = NotReady;
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
