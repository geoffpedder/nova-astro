using System.Globalization;
using System.Threading.Channels;
using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Core.Stacking;

namespace Nova.Server.Services;

/// <summary>A frame just taken, offered for stacking: "manual" (Expose, Loop) or a sequence target's name.</summary>
public sealed record TakenFrame(RawImage Image, ImageInfo Info, string StackAs, FrameType? Type, string? Filter, double ExposureSeconds, int Binning);

/// <param name="Label">What it's a stack of, e.g. "M 31, L 120 s" or "Live stack, 30 s".</param>
/// <param name="LastRejected">Why the last frame left out was, e.g. "only 4 stars lined up (clouds?)".</param>
/// <param name="Version">Goes up with every frame added: the preview's cache key.</param>
public sealed record LiveStackInfo(
    string Key, string Label, string? Target, string? Filter, double ExposureSeconds, int Frames, int Rejected,
    double IntegrationSeconds, string? LastRejected, DateTime UpdatedUtc, int Version, bool Color, int Width, int Height,
    string? SavedPath);

/// <param name="Enabled">Stacking Expose and Loop frames (the Image tab's Live stack switch).</param>
/// <param name="Current">The stack last added to (what the Image tab shows).</param>
public sealed record LiveStackStatus(bool Enabled, bool Busy, LiveStackInfo? Current, IReadOnlyList<LiveStackInfo> Stacks);

/// <summary>
/// Live stacking, optional: frames are aligned to the first of their stack (by their stars) and averaged, so the
/// picture builds up as the night goes on. Only light frames: from Expose and Loop while the Image tab's switch is on,
/// and from a sequence's targets that have Live stack ticked. Frames NOVA takes for itself (autofocus, centring,
/// polar alignment, flats) never are. It works in the background, one frame at a time, so the next exposure is
/// never held up; the frames themselves are saved as always. A stack per target, filter and exposure; a sequence
/// target's are saved (FITS and PNG, in its Stacked folder) when the target ends.
/// </summary>
public sealed class LiveStackService
{
    /// <summary>Stars a frame needs, to be aligned (or to start a stack).</summary>
    public const int MinStars = 8;

    /// <summary>A frame whose stars are this much fatter than the stack's usual is left out (wind, focus).</summary>
    public const double HfrLimit = 1.6;

    private sealed class Entry(string key, LiveStackInfo info)
    {
        public readonly object Sync = new();
        public string Key { get; } = key;
        public LiveStackInfo Info { get; set; } = info;
        public LiveStack? Stack { get; set; }
        public List<StarPoint> ReferenceStars { get; set; } = [];
        public List<double> Hfrs { get; } = [];
        public (int Version, int Size, PreviewImage Image)? Preview { get; set; }
    }

    private readonly ImagingService _imaging;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _stacks = new();
    private readonly Channel<TakenFrame> _queue = Channel.CreateUnbounded<TakenFrame>(new UnboundedChannelOptions { SingleReader = true });
    private string? _current;
    private int _busy; // frames queued or being stacked

    public LiveStackService(ImagingService imaging, ActivityLog log)
    {
        _imaging = imaging;
        _log = log;
        imaging.FrameTaken = Offer;
        _ = Task.Run(WorkAsync);
    }

    /// <summary>Whether Expose and Loop frames are stacked (the Image tab's switch).</summary>
    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        _log.Info(enabled ? "Live stacking on: Expose and Loop frames are stacked" : "Live stacking off (the stack is kept until Reset)");
    }

    public LiveStackStatus Status
    {
        get
        {
            lock (_sync)
            {
                var all = _stacks.Values.Select(e => e.Info).OrderBy(i => i.UpdatedUtc).ToList();
                var current = _current is { } key && _stacks.TryGetValue(key, out var e2) ? e2.Info : all.LastOrDefault();
                return new LiveStackStatus(Enabled, _busy > 0, current, all);
            }
        }
    }

    /// <summary>A frame from ImagingService: queued if it's to be stacked, otherwise nothing.</summary>
    public void Offer(TakenFrame frame)
    {
        if (frame.Type is { } type && type != FrameType.Light) return;
        if (frame.StackAs == Manual && !Enabled) return;
        Interlocked.Increment(ref _busy);
        if (!_queue.Writer.TryWrite(frame)) Interlocked.Decrement(ref _busy);
    }

    /// <summary>What Expose and Loop frames are stacked as.</summary>
    public const string Manual = "manual";

    private async Task WorkAsync()
    {
        await foreach (var frame in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Add(frame);
            }
            catch (Exception ex)
            {
                _log.Warn($"Live stack: couldn't add image {frame.Info.Id} ({ex.Message})");
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
    }

    /// <summary>Adds a frame to its stack (synchronously; the service does it in the background).</summary>
    public void Add(TakenFrame frame)
    {
        var key = KeyOf(frame);
        Entry entry;
        lock (_sync)
        {
            if (frame.StackAs == Manual)
                foreach (var old in _stacks.Keys.Where(k => k.StartsWith(Manual + "|") && k != key).ToList())
                {
                    _stacks.Remove(old);
                    _log.Info("Live stack started again: the exposure, filter or binning changed");
                }
            if (!_stacks.TryGetValue(key, out entry!))
            {
                var label = (frame.StackAs == Manual ? "Live stack" : frame.StackAs)
                            + $", {(frame.Filter is { Length: > 0 } f ? f + " " : "")}{Seconds(frame.ExposureSeconds)}";
                entry = new Entry(key, new LiveStackInfo(key, label, frame.StackAs == Manual ? null : frame.StackAs, frame.Filter,
                    frame.ExposureSeconds, 0, 0, 0, null, DateTime.UtcNow, 0, false, 0, 0, null));
                _stacks[key] = entry;
            }
            _current = key;
        }

        var prepared = LiveStack.Prepare(frame.Image);
        var analysis = StarDetector.Analyze(frame.Image);
        var stars = analysis.Stars.Select(s => LiveStack.ToStackPixels(s.X, s.Y, s.Flux)).ToList();
        lock (entry.Sync)
        {
            string? reject = null;
            Similarity transform = Similarity.Identity;
            if (stars.Count < MinStars)
                reject = $"only {stars.Count} stars found (clouds, or out of focus?)";
            else if (entry.Stack is null)
            {
                entry.Stack = new LiveStack(prepared.Width, prepared.Height, prepared.Planes.Length);
                entry.ReferenceStars = stars;
            }
            else
            {
                var match = StarMatcher.Match(entry.ReferenceStars, stars);
                if (match.Transform is not { } fit || match.Matched < MinStars)
                    reject = $"only {match.Matched} stars lined up with the stack (clouds?)";
                else if (analysis.MedianHfr is { } hfr && entry.Hfrs.Count >= 3 && hfr > HfrLimit * Median(entry.Hfrs))
                    reject = $"stars blurred, HFR {hfr:0.0} against the stack's {Median(entry.Hfrs):0.0} (wind, or focus?)";
                else transform = fit;
            }

            var info = entry.Info;
            if (reject is not null)
            {
                entry.Info = info with { Rejected = info.Rejected + 1, LastRejected = reject, UpdatedUtc = DateTime.UtcNow };
                _log.Info($"Live stack ({info.Label}): image {frame.Info.Id} left out: {reject}");
                return;
            }
            entry.Stack!.Add(prepared, transform);
            if (analysis.MedianHfr is { } h) entry.Hfrs.Add(h);
            entry.Info = info with
            {
                Frames = entry.Stack.Frames, IntegrationSeconds = info.IntegrationSeconds + frame.ExposureSeconds,
                UpdatedUtc = DateTime.UtcNow, Version = info.Version + 1, Color = prepared.Color,
                Width = prepared.Width, Height = prepared.Height,
            };
        }
    }

    private static string KeyOf(TakenFrame f) =>
        string.Join('|', f.StackAs, f.Filter ?? "", f.ExposureSeconds.ToString(CultureInfo.InvariantCulture), f.Binning);

    /// <summary>The stack shown as a stretched PNG (null if there's none yet).</summary>
    public PreviewImage? Preview(string? key, int maxSize)
    {
        var entry = Find(key);
        if (entry is null) return null;
        lock (entry.Sync)
        {
            if (entry.Stack is not { Frames: > 0 } stack) return null;
            if (entry.Preview is { } cached && cached.Version == entry.Info.Version && cached.Size == maxSize) return cached.Image;
            var image = Nova.Core.Imaging.Preview.RenderPlanes(stack.Result(), stack.Width, stack.Height, maxSize);
            entry.Preview = (entry.Info.Version, maxSize, image);
            return image;
        }
    }

    /// <summary>Forgets a stack (the current one if no key).</summary>
    public void Reset(string? key)
    {
        lock (_sync)
        {
            var entry = Find(key);
            if (entry is null) return;
            _stacks.Remove(entry.Key);
            if (_current == entry.Key) _current = null;
            _log.Info($"Live stack cleared ({entry.Info.Label})");
        }
    }

    /// <summary>Saves a stack (the current one if no key) as a 32-bit FITS and a PNG; returns the FITS's path.</summary>
    public async Task<string?> SaveAsync(string? key)
    {
        var entry = Find(key);
        if (entry is null) return null;
        float[][] planes;
        LiveStackInfo info;
        int width, height;
        lock (entry.Sync)
        {
            if (entry.Stack is not { Frames: > 0 } stack) return null;
            (planes, info, width, height) = (stack.Result(), entry.Info, stack.Width, stack.Height);
        }
        var cards = new List<FitsCard>
        {
            new("NCOMBINE", info.Frames, "frames stacked"),
            new("EXPTIME", info.ExposureSeconds, "seconds, each frame"),
            new("TOTALEXP", info.IntegrationSeconds, "seconds, all frames"),
            new("DATE-OBS", info.UpdatedUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            new("CREATOR", "NOVA live stack"),
        };
        if (info.Target is { } target) cards.Add(new FitsCard("OBJECT", target));
        if (info.Filter is { Length: > 0 } filter) cards.Add(new FitsCard("FILTER", filter));
        var name = $"Stack_{(info.Filter is { Length: > 0 } f ? f + "_" : "")}{Seconds(info.ExposureSeconds).Replace(" ", "")}_{info.Frames}x_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var fits = Fits.WriteFloat32(width, height, planes, cards);
        var png = Nova.Core.Imaging.Preview.RenderPlanes(planes, width, height, int.MaxValue).Png;
        var path = await _imaging.SaveExtraAsync(DateTime.UtcNow, info.Target, "Stacked", name + ".fits", fits);
        await _imaging.SaveExtraAsync(DateTime.UtcNow, info.Target, "Stacked", name + ".png", png);
        lock (entry.Sync) entry.Info = entry.Info with { SavedPath = path };
        _log.Info($"Live stack saved: {path} ({info.Frames} frames, {Seconds(info.IntegrationSeconds)})");
        return path;
    }

    /// <summary>A sequence target has ended: save its stacks, then let them go.</summary>
    public async Task FinishTargetAsync(string target)
    {
        // Its last frames may still be on their way into the stack.
        using (var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            try { await WaitForQueueAsync(limit.Token); } catch (OperationCanceledException) { }
        List<string> keys;
        lock (_sync) keys = _stacks.Values.Where(e => e.Info.Target == target).Select(e => e.Key).ToList();
        foreach (var key in keys)
        {
            try
            {
                await SaveAsync(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log.Warn($"Live stack for {target} couldn't be saved: {ex.Message}");
            }
            lock (_sync) _stacks.Remove(key);
        }
    }

    /// <summary>Waits until every queued frame has been stacked (for tests and saving at a target's end).</summary>
    public async Task WaitForQueueAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _busy) > 0 && !ct.IsCancellationRequested) await Task.Delay(50, ct);
    }

    private Entry? Find(string? key)
    {
        lock (_sync)
        {
            key ??= _current ?? _stacks.Values.MaxBy(e => e.Info.UpdatedUtc)?.Key;
            return key is not null && _stacks.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Seconds(double s) =>
        s >= 3600 ? $"{s / 3600:0.#} h" : s >= 60 && s % 60 != 0 ? $"{s / 60:0.#} min" : s >= 60 ? $"{s / 60:0} min" : $"{s:0.##} s";
}
