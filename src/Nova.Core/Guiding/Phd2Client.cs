using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Core.Guiding;

// PHD2 event server protocol: https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring
// Newline-delimited JSON on TCP 4400. PHD2 pushes events ({"Event": ...}); clients send JSON-RPC 2.0
// requests ({"method", "params", "id"}) and get {"result"} or {"error"} back with the same id.

public enum GuiderState { Disconnected, Stopped, Selected, Calibrating, Guiding, LostLock, Paused, Looping }

/// <param name="Units">"arcsec" when PHD2 knows the pixel scale, otherwise "px".</param>
public sealed record GuideStats(double RmsRa, double RmsDec, double RmsTotal, double PeakRa, double PeakDec, int Samples, string Units);

/// <summary>One guide step's error, in <see cref="GuideStats.Units"/>.</summary>
public sealed record GuidePoint(double Ra, double Dec);

/// <summary>A PHD2 equipment profile (camera, mount and guiding settings).</summary>
public sealed record Phd2Profile(int Id, string Name);

/// <param name="Settle">Done when the star stays within <paramref name="Pixels"/> for <paramref name="Seconds"/>, or fails after <paramref name="Timeout"/>.</param>
public sealed record SettleSettings(double Pixels = 1.5, double Seconds = 10, double Timeout = 60);

public sealed class Phd2Client : IAsyncDisposable
{
    public const int DefaultPort = 4400;
    private const int HistoryLength = 100;

    private readonly object _sync = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<(double Ra, double Dec)> _history = new(); // raw pixels
    private readonly DecDriftMonitor _drift = new();
    private bool _driftReported;
    private TaskCompletionSource<bool>? _settle;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private int _nextId;

    public bool IsConnected => _readLoop is { IsCompleted: false };
    public GuiderState State { get; private set; } = GuiderState.Disconnected;
    public bool IsSettling { get; private set; }
    /// <summary>Arcseconds per guide-camera pixel, or null if PHD2 doesn't know it yet.</summary>
    public double? PixelScale { get; private set; }
    public double? Snr { get; private set; }
    public double? Hfd { get; private set; }
    public string? Version { get; private set; }
    public IReadOnlyList<Phd2Profile> Profiles { get; private set; } = [];
    public Phd2Profile? Profile { get; private set; }
    /// <summary>Whether PHD2 has its camera and mount connected (its "Connect All").</summary>
    public bool EquipmentConnected { get; private set; }

    /// <summary>A steady Dec drift guiding keeps correcting (poor polar alignment), once there's enough guiding to tell.</summary>
    public DecDrift? Drift
    {
        get
        {
            lock (_sync) return PixelScale is { } scale ? _drift.Estimate(scale) : null;
        }
    }

    /// <summary>Notable PHD2 happenings worth showing the user (alerts, star lost, settle results).</summary>
    public event EventHandler<string>? Message;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (IsConnected) return;
        _tcp?.Dispose();
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_stream, _readCts.Token));

        // PHD2 sends AppState on connect, but ask anyway so State is right immediately.
        var state = await CallAsync("get_app_state", null, ct);
        SetState(ParseState(state?.GetValue<string>()));
        await RefreshPixelScaleAsync(ct);
        try
        {
            await RefreshEquipmentAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // An old PHD2 without the profile API: guiding still works.
        }
    }

    /// <summary>Re-reads the profile list, current profile and whether PHD2's equipment is connected.</summary>
    public async Task RefreshEquipmentAsync(CancellationToken ct)
    {
        var profiles = await CallAsync("get_profiles", null, ct);
        Profiles = profiles is JsonArray array
            ? array.Select(p => new Phd2Profile(p!["id"]!.GetValue<int>(), p["name"]?.GetValue<string>() ?? "")).ToList()
            : [];
        var current = await CallAsync("get_profile", null, ct);
        Profile = current?["id"] is { } id ? new Phd2Profile(id.GetValue<int>(), current["name"]?.GetValue<string>() ?? "") : null;
        EquipmentConnected = (await CallAsync("get_connected", null, ct))?.GetValue<bool>() ?? false;
    }

    /// <summary>Switches PHD2 to another equipment profile (disconnecting its equipment first, as PHD2 requires).</summary>
    public async Task SetProfileAsync(int id, CancellationToken ct)
    {
        if (EquipmentConnected) await SetEquipmentConnectedAsync(false, ct);
        await CallAsync("set_profile", new JsonObject { ["id"] = id }, ct);
        await RefreshEquipmentAsync(ct);
    }

    /// <summary>Connects or disconnects all of PHD2's equipment in the current profile.</summary>
    public async Task SetEquipmentConnectedAsync(bool connected, CancellationToken ct)
    {
        await CallAsync("set_connected", new JsonObject { ["connected"] = connected }, ct);
        EquipmentConnected = connected;
        if (connected) await RefreshPixelScaleAsync(ct);
    }

    public async Task DisconnectAsync()
    {
        _readCts?.Cancel();
        _tcp?.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* reported via Disconnected */ }
        }
        _readLoop = null;
        _stream = null;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    /// <summary>RMS error over the recent guide steps (reset when guiding starts).</summary>
    public GuideStats Stats
    {
        get
        {
            lock (_sync)
            {
                var scale = PixelScale ?? 1;
                var units = PixelScale is null ? "px" : "arcsec";
                if (_history.Count == 0) return new GuideStats(0, 0, 0, 0, 0, 0, units);
                var ra = Math.Sqrt(_history.Average(p => p.Ra * p.Ra)) * scale;
                var dec = Math.Sqrt(_history.Average(p => p.Dec * p.Dec)) * scale;
                return new GuideStats(ra, dec, Math.Sqrt(ra * ra + dec * dec),
                    _history.Max(p => Math.Abs(p.Ra)) * scale, _history.Max(p => Math.Abs(p.Dec)) * scale,
                    _history.Count, units);
            }
        }
    }

    public IReadOnlyList<GuidePoint> History
    {
        get
        {
            lock (_sync)
            {
                var scale = PixelScale ?? 1;
                return _history.Select(p => new GuidePoint(p.Ra * scale, p.Dec * scale)).ToList();
            }
        }
    }

    /// <summary>
    /// Starts guiding (PHD2 loops, auto-selects a star and calibrates if needed) and waits until it has settled.
    /// </summary>
    public Task GuideAsync(SettleSettings settle, bool recalibrate, CancellationToken ct) =>
        RunWithSettleAsync("guide", new JsonObject { ["settle"] = SettleJson(settle), ["recalibrate"] = recalibrate }, settle, ct);

    /// <summary>Dithers by up to <paramref name="pixels"/> and waits until guiding has settled again.</summary>
    public Task DitherAsync(double pixels, bool raOnly, SettleSettings settle, CancellationToken ct) =>
        RunWithSettleAsync("dither", new JsonObject { ["amount"] = pixels, ["raOnly"] = raOnly, ["settle"] = SettleJson(settle) }, settle, ct);

    public Task StopAsync(CancellationToken ct) => CallAsync("stop_capture", null, ct);

    public Task LoopAsync(CancellationToken ct) => CallAsync("loop", null, ct);

    /// <summary>Sends a JSON-RPC request and returns its result; PHD2 errors become exceptions.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to PHD2");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _pending[id] = tcs;

        var request = new JsonObject { ["method"] = method, ["id"] = id };
        if (parameters is not null) request["params"] = parameters;
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\r\n");

        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await stream.WriteAsync(bytes, ct);
            }
            finally
            {
                _writeLock.Release();
            }
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        finally
        {
            lock (_sync) _pending.Remove(id);
        }
    }

    private async Task RunWithSettleAsync(string method, JsonObject parameters, SettleSettings settle, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settle = done;
        IsSettling = true;
        try
        {
            await CallAsync(method, parameters, ct);
            // PHD2 enforces its own timeout and reports it in SettleDone; ours is only a safety net.
            await done.Task.WaitAsync(TimeSpan.FromSeconds(settle.Timeout + 120), ct);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            IsSettling = false;
            if (_settle == done) _settle = null;
        }
    }

    private async Task RefreshPixelScaleAsync(CancellationToken ct)
    {
        try
        {
            var scale = (await CallAsync("get_pixel_scale", null, ct))?.GetValue<double>();
            PixelScale = scale > 0 ? scale : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PixelScale = null; // no camera connected in PHD2 yet
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    Handle(JsonNode.Parse(line)?.AsObject());
                }
                catch (JsonException)
                {
                    // Ignore a malformed line rather than dropping the connection.
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // DisconnectAsync
        }
        catch (Exception ex)
        {
            Message?.Invoke(this, $"PHD2 connection lost: {ex.Message}");
        }
        finally
        {
            SetState(GuiderState.Disconnected);
            IsSettling = false;
            lock (_sync)
            {
                foreach (var pending in _pending.Values) pending.TrySetException(new IOException("PHD2 disconnected"));
                _pending.Clear();
            }
            _settle?.TrySetException(new IOException("PHD2 disconnected"));
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void Handle(JsonObject? message)
    {
        if (message is null) return;

        // A response to one of our requests.
        if (message["id"] is { } idNode && message["Event"] is null)
        {
            TaskCompletionSource<JsonNode?>? tcs;
            lock (_sync) _pending.TryGetValue(idNode.GetValue<int>(), out tcs);
            if (tcs is null) return;
            if (message["error"] is { } error)
                tcs.TrySetException(new InvalidOperationException($"PHD2: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}"));
            else
                tcs.TrySetResult(message["result"]?.DeepClone());
            return;
        }

        switch (message["Event"]?.GetValue<string>())
        {
            case "Version":
                Version = message["PHDVersion"]?.GetValue<string>();
                break;
            case "AppState":
                SetState(ParseState(message["State"]?.GetValue<string>()));
                break;
            case "GuideStep":
                lock (_sync)
                {
                    _history.Add((Number(message, "RADistanceRaw"), Number(message, "DECDistanceRaw")));
                    if (_history.Count > HistoryLength) _history.RemoveAt(0);
                    var time = message["Timestamp"] is not null ? Number(message, "Timestamp") : Number(message, "Time");
                    _drift.Add(time, Number(message, "DECDistanceGuide"), message["DECDirection"]?.GetValue<string>(),
                        counts: !IsSettling && Number(message, "ErrorCode") == 0);
                }
                if (!_driftReported && Drift is { Significant: true } drift)
                {
                    _driftReported = true;
                    Message?.Invoke(this, $"Dec is drifting {drift.ArcsecPerMinute:0}″ a minute (guiding keeps pushing {drift.Direction.ToLowerInvariant()}): " +
                                          $"the polar alignment is at least {DecDriftMonitor.Describe(drift.PolarErrorArcmin)} off. Polar align again for better guiding.");
                }
                Snr = Number(message, "SNR");
                Hfd = Number(message, "HFD");
                if (State != GuiderState.Guiding) SetState(GuiderState.Guiding);
                break;
            case "StartGuiding":
                lock (_sync)
                {
                    _history.Clear();
                    _drift.Reset();
                }
                _driftReported = false;
                SetState(GuiderState.Guiding);
                _ = RefreshPixelScaleAsync(CancellationToken.None); // known for sure after calibration
                break;
            case "GuidingStopped" or "LoopingExposuresStopped":
                SetState(GuiderState.Stopped);
                break;
            case "LoopingExposures":
                if (State != GuiderState.Looping) SetState(GuiderState.Looping);
                break;
            case "StartCalibration" or "Calibrating":
                SetState(GuiderState.Calibrating);
                break;
            case "CalibrationFailed":
                Message?.Invoke(this, $"PHD2 calibration failed: {message["Reason"]?.GetValue<string>()}");
                break;
            case "StarLost":
                SetState(GuiderState.LostLock);
                Message?.Invoke(this, "PHD2 lost the guide star");
                break;
            case "Paused":
                SetState(GuiderState.Paused);
                break;
            case "Resumed":
                SetState(GuiderState.Guiding);
                break;
            case "StarSelected":
                if (State is GuiderState.Stopped or GuiderState.Looping) SetState(GuiderState.Selected);
                break;
            case "SettleDone":
                var status = (int)Number(message, "Status");
                if (status == 0)
                {
                    _settle?.TrySetResult(true);
                }
                else
                {
                    var reason = message["Error"]?.GetValue<string>() ?? "settling failed";
                    _settle?.TrySetException(new InvalidOperationException($"PHD2: {reason}"));
                }
                break;
            case "Alert":
                Message?.Invoke(this, $"PHD2: {message["Msg"]?.GetValue<string>()}");
                break;
        }
    }

    private void SetState(GuiderState state) => State = state;

    private static GuiderState ParseState(string? state) =>
        Enum.TryParse<GuiderState>(state, true, out var parsed) ? parsed : GuiderState.Stopped;

    private static double Number(JsonObject message, string key) =>
        message[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static JsonObject SettleJson(SettleSettings s) =>
        new() { ["pixels"] = s.Pixels, ["time"] = s.Seconds, ["timeout"] = s.Timeout };
}
