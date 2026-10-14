using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Imaging;

namespace Nova.Server.Services;

/// <summary>A webcam to watch the rig with: its device (/dev/videoN, or "sim" for the simulator's test card) and name.</summary>
public sealed record WebcamDevice(string Path, string Name);

/// <param name="Device">The webcam; null = the first one found.</param>
public sealed record WebcamSettings(string? Device = null, int Width = 640, int Height = 480, int Fps = 5);

/// <summary>One frame, as the webcam sent it (JPEG; the test card is PNG).</summary>
public sealed record WebcamFrame(byte[] Data, string ContentType, DateTime Utc);

/// <param name="State">"Idle" (nobody watching), "Waiting" (watched, but no webcam plugged in yet), "Starting", "Streaming"
/// or "Error" (it tries again every couple of seconds while watched).</param>
/// <param name="Note">Something worth knowing while it streams, e.g. that it has to transcode.</param>
public sealed record WebcamStatus(
    IReadOnlyList<WebcamDevice> Devices, WebcamSettings Settings, string? Device, string State, int Viewers,
    double? Fps, string? Error, string? Note);

/// <summary>Where frames come from: a real webcam through ffmpeg, or the simulator's test card.</summary>
public interface IWebcamSource
{
    /// <summary>Delivers frames until cancelled; throws with a reason the user can act on if it can't.</summary>
    Task RunAsync(string device, WebcamSettings settings, Action<WebcamFrame> frame, Action<string> note, CancellationToken ct);
}

/// <summary>
/// The webcam view, for watching the rig from indoors. Capture runs only while someone watches: it starts with the
/// first viewer and stops 10 s after the last, so it costs nothing the rest of the night. Webcams send MJPEG, which is
/// passed on as it comes (no decoding or re-encoding), so it takes a few percent of a core at most.
/// </summary>
public sealed class WebcamService : IAsyncDisposable
{
    /// <summary>How long capture carries on after the last viewer goes (10 s: switching tabs or reloading shouldn't restart it).</summary>
    public TimeSpan StopAfter { get; set; } = TimeSpan.FromSeconds(10);

    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly bool _simulator;
    private readonly string _sysRoot;
    private readonly object _sync = new();
    private WebcamSettings _settings;
    private int _viewers;
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _stopTimer;
    private WebcamFrame? _latest;
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _state = "Idle";
    private string? _device;
    private string? _error;
    private string? _note;
    private readonly Queue<DateTime> _recent = new();

    private readonly string _controlsFile;
    private Dictionary<string, Dictionary<string, int>> _saved; // webcam name → control → value the user set

    public WebcamService(IOptions<NovaOptions> options, ActivityLog log, string sysRoot = "/sys/class/video4linux")
    {
        _log = log;
        _file = System.IO.Path.Combine(options.Value.ResolveConfigDirectory(), "webcam.json");
        _controlsFile = System.IO.Path.Combine(options.Value.ResolveConfigDirectory(), "webcam-controls.json");
        _simulator = !options.Value.Backend.Equals("Indi", StringComparison.OrdinalIgnoreCase);
        _sysRoot = sysRoot;
        _settings = Load();
        _saved = LoadControls();
        TestCard = new TestCardWebcamSource(() => SimulatedControls.Brightness);
    }

    /// <summary>Where frames come from; the ffmpeg source for webcams and the test card for "sim" unless replaced (tests).</summary>
    public IWebcamSource Webcams { get; set; } = new FfmpegWebcamSource();
    public IWebcamSource TestCard { get; set; }

    /// <summary>A webcam's controls (v4l2-ctl), and the simulator's pretend ones.</summary>
    public IWebcamControls V4l2 { get; set; } = new V4l2Controls();
    public SimulatedWebcamControls SimulatedControls { get; } = new();

    // ---- Controls ----

    private IWebcamControls ControlsFor(WebcamDevice device) => device.Path == "sim" ? SimulatedControls : V4l2;

    /// <summary>The webcam's controls (brightness, exposure, white balance…) with their ranges and values now.</summary>
    public async Task<WebcamControls> ControlsAsync(CancellationToken ct)
    {
        var device = Chosen();
        if (device is null) return new WebcamControls(null, [], "No webcam found");
        try
        {
            return new WebcamControls(device.Path, await ControlsFor(device).ListAsync(device.Path, ct), null);
        }
        catch (InvalidOperationException ex)
        {
            return new WebcamControls(device.Path, [], ex.Message);
        }
    }

    /// <summary>Sets one control (checked against the webcam's own range or choices) and remembers it for this webcam.</summary>
    public async Task SetControlAsync(string name, int value, CancellationToken ct)
    {
        var device = Chosen() ?? throw new InvalidOperationException("No webcam found");
        var controls = ControlsFor(device);
        var control = (await controls.ListAsync(device.Path, ct)).FirstOrDefault(c => c.Name == name)
            ?? throw new ArgumentException($"This webcam has no control '{name}'");
        var ok = control.Type switch
        {
            "bool" => value is 0 or 1,
            "menu" => control.Menu.Count == 0 ? value >= control.Min && value <= control.Max : control.Menu.Any(m => m.Value == value),
            _ => value >= control.Min && value <= control.Max,
        };
        if (!ok) throw new ArgumentException($"{control.Label} can't be {value}");
        await controls.SetAsync(device.Path, name, value, ct);
        lock (_sync)
        {
            if (!_saved.TryGetValue(device.Name, out var mine)) _saved[device.Name] = mine = [];
            mine[name] = value;
        }
        SaveControls();
    }

    /// <summary>Puts every control back to the webcam's default and forgets what was set.</summary>
    public async Task ResetControlsAsync(CancellationToken ct)
    {
        var device = Chosen() ?? throw new InvalidOperationException("No webcam found");
        var controls = ControlsFor(device);
        foreach (var c in AutoFirst(await controls.ListAsync(device.Path, ct)))
        {
            if (c.Value == c.Default) continue;
            try
            {
                await controls.SetAsync(device.Path, c.Name, c.Default, ct);
            }
            catch (InvalidOperationException)
            {
                // inactive now (set by an automatic mode): fine
            }
        }
        lock (_sync) _saved.Remove(device.Name);
        SaveControls();
        _log.Info($"Webcam {device.Name}: controls back to its defaults");
    }

    /// <summary>The saved values again, once the webcam streams (some webcams reset their controls when opened).</summary>
    private async Task ApplySavedAsync(WebcamDevice device, Task streaming, CancellationToken ct)
    {
        Dictionary<string, int>? mine;
        lock (_sync) mine = _saved.TryGetValue(device.Name, out var m) ? new(m) : null;
        if (mine is null || mine.Count == 0) return;
        try
        {
            await streaming.WaitAsync(ct);
            var controls = ControlsFor(device);
            var known = await controls.ListAsync(device.Path, ct);
            foreach (var c in AutoFirst(known.Where(k => mine.ContainsKey(k.Name))))
            {
                try
                {
                    await controls.SetAsync(device.Path, c.Name, mine[c.Name], ct);
                }
                catch (InvalidOperationException)
                {
                    // one that can't be set now: the others still
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    /// <summary>Automatic modes first: switching auto exposure off is what makes exposure time settable.</summary>
    private static IEnumerable<WebcamControl> AutoFirst(IEnumerable<WebcamControl> controls) =>
        controls.OrderBy(c => c.Name.Contains("auto", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private Dictionary<string, Dictionary<string, int>> LoadControls()
    {
        try
        {
            if (File.Exists(_controlsFile))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(_controlsFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.Warn($"Ignoring unreadable {_controlsFile}: {ex.Message}");
        }
        return [];
    }

    private void SaveControls()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_saved);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_controlsFile)!);
            File.WriteAllText(_controlsFile, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save the webcam controls to {_controlsFile}", ex);
        }
    }

    public WebcamSettings Settings => _settings;

    public IReadOnlyList<WebcamDevice> Devices
    {
        get
        {
            var devices = ListDevices(_sysRoot).ToList();
            if (_simulator) devices.Add(new WebcamDevice("sim", "Simulated webcam (test card)"));
            return devices;
        }
    }

    public WebcamStatus Status
    {
        get
        {
            lock (_sync)
            {
                double? fps = null;
                if (_recent.Count >= 2 && _state == "Streaming")
                {
                    var span = (_recent.Last() - _recent.First()).TotalSeconds;
                    fps = span > 0 ? Math.Round((_recent.Count - 1) / span, 1) : null;
                }
                return new WebcamStatus(Devices, _settings, _device ?? Chosen()?.Path, _state, _viewers, fps, _error, _note);
            }
        }
    }

    /// <summary>The capture devices in /sys/class/video4linux (a webcam also lists a metadata node, left out), by name.</summary>
    public static IEnumerable<WebcamDevice> ListDevices(string sysRoot)
    {
        if (!Directory.Exists(sysRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(sysRoot, "video*").Order(StringComparer.Ordinal))
        {
            string? Read(string name)
            {
                try
                {
                    var path = System.IO.Path.Combine(dir, name);
                    return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
            if (Read("index") is { } index && index != "0") continue;
            var node = System.IO.Path.GetFileName(dir);
            yield return new WebcamDevice($"/dev/{node}", Read("name") ?? node);
        }
    }

    public void UpdateSettings(WebcamSettings settings)
    {
        if (settings.Fps is < 1 or > 30) throw new ArgumentException("The webcam frame rate is 1–30 a second");
        if (settings.Width is < 160 or > 1920 || settings.Height is < 120 or > 1080) throw new ArgumentException("That webcam size isn't supported");
        if (settings.Device is { } d && !Devices.Any(x => x.Path == d)) throw new ArgumentException($"There's no webcam {d}");
        bool restart;
        lock (_sync)
        {
            restart = _settings != settings && _run is not null;
            _settings = settings;
        }
        Save();
        if (restart) Restart();
    }

    /// <summary>
    /// Frames for one viewer, from the first one available until the viewer goes (cancellation). The capture starts with
    /// the first viewer and stops a little after the last.
    /// </summary>
    public async IAsyncEnumerable<WebcamFrame> Watch([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Join();
        try
        {
            WebcamFrame? last = null;
            while (!ct.IsCancellationRequested)
            {
                Task next;
                WebcamFrame? frame;
                lock (_sync) (next, frame) = (_next.Task, _latest);
                if (frame is not null && !ReferenceEquals(frame, last))
                {
                    last = frame;
                    yield return frame;
                    continue;
                }
                try
                {
                    await next.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>One frame now (capturing briefly if nobody is watching); null when the webcam gives none in time.</summary>
    public async Task<WebcamFrame?> SnapshotAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await foreach (var frame in Watch(timeout.Token)) return frame;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        return null;
    }

    private void Join()
    {
        lock (_sync)
        {
            _viewers++;
            _stopTimer?.Cancel();
            _stopTimer = null;
            if (_run is null) StartLocked();
        }
    }

    private void Leave()
    {
        lock (_sync)
        {
            if (--_viewers > 0 || _run is null) return;
            // Keep it running a little: switching tabs or reloading shouldn't restart the webcam.
            var timer = new CancellationTokenSource();
            _stopTimer = timer;
            _ = Task.Delay(StopAfter, timer.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_sync)
                {
                    if (_viewers > 0 || _stopTimer != timer) return;
                    StopLocked();
                }
            }, TaskScheduler.Default);
        }
    }

    private void Restart()
    {
        lock (_sync)
        {
            StopLocked();
            if (_viewers > 0) StartLocked();
        }
    }

    private WebcamDevice? Chosen()
    {
        var devices = Devices;
        return _settings.Device is { } d ? devices.FirstOrDefault(x => x.Path == d) : devices.FirstOrDefault();
    }

    /// <summary>How long to wait before looking again for a webcam, or trying again after it failed or was unplugged.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the capture for as long as anyone watches: waits for a webcam to be plugged in, starts it, and starts again
    /// if it's unplugged or ffmpeg stops, so plugging one in (or back in) just works.
    /// </summary>
    private void StartLocked()
    {
        var run = new CancellationTokenSource();
        _run = run;
        (_latest, _error, _note) = (null, null, null);
        _recent.Clear();
        _state = "Starting";
        _ = Task.Run(async () =>
        {
            string? lastProblem = null;
            while (!run.IsCancellationRequested)
            {
                WebcamDevice? device;
                WebcamSettings settings;
                lock (_sync)
                {
                    if (_run != run) return;
                    device = Chosen();
                    settings = _settings;
                    _device = device?.Path;
                    if (device is null)
                        (_state, _error) = ("Waiting", "No webcam found yet: plug one into the mini PC (a different USB port from the cameras)");
                    else if (_state != "Streaming")
                        (_state, _error) = ("Starting", null);
                }
                if (device is not null)
                {
                    var source = device.Path == "sim" ? TestCard : Webcams;
                    if (lastProblem is null) _log.Info($"Webcam {device.Name}: streaming {settings.Width}×{settings.Height} at {settings.Fps} fps while it's watched");
                    Task firstFrame;
                    lock (_sync) firstFrame = _next.Task;
                    _ = ApplySavedAsync(device, firstFrame, run.Token);
                    try
                    {
                        await source.RunAsync(device.Path, settings, OnFrame, note => { lock (_sync) _note = note; }, run.Token);
                        lastProblem = null; // it ran and stopped (unplugged?): look again
                    }
                    catch (OperationCanceledException) when (run.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            if (_run != run) return;
                            (_state, _error) = ("Error", ex.Message);
                        }
                        // Said once, not every couple of seconds while it keeps failing the same way.
                        if (ex.Message != lastProblem) _log.Warn($"Webcam {device.Name}: {ex.Message}");
                        lastProblem = ex.Message;
                    }
                }
                try
                {
                    await Task.Delay(RetryAfter, run.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        void OnFrame(WebcamFrame frame)
        {
            TaskCompletionSource signal;
            lock (_sync)
            {
                if (_run != run) return;
                _latest = frame;
                (_state, _error) = ("Streaming", null);
                _recent.Enqueue(frame.Utc);
                while (_recent.Count > 20) _recent.Dequeue();
                signal = _next;
                _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }
    }

    private void StopLocked()
    {
        _run?.Cancel();
        _run = null;
        (_state, _error) = ("Idle", null);
        _latest = null;
        _recent.Clear();
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) StopLocked();
        return ValueTask.CompletedTask;
    }

    private WebcamSettings Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<WebcamSettings>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new WebcamSettings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save the webcam settings to {_file}", ex);
        }
    }
}

/// <summary>
/// Cuts a stream of concatenated JPEGs (MJPEG, as ffmpeg writes it) into frames: each runs from a start-of-image marker
/// (FF D8) to the next end-of-image marker (FF D9). Bytes outside frames are skipped.
/// </summary>
public sealed class MjpegSplitter
{
    private readonly List<byte> _buffer = [];

    public IEnumerable<byte[]> Add(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<byte[]>();
        while (true)
        {
            var start = IndexOf(0xD8, 0);
            if (start < 0)
            {
                // Keep a trailing FF: it may be the first half of the next marker.
                var keep = _buffer.Count > 0 && _buffer[^1] == 0xFF ? 1 : 0;
                _buffer.RemoveRange(0, _buffer.Count - keep);
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            var end = IndexOf(0xD9, 2);
            if (end < 0) break;
            frames.Add(_buffer.GetRange(0, end + 2).ToArray());
            _buffer.RemoveRange(0, end + 2);
        }
        return frames;
    }

    /// <summary>Where FF followed by <paramref name="marker"/> starts, from <paramref name="from"/>; −1 if not there.</summary>
    private int IndexOf(byte marker, int from)
    {
        for (var i = from; i < _buffer.Count - 1; i++)
            if (_buffer[i] == 0xFF && _buffer[i + 1] == marker) return i;
        return -1;
    }
}

/// <summary>
/// A real webcam through ffmpeg (V4L2): asks for MJPEG and passes it on untouched. A webcam without MJPEG at that size
/// is transcoded instead (more CPU), and the status says so.
/// </summary>
public sealed class FfmpegWebcamSource : IWebcamSource
{
    public async Task RunAsync(string device, WebcamSettings s, Action<WebcamFrame> frame, Action<string> note, CancellationToken ct)
    {
        var copied = await RunOnceAsync(device, s, transcode: false, frame, ct);
        if (copied || ct.IsCancellationRequested) return;
        note("This webcam doesn't send MJPEG at that size, so NOVA converts its frames (uses more CPU); a smaller size may avoid it");
        await RunOnceAsync(device, s, transcode: true, frame, ct);
    }

    /// <summary>Runs ffmpeg until cancelled. False when it gave no frame at all (to try transcoding); throws on other failures.</summary>
    private static async Task<bool> RunOnceAsync(string device, WebcamSettings s, bool transcode, Action<WebcamFrame> frame, CancellationToken ct)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-f", "v4l2",
            .. transcode ? Array.Empty<string>() : ["-input_format", "mjpeg"],
            "-video_size", $"{s.Width}x{s.Height}", "-framerate", s.Fps.ToString(), "-i", device,
            .. transcode ? ["-c:v", "mjpeg", "-q:v", "7", "-r", s.Fps.ToString()] : new[] { "-c", "copy" },
            "-f", "mjpeg", "pipe:1",
        ];
        foreach (var a in args) info.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg didn't start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("ffmpeg isn't installed, so the webcam can't be shown: nova setup (or: sudo apt install ffmpeg, sudo pacman -S ffmpeg)");
        }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var kill = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            var splitter = new MjpegSplitter();
            var buffer = new byte[64 * 1024];
            var stream = process.StandardOutput.BaseStream;
            var any = false;
            int read;
            while ((read = await stream.ReadAsync(buffer, CancellationToken.None)) > 0)
                foreach (var jpeg in splitter.Add(buffer.AsSpan(0, read)))
                {
                    any = true;
                    frame(new WebcamFrame(jpeg, "image/jpeg", DateTime.UtcNow));
                }
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) return true;
            var message = (await errors).Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
            if (!any && !transcode && message is not null && !message.Contains("busy", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return false;
            if (message?.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException($"NOVA may not use {device}: re-run deploy/install.sh (it adds NOVA's user to the video group)");
            if (message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException($"{device} is busy (another program is using the webcam)");
            throw new InvalidOperationException(message ?? $"The webcam {device} stopped");
        }
    }
}

/// <summary>The simulator's webcam: a dark test card with a light sweeping across, as PNG frames; follows the pretend brightness.</summary>
public sealed class TestCardWebcamSource(Func<int>? brightness = null) : IWebcamSource
{
    public async Task RunAsync(string device, WebcamSettings s, Action<WebcamFrame> frame, Action<string> note, CancellationToken ct)
    {
        int w = Math.Min(s.Width, 640), h = Math.Min(s.Height, 480);
        var pixels = new byte[w * h];
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            // A dim gradient (the "sky"), a grey block (the "mount") and a light that sweeps across once every 8 s.
            var sweep = (int)(n++ * w / (8.0 * s.Fps)) % w;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = 12 + 20 * y / h;
                if (x > w * 0.4 && x < w * 0.6 && y > h * 0.35) v = 70;
                if (Math.Abs(x - sweep) < 6 && y > h * 0.1 && y < h * 0.3) v = 230;
                pixels[y * w + x] = (byte)Math.Clamp(v + 2 * (brightness?.Invoke() ?? 0), 0, 255);
            }
            frame(new WebcamFrame(Png.EncodeGray8(w, h, pixels), "image/png", DateTime.UtcNow));
            await Task.Delay(TimeSpan.FromSeconds(1.0 / s.Fps), ct);
        }
    }
}
