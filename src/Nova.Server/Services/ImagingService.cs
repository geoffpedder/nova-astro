using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Server.Hubs;

namespace Nova.Server.Services;

public sealed record ExposureStatus(double ElapsedSeconds, double TotalSeconds, CameraState State);

/// <summary>Star measurements for one frame. Arcsecond values need the camera's optics preset.</summary>
/// <param name="MedianHfr">Half-flux radius in image pixels — the focus measure (smaller is sharper).</param>
public sealed record StarSummary(int Count, double? MedianHfr, double? HfrArcsec, double? MedianFwhm, double? FwhmArcsec);

/// <param name="BayerPattern">From the FITS header (e.g. "RGGB"), or null for mono cameras.</param>
public sealed record ImageInfo(
    int Id, DateTime CapturedUtc, double DurationSeconds,
    int Width, int Height, int PreviewWidth, int PreviewHeight,
    ImageStatistics Statistics, string? SavedPath, string? BayerPattern, StarSummary Stars, string? OpenedFile = null);

/// <summary>One frame's HFR, for the focus trend.</summary>
public sealed record HfrPoint(int ImageId, DateTime CapturedUtc, double? Hfr, int Stars);

public sealed record StarPosition(double X, double Y, double Hfr);

/// <summary>Detected stars of the latest frame in its full-resolution pixels, for the viewer's overlay.</summary>
public sealed record StarOverlay(int ImageId, int Width, int Height, IReadOnlyList<StarPosition> Stars);

/// <param name="Mode">"mono" or "color RGGB" etc., plus " linear" when not stretched.</param>
public sealed record RenderedPreview(byte[] Png, string Mode);

/// <summary>An image kept in the history, to look back at and compare.</summary>
/// <param name="Pinned">Kept however many newer images arrive.</param>
/// <param name="Solve">Its plate solve, if it's been solved (filled in by the status report).</param>
public sealed record HistoryImage(ImageInfo Info, bool Pinned, SolveInfo? Solve = null);

/// <param name="Size">How many images are kept (3–20).</param>
/// <param name="Images">Newest first.</param>
public sealed record ImageHistoryStatus(int Size, IReadOnlyList<HistoryImage> Images);

/// <summary>Runs exposures one at a time, saves FITS and keeps the latest preview for the browser.</summary>
public sealed class ImagingService(
    EquipmentService equipment, OpticsService optics, ActivityLog log, IHubContext<NovaHub> hub, IOptions<NovaOptions> options)
{
    private const int HistoryLength = 60;

    /// <summary>Where frames are saved (the mini PC or a USB drive); without it, the image folder from the options.</summary>
    public StorageService? Storage { get; set; }
    private readonly List<HfrPoint> _hfrHistory = new();

    /// <summary>Recent frames' HFR, oldest first (what to watch while focusing).</summary>
    public IReadOnlyList<HfrPoint> HfrHistory
    {
        get { lock (_hfrHistory) return _hfrHistory.ToList(); }
    }

    public StarOverlay? LatestStars { get; private set; }

    private readonly object _sync = new();
    private CancellationTokenSource? _exposure;
    private int _nextId;
    // Frames are numbered as they finish downloading; the viewer only ever moves forward through them.
    private long _downloaded;
    private long _shown;
    private readonly SemaphoreSlim _publishing = new(1, 1);

    public ExposureStatus? Exposure { get; private set; }
    public bool IsBusy
    {
        get { lock (_sync) return _exposure is not null; }
    }
    public ImageInfo? LatestImage { get; private set; }
    public byte[]? LatestFits { get; private set; }

    // The raw frame is kept so the preview can be re-rendered in colour on request.
    private RawImage? _latestRaw;
    private readonly Dictionary<string, RenderedPreview> _previews = new();

    // ---- History: the last few images (not autofocus or polar alignment frames), kept on disk to look back at ----

    public const int MinHistory = 3, MaxHistory = 20;
    private const int ThumbnailSize = 256;
    private const int CachedOlderPreviews = 24;

    private sealed class Kept
    {
        public required ImageInfo Info { get; init; }
        public required string File { get; init; }
        public required bool OwnFile { get; init; } // a copy NOVA made (unsaved frames, opened files), deleted when dropped
        public required StarOverlay Stars { get; init; }
        public required byte[] Thumbnail { get; init; }
        public bool Pinned { get; set; }
    }

    private readonly List<Kept> _kept = new(); // newest first
    private readonly Dictionary<int, RawImage> _olderRaw = new(); // decoded older images, a couple at a time
    private readonly Dictionary<string, RenderedPreview> _olderPreviews = new();
    private readonly string _historyFolder = Path.Combine(options.Value.ResolveConfigDirectory(), "image-history");
    private readonly string _historySettings = Path.Combine(options.Value.ResolveConfigDirectory(), "image-history.json");
    private int? _historySize;
    private readonly bool _cleared = ClearFolder(Path.Combine(options.Value.ResolveConfigDirectory(), "image-history"));

    private static bool ClearFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, "*.fits")) File.Delete(file);
        }
        catch (IOException)
        {
            // best effort
        }
        return true;
    }

    public int HistorySize
    {
        get
        {
            if (_historySize is { } known) return known;
            var size = 5;
            try
            {
                if (File.Exists(_historySettings)
                    && System.Text.Json.JsonDocument.Parse(File.ReadAllText(_historySettings)).RootElement.TryGetProperty("size", out var s))
                    size = Math.Clamp(s.GetInt32(), MinHistory, MaxHistory);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // the default, then
            }
            return (_historySize = size).Value;
        }
    }

    public IReadOnlyList<HistoryImage> History
    {
        get { lock (_kept) return _kept.Select(k => new HistoryImage(k.Info, k.Pinned)).ToList(); }
    }

    public ImageHistoryStatus HistoryStatus => new(HistorySize, History);

    public void SetHistorySize(int size)
    {
        if (size is < MinHistory or > MaxHistory) throw new ArgumentException($"Keep between {MinHistory} and {MaxHistory} images");
        _historySize = size;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historySettings)!);
            File.WriteAllText(_historySettings, $$"""{"size":{{size}}}""");
        }
        catch (IOException ex)
        {
            log.Warn($"Couldn't save the image history size: {ex.Message}");
        }
        lock (_kept) TrimHistory();
    }

    /// <summary>Pins an image so newer ones don't push it out (at most one fewer than the history holds).</summary>
    public void Pin(int id, bool pinned)
    {
        lock (_kept)
        {
            var kept = _kept.FirstOrDefault(k => k.Info.Id == id) ?? throw new ArgumentException("That image is no longer in the history");
            if (pinned && !kept.Pinned && _kept.Count(k => k.Pinned) >= HistorySize - 1)
                throw new ArgumentException($"At most {HistorySize - 1} pinned images: unpin one first, or keep more images");
            kept.Pinned = pinned;
            TrimHistory();
        }
    }

    private void TrimHistory()
    {
        while (_kept.Count > HistorySize && _kept.LastOrDefault(k => !k.Pinned) is { } oldest)
        {
            _kept.Remove(oldest);
            Forget(oldest);
        }
    }

    private void Forget(Kept kept)
    {
        lock (_olderPreviews)
        {
            _olderRaw.Remove(kept.Info.Id);
            foreach (var key in _olderPreviews.Keys.Where(k => k.StartsWith($"{kept.Info.Id} ", StringComparison.Ordinal)).ToList())
                _olderPreviews.Remove(key);
        }
        if (!kept.OwnFile) return;
        try
        {
            File.Delete(kept.File);
        }
        catch (IOException)
        {
            // best effort: the folder is cleared at start-up too
        }
    }

    private Kept? KeptImage(int? id)
    {
        if (id is null) return null;
        lock (_kept) return _kept.FirstOrDefault(k => k.Info.Id == id);
    }

    /// <summary>An image by id: the latest, or one in the history; null (the latest) when not given.</summary>
    public ImageInfo? InfoOf(int? id) => id is null || id == LatestImage?.Id ? LatestImage : KeptImage(id)?.Info;

    public byte[]? FitsOf(int? id)
    {
        if (id is null || id == LatestImage?.Id) return LatestFits;
        if (KeptImage(id) is not { } kept) return null;
        try
        {
            return File.ReadAllBytes(kept.File);
        }
        catch (IOException)
        {
            return null; // moved or deleted since
        }
    }

    public StarOverlay? StarsOf(int? id) => id is null || id == LatestImage?.Id ? LatestStars : KeptImage(id)?.Stars;

    public IReadOnlyList<FitsHeaderCard>? HeaderOf(int? id) => FitsOf(id) is { } fits ? Fits.ReadHeader(fits) : null;

    public byte[]? ThumbnailOf(int id) => KeptImage(id)?.Thumbnail;

    /// <summary>The name to download an image as.</summary>
    public string FileNameOf(int? id) =>
        InfoOf(id) is { } info ? Path.GetFileName(info.SavedPath ?? info.OpenedFile) ?? $"image-{info.Id}.fits" : "latest.fits";

    private RawImage? OlderRaw(int id)
    {
        lock (_olderPreviews)
        {
            if (_olderRaw.TryGetValue(id, out var cached)) return cached;
        }
        if (FitsOf(id) is not { } fits) return null;
        var raw = Fits.Read(fits);
        lock (_olderPreviews)
        {
            // Two decoded at a time is enough to compare a pair; each is ~100 MB for a big sensor.
            foreach (var other in _olderRaw.Keys.Where(k => k != id).Skip(1).ToList()) _olderRaw.Remove(other);
            _olderRaw[id] = raw;
        }
        return raw;
    }

    public bool TryStartExposure(double durationSeconds, bool save, out string? error)
    {
        var camera = equipment.Camera;
        if (camera is null || !camera.IsConnected)
        {
            error = "No camera connected";
            return false;
        }
        if (durationSeconds is < 0 or > 3600)
        {
            error = "Duration must be between 0 and 3600 seconds";
            return false;
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_exposure is not null)
            {
                error = "An exposure is already running";
                return false;
            }
            _exposure = cts = new CancellationTokenSource();
        }

        error = null;
        _ = Task.Run(() => RunAsync(camera, durationSeconds, save, cts, stackAs: LiveStackService.Manual));
        return true;
    }

    public void Abort()
    {
        lock (_sync) _exposure?.Cancel();
    }

    /// <summary>
    /// A frame taken for measuring (autofocus): back as soon as it's downloaded; its stars are measured in the
    /// background while the caller carries on.
    /// </summary>
    public sealed record MeasuredFrame(DateTime StartedUtc, Task<StarSummary> Stars);

    /// <summary>
    /// Exposes for measuring, without waiting for the analysis: returns once the frame is downloaded, so the
    /// caller can already move the focuser and start the next exposure while <see cref="MeasuredFrame.Stars"/>
    /// is worked out. <paramref name="exposureEnded"/> is called when the sensor stops collecting light (the
    /// download is starting), which is the earliest a focuser may move. The frame still reaches the viewer,
    /// in order, once measured.
    /// </summary>
    public async Task<MeasuredFrame> ExposeForMeasurementAsync(double durationSeconds, CancellationToken ct, Action? exposureEnded = null)
    {
        var camera = equipment.Camera;
        if (camera is null || !camera.IsConnected) throw new InvalidOperationException("No camera connected");

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_exposure is not null) throw new InvalidOperationException("An exposure is already running");
            _exposure = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        log.Info($"Exposing {durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s on {camera.Name}");
        var ended = 0;
        void End()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0) exposureEnded?.Invoke();
        }
        CapturedImage captured;
        try
        {
            var progress = new InlineProgress<ExposureProgress>(p =>
            {
                Exposure = new ExposureStatus(p.ElapsedSeconds, p.TotalSeconds, p.State);
                _ = hub.Clients.All.SendAsync(NovaHub.Events.ExposureProgress, Exposure);
                if (p.State == CameraState.Downloading) End();
            });
            Exposure = new ExposureStatus(0, durationSeconds, CameraState.Exposing);
            captured = await camera.ExposeAsync(new ExposureRequest(durationSeconds), progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Exposure aborted");
            throw;
        }
        catch (Exception ex)
        {
            log.Error("Exposure failed", ex);
            throw;
        }
        finally
        {
            Exposure = null;
            lock (_sync) _exposure = null;
            cts.Dispose();
            await hub.Clients.All.SendAsync(NovaHub.Events.ExposureProgress, null);
        }
        End(); // drivers that never report "downloading": it's certainly over now

        var sequence = Interlocked.Increment(ref _downloaded);
        var analysis = Task.Run(() => StarDetector.Analyze(captured.Image), CancellationToken.None);
        var stars = analysis.ContinueWith(a => Summarise(a.Result, camera.Name, captured.Image), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await PublishAsync(captured.Image, captured.FitsBytes, captured.StartedUtc, durationSeconds,
                    camera.Name, path: null, openedFile: null, sequence, await analysis, keep: false);
                log.Info($"Image {info.Id} ready" + Describe(info.Stars));
            }
            catch (Exception ex)
            {
                log.Error("Couldn't show a measured frame", ex);
            }
        }, CancellationToken.None);
        return new MeasuredFrame(captured.StartedUtc, stars);
    }

    /// <summary>
    /// Takes one exposure and waits for it — for automation such as centring (and later, sequences).
    /// The frame becomes the latest image like any other; Abort cancels it too.
    /// </summary>
    /// <param name="target">Saved frames go into a folder of this name inside the night's folder (sequences).</param>
    /// <param name="frameNumber">The frame's number in its sequence, which ends the file name (…_0024.fits).</param>
    /// <param name="subfolder">A folder inside the target's (or the night's) folder, e.g. "Flats".</param>
    /// <param name="keep">Keep it in the image history (not for autofocus, polar alignment or flats' test frames).</param>
    /// <param name="stackAs">Offer it to the live stack, as this target's (a sequence's light frames, when asked).</param>
    public async Task<ImageInfo> CaptureAsync(double durationSeconds, bool save, CancellationToken ct, string? target = null,
        int? frameNumber = null, string? subfolder = null, bool keep = true, string? stackAs = null)
    {
        var camera = equipment.Camera;
        if (camera is null || !camera.IsConnected) throw new InvalidOperationException("No camera connected");

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_exposure is not null) throw new InvalidOperationException("An exposure is already running");
            _exposure = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        return (await RunAsync(camera, durationSeconds, save, cts, rethrow: true, target, frameNumber, subfolder, keep, stackAs))!;
    }

    /// <summary>Where frames are offered for live stacking (set by <see cref="LiveStackService"/>).</summary>
    public Action<TakenFrame>? FrameTaken { get; set; }

    private async Task<ImageInfo?> RunAsync(ICamera camera, double duration, bool save, CancellationTokenSource cts,
        bool rethrow = false, string? target = null, int? frameNumber = null, string? subfolder = null, bool keep = true,
        string? stackAs = null)
    {
        log.Info($"Exposing {duration.ToString("0.###", CultureInfo.InvariantCulture)}s on {camera.Name}");
        try
        {
            var progress = new InlineProgress<ExposureProgress>(p =>
            {
                Exposure = new ExposureStatus(p.ElapsedSeconds, p.TotalSeconds, p.State);
                _ = hub.Clients.All.SendAsync(NovaHub.Events.ExposureProgress, Exposure);
            });
            Exposure = new ExposureStatus(0, duration, CameraState.Exposing);

            // What's in the light path when the exposure starts, for the file name.
            var frameType = camera.Controls.FrameType;
            var filter = CurrentFilter();

            var captured = await camera.ExposeAsync(new ExposureRequest(duration), progress, cts.Token);
            var sequence = Interlocked.Increment(ref _downloaded);
            var path = save ? await SaveAsync(captured, camera, frameType, filter, target, frameNumber, subfolder) : null;
            var info = await PublishAsync(captured.Image, captured.FitsBytes, captured.StartedUtc, duration,
                camera.Name, path, openedFile: null, sequence, keep: keep);
            log.Info((path is null ? $"Image {info.Id} ready" : $"Image {info.Id} saved to {path}") + Describe(info.Stars));
            if (stackAs is not null)
                FrameTaken?.Invoke(new TakenFrame(captured.Image, info, stackAs, frameType, filter, duration,
                    (int)(camera.Controls.Binning?.Value ?? 1)));
            return info;
        }
        catch (OperationCanceledException)
        {
            log.Warn("Exposure aborted");
            if (rethrow) throw;
            return null;
        }
        catch (Exception ex)
        {
            log.Error("Exposure failed", ex);
            if (rethrow) throw;
            return null;
        }
        finally
        {
            Exposure = null;
            lock (_sync) _exposure = null;
            cts.Dispose();
            await hub.Clients.All.SendAsync(NovaHub.Events.ExposureProgress, null);
        }
    }

    /// <summary>
    /// Opens a FITS file from the user (e.g. an old frame to slew back to, or to check HFR): it becomes the
    /// latest image — preview, stars and plate solving all work on it — without touching the camera.
    /// </summary>
    public async Task<ImageInfo> OpenAsync(byte[] data, string fileName, CancellationToken ct)
    {
        // .fits.gz from archives: unpack first (gzip files start 1F 8B).
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            await gz.CopyToAsync(unpacked, ct);
            data = unpacked.ToArray();
        }

        RawImage image;
        try
        {
            image = await Task.Run(() => Fits.Read(data), ct);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{fileName} isn't a FITS image NOVA can read: {ex.Message}");
        }

        var duration = FitsMetadata.Number(image, "EXPTIME") ?? FitsMetadata.Number(image, "EXPOSURE") ?? 0;
        var info = await PublishAsync(image, data, FitsMetadata.Started(image) ?? DateTime.UtcNow, duration,
            camera: null, path: null, openedFile: fileName, Interlocked.Increment(ref _downloaded));
        log.Info($"Opened {fileName} as image {info.Id}" + Describe(info.Stars));
        return info;
    }

    /// <summary>Preview, star measurement and announcing a new latest image — for captures and opened files alike.</summary>
    /// <param name="sequence">Download order: frames measured in the background can finish out of order, and the
    /// viewer must never go back to an older one.</param>
    /// <param name="measured">Star analysis already done (measurement frames), else it's done here.</param>
    private async Task<ImageInfo> PublishAsync(RawImage image, byte[] fits, DateTime capturedUtc, double duration,
        string? camera, string? path, string? openedFile, long sequence, StarAnalysis? measured = null, bool keep = true)
    {
        // Preview and star measurement are independent: do them side by side.
        var previewTask = Task.Run(() => Preview.Render(image, options.Value.PreviewMaxSize));
        var starsTask = measured is null ? Task.Run(() => StarDetector.Analyze(image)) : Task.FromResult(measured);
        // For the history: a thumbnail, and a copy on disk unless it was saved already.
        var thumbnailTask = keep
            ? Task.Run(() => (Debayer.FromHeaders(image) is { } bayer ? Preview.RenderColor(image, bayer, ThumbnailSize) : Preview.Render(image, ThumbnailSize)).Png)
            : Task.FromResult<byte[]>([]);
        string? keptFile = path;
        if (keep && path is null)
        {
            try
            {
                Directory.CreateDirectory(_historyFolder);
                keptFile = Path.Combine(_historyFolder, $"{Guid.NewGuid():N}.fits");
                await File.WriteAllBytesAsync(keptFile, fits);
            }
            catch (IOException ex)
            {
                log.Warn($"Couldn't keep the image for the history: {ex.Message}");
                keptFile = null;
            }
        }
        var preview = await previewTask;
        var analysis = await starsTask;
        var summary = Summarise(analysis, camera, image);
        var thumbnail = await thumbnailTask;

        await _publishing.WaitAsync();
        try
        {
            return Publish(image, fits, capturedUtc, duration, path, openedFile, sequence, preview, analysis, summary,
                keptFile is null ? null : (keptFile, path is null, thumbnail));
        }
        finally
        {
            _publishing.Release();
        }
    }

    private ImageInfo Publish(RawImage image, byte[] fits, DateTime capturedUtc, double duration, string? path,
        string? openedFile, long sequence, PreviewImage preview, StarAnalysis analysis, StarSummary summary,
        (string File, bool OwnFile, byte[] Thumbnail)? keep)
    {

        var info = new ImageInfo(Interlocked.Increment(ref _nextId), capturedUtc, duration,
            image.Width, image.Height, preview.Width, preview.Height, preview.Statistics, path,
            Debayer.FromHeaders(image)?.ToString(), summary, openedFile);
        if (sequence < _shown) // a newer frame is already on screen
        {
            if (keep is { OwnFile: true } stale) TryDelete(stale.File);
            return info;
        }
        _shown = sequence;
        LatestStars = new StarOverlay(info.Id, image.Width, image.Height,
            analysis.Stars.Select(s => new StarPosition(Math.Round(s.X, 1), Math.Round(s.Y, 1), Math.Round(s.Hfr, 2))).ToList());
        if (keep is { } k)
        {
            lock (_kept)
            {
                _kept.Insert(0, new Kept { Info = info, File = k.File, OwnFile = k.OwnFile, Stars = LatestStars, Thumbnail = k.Thumbnail });
                TrimHistory();
            }
        }
        if (openedFile is null) // the focus trend is for live frames only
        {
            lock (_hfrHistory)
            {
                _hfrHistory.Add(new HfrPoint(info.Id, info.CapturedUtc, summary.MedianHfr, summary.Count));
                if (_hfrHistory.Count > HistoryLength) _hfrHistory.RemoveAt(0);
            }
        }
        lock (_previews)
        {
            _previews.Clear();
            _previews[$"mono {options.Value.PreviewMaxSize}"] = new RenderedPreview(preview.Png, "mono");
            _latestRaw = image;
        }
        LatestFits = fits;
        LatestImage = info;

        _ = hub.Clients.All.SendAsync(NovaHub.Events.ImageReady, info);
        return info;
    }

    private static string Describe(StarSummary s) => s.MedianHfr is { } hfr
        ? $", {s.Count} stars, HFR {hfr:0.00} px{(s.HfrArcsec is { } a ? $" ({a:0.00}″)" : "")}"
        : ", no stars measured";

    /// <summary>
    /// The latest frame's preview. <paramref name="mode"/> is "mono", "auto" (debayer if the FITS header
    /// names a Bayer pattern) or a pattern such as "RGGB" to force one. Colour renders are cached per frame.
    /// </summary>
    /// <summary>The latest frame's FITS header cards, in file order.</summary>
    public IReadOnlyList<FitsHeaderCard>? LatestHeader => LatestFits is { } fits ? Fits.ReadHeader(fits) : null;

    /// <param name="maxSize">Longest side of the preview in pixels; null for the configured default, 0 for full resolution.</param>
    /// <param name="id">An image in the history; null (or one that's gone) for the latest.</param>
    public async Task<RenderedPreview?> GetPreviewAsync(string? mode, bool stretch = true, int? maxSize = null, int? id = null)
    {
        RawImage? raw;
        lock (_previews) raw = _latestRaw;
        var older = id is { } wanted && wanted != LatestImage?.Id && KeptImage(wanted) is not null ? wanted : (int?)null;
        if (older is { } o) raw = await Task.Run(() => OlderRaw(o)) ?? raw;
        if (raw is null) return null;

        BayerPattern? pattern = mode?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mono" => null,
            "auto" => Debayer.FromHeaders(raw),
            var explicitPattern => Enum.TryParse<BayerPattern>(explicitPattern, true, out var p)
                ? p
                : throw new ArgumentException($"Unknown preview mode '{mode}'. Use mono, auto, RGGB, BGGR, GRBG or GBRG."),
        };
        // 0 means full resolution: big enough that nothing is downsampled.
        var size = maxSize switch
        {
            null => options.Value.PreviewMaxSize,
            0 => Math.Max(raw.Width, raw.Height),
            var s => Math.Clamp(s.Value, 256, 16384),
        };
        var label = (pattern is { } bp ? $"color {bp}" : "mono") + (stretch ? "" : " linear");
        var key = $"{label} {size}"; // cached per rendering and size

        if (older is { } olderId)
        {
            var olderKey = $"{olderId} {key}";
            lock (_olderPreviews)
            {
                if (_olderPreviews.TryGetValue(olderKey, out var hit)) return hit;
            }
            var made = await Task.Run(() => pattern is { } p2 ? Preview.RenderColor(raw, p2, size, stretch) : Preview.Render(raw, size, stretch));
            var olderResult = new RenderedPreview(made.Png, label);
            lock (_olderPreviews)
            {
                if (_olderPreviews.Count >= CachedOlderPreviews) _olderPreviews.Remove(_olderPreviews.Keys.First());
                _olderPreviews[olderKey] = olderResult;
            }
            return olderResult;
        }

        lock (_previews)
        {
            if (_previews.TryGetValue(key, out var cached)) return cached;
        }

        var rendered = await Task.Run(() => pattern is { } bayer
            ? Preview.RenderColor(raw, bayer, size, stretch)
            : Preview.Render(raw, size, stretch));
        var result = new RenderedPreview(rendered.Png, label);
        lock (_previews)
        {
            // Only cache if a newer frame hasn't replaced this one meanwhile.
            if (ReferenceEquals(_latestRaw, raw)) _previews[key] = result;
        }
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private async Task<string> SaveAsync(CapturedImage captured, ICamera camera, FrameType? frameType, string? filter,
        string? target, int? frameNumber, string? subfolder = null)
    {
        // Group by observing night: frames after midnight still belong to the evening's folder.
        var local = captured.StartedUtc.ToLocalTime();
        var night = local.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var relative = night;
        if (FolderName(target) is { } targetFolder) relative = Path.Combine(relative, targetFolder);
        if (FolderName(subfolder) is { } inner) relative = Path.Combine(relative, inner);

        // Prefer what the driver recorded in the frame itself; fall back to what NOVA knows.
        var headers = captured.Image.Headers;
        var temperature = headers.TryGetValue("CCD-TEMP", out var t)
            && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var headerTemp)
                ? headerTemp
                : camera.Controls.Cooler?.Temperature;
        var description = new FrameDescription(
            local,
            captured.DurationSeconds,
            frameType ?? FileNaming.FrameTypeFromHeader(headers.GetValueOrDefault("IMAGETYP")),
            filter ?? headers.GetValueOrDefault("FILTER"),
            temperature,
            frameNumber);

        var name = FileNaming.FileName(description);
        // The mini PC's image folder, or a USB drive (falling back to the mini PC if it can't be written).
        if (Storage is { } storage)
            return await storage.WriteAsync(root => FileNaming.UniquePath(Path.Combine(root, relative), name), captured.FitsBytes);
        var folder = Path.Combine(options.Value.ResolveImageDirectory(), relative);
        Directory.CreateDirectory(folder);
        var path = FileNaming.UniquePath(folder, name);
        await File.WriteAllBytesAsync(path, captured.FitsBytes);
        return path;
    }

    /// <summary>
    /// Saves a file NOVA made (a live stack, say) beside the night's frames: in the night's folder, the target's
    /// within it, and a folder inside that; on the USB drive when frames go there.
    /// </summary>
    public async Task<string> SaveExtraAsync(DateTime utc, string? target, string? subfolder, string fileName, byte[] data)
    {
        var night = utc.ToLocalTime().AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var relative = night;
        if (FolderName(target) is { } targetFolder) relative = Path.Combine(relative, targetFolder);
        if (FolderName(subfolder) is { } inner) relative = Path.Combine(relative, inner);
        if (Storage is { } storage)
            return await storage.WriteAsync(root => FileNaming.UniquePath(Path.Combine(root, relative), fileName), data);
        var folder = Path.Combine(options.Value.ResolveImageDirectory(), relative);
        Directory.CreateDirectory(folder);
        var path = FileNaming.UniquePath(folder, fileName);
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    /// <summary>A target name made safe as a folder name ("M 31 / Andromeda" → "M 31 - Andromeda"); null if empty.</summary>
    public static string? FolderName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string(target.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.', '-');
        return name.Length == 0 ? null : name.Length > 80 ? name[..80] : name;
    }

    /// <summary>Rounds the measurements and adds arcseconds when the camera has an optics preset.</summary>
    private StarSummary Summarise(StarAnalysis analysis, string? camera, RawImage image)
    {
        // Our camera: its optics preset per unbinned pixel × this frame's binning. Otherwise (opened files,
        // or no preset): whatever the header says.
        var preset = camera is null ? null : optics.All.FirstOrDefault(o => o.Camera == camera)?.ImageScale;
        var binning = FitsMetadata.Number(image, "XBINNING") is > 0 and var b ? b : 1;
        var scale = preset is { } p ? p * binning : FitsMetadata.ImageScale(image);
        double? Arcsec(double? px) => px is { } v && scale is { } s ? Math.Round(v * s, 2) : null;
        double? Round(double? v) => v is { } d ? Math.Round(d, 2) : null;
        return new StarSummary(analysis.Stars.Count, Round(analysis.MedianHfr), Arcsec(analysis.MedianHfr),
            Round(analysis.MedianFwhm), Arcsec(analysis.MedianFwhm));
    }

    /// <summary>The filter the chosen filter wheel is on, if there is one and it's connected.</summary>
    private string? CurrentFilter() =>
        equipment.FilterWheel is { IsConnected: true, Position: > 0 } wheel
            ? wheel.Names.ElementAtOrDefault(wheel.Position - 1)
            : null;

    /// <summary>Unlike Progress&lt;T&gt;, reports synchronously and in order.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
