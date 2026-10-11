using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <param name="StoredTiles">Tiles on disk (all orders).</param>
/// <param name="StoredBytes">Their total size.</param>
/// <param name="CompleteOrder">The deepest order stored for the whole sky (with every order above it), or -1.</param>
/// <param name="Download">The download in progress or last run, if any.</param>
public sealed record SkyTilesStatus(int StoredTiles, long StoredBytes, int CompleteOrder, SkyTilesDownload? Download, string Folder);

/// <param name="State">Running, Done, Cancelled or Failed.</param>
public sealed record SkyTilesDownload(
    string State, int MaxOrder, int Total, int Done, int Failed, long Bytes, DateTime StartedUtc, DateTime? FinishedUtc, string? Message);

/// <summary>
/// The sky survey (DSS colour, a HiPS: the whole sky as 512-pixel JPEG tiles at levels of detail 0–9) kept on the
/// mini PC, so the sky chart draws imagery instantly and offline. Tiles come from CDS (alasky) the first time
/// they're needed and stay; "Download" fetches whole levels in advance for use in the field. The folder mirrors
/// the survey's own layout (NorderK/DirD/NpixN.jpg), so it's a standard HiPS copy.
/// </summary>
public sealed class SkyTileService
{
    public const string Survey = "DSSColor";
    public const int MaxOrder = 9;
    private const string Source = "https://alasky.cds.unistra.fr/DSS/DSSColor/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _folder;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    private DateTime _offlineUntil = DateTime.MinValue;
    private CancellationTokenSource? _download;
    private SkyTilesDownload? _progress;
    private (int Tiles, long Bytes)? _stored;

    public SkyTileService(IOptions<NovaOptions> options, ActivityLog log)
    {
        _folder = Path.Combine(options.Value.ResolveConfigDirectory(), "sky", Survey);
        _log = log;
    }

    public static int TilesIn(int order) => 12 << (2 * order);

    public static int TilesUpTo(int maxOrder) => Enumerable.Range(0, maxOrder + 1).Sum(TilesIn);

    private static string RelativePath(int order, long npix) => $"Norder{order}/Dir{npix / 10000 * 10000}/Npix{npix}.jpg";

    /// <summary>A tile's JPEG: from disk, else fetched (and kept) when there's internet; null if neither.</summary>
    public async Task<byte[]?> GetTileAsync(int order, long npix, CancellationToken ct)
    {
        if (order is < 0 or > MaxOrder || npix < 0 || npix >= TilesIn(order)) return null;
        return await GetAsync(RelativePath(order, npix), ct);
    }

    /// <summary>All of level 3 in one image (64 px per tile, 27 to a row): an instant whole-sky backdrop.</summary>
    public Task<byte[]?> GetAllskyAsync(CancellationToken ct) => GetAsync("Norder3/Allsky.jpg", ct);

    private async Task<byte[]?> GetAsync(string relative, CancellationToken ct)
    {
        var file = Path.Combine(_folder, relative);
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file, ct);
        if (DateTime.UtcNow < _offlineUntil) return null;
        try
        {
            var bytes = await FetchAsync(relative, file, ct);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No internet (normal in the field): don't keep trying for every tile.
            _offlineUntil = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
    }

    private async Task<byte[]?> FetchAsync(string relative, string file, CancellationToken ct)
    {
        using var response = await Http.GetAsync(Source + relative, ct);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".part";
        await File.WriteAllBytesAsync(temporary, bytes, ct);
        File.Move(temporary, file, overwrite: true);
        lock (_sync)
            if (_stored is { } s) _stored = (s.Tiles + 1, s.Bytes + bytes.Length);
        return bytes;
    }

    public SkyTilesStatus Status
    {
        get
        {
            var (tiles, bytes) = Stored();
            lock (_sync) return new SkyTilesStatus(tiles, bytes, CompleteOrder(), _progress, _folder);
        }
    }

    private (int Tiles, long Bytes) Stored()
    {
        lock (_sync)
            if (_stored is { } known) return known;
        var tiles = 0;
        long bytes = 0;
        if (Directory.Exists(_folder))
            foreach (var file in Directory.EnumerateFiles(_folder, "Npix*.jpg", SearchOption.AllDirectories))
            {
                tiles++;
                bytes += new FileInfo(file).Length;
            }
        lock (_sync) _stored = (tiles, bytes);
        return (tiles, bytes);
    }

    private int CompleteOrder()
    {
        var complete = -1;
        for (var order = 0; order <= MaxOrder; order++)
        {
            var folder = Path.Combine(_folder, $"Norder{order}");
            if (!Directory.Exists(folder)) break;
            if (Directory.EnumerateFiles(folder, "Npix*.jpg", SearchOption.AllDirectories).Count() < TilesIn(order)) break;
            complete = order;
        }
        return complete;
    }

    /// <summary>Downloads every tile of levels 0 to <paramref name="maxOrder"/> that isn't stored yet, in the background.</summary>
    public bool StartDownload(int maxOrder, out string? error)
    {
        if (maxOrder is < 0 or > 7) { error = "Choose a level from 0 to 7"; return false; }
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_download is not null) { error = "Already downloading"; return false; }
            _download = cts = new CancellationTokenSource();
            _progress = new SkyTilesDownload("Running", maxOrder, TilesUpTo(maxOrder), 0, 0, 0, DateTime.UtcNow, null, "Starting");
        }
        _log.Info($"Downloading the sky survey to level {maxOrder} ({TilesUpTo(maxOrder):N0} tiles) into {_folder}");
        _ = Task.Run(() => DownloadAsync(maxOrder, cts));
        error = null;
        return true;
    }

    public void CancelDownload()
    {
        lock (_sync) _download?.Cancel();
    }

    private async Task DownloadAsync(int maxOrder, CancellationTokenSource cts)
    {
        var done = 0;
        var failed = 0;
        long bytes = 0;
        void Report(string state, string? message, bool finished = false)
        {
            lock (_sync)
                _progress = _progress! with
                {
                    State = state, Done = done, Failed = failed, Bytes = bytes, Message = message,
                    FinishedUtc = finished ? DateTime.UtcNow : null,
                };
        }

        try
        {
            await GetAllskyAsync(cts.Token);
            var work = Enumerable.Range(0, maxOrder + 1).SelectMany(order => Enumerable.Range(0, TilesIn(order)).Select(n => (order, (long)n)));
            var consecutiveFailures = 0;
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token }, async (tile, ct) =>
            {
                var relative = RelativePath(tile.Item1, tile.Item2);
                var file = Path.Combine(_folder, relative);
                if (!File.Exists(file))
                {
                    byte[]? got = null;
                    for (var attempt = 0; attempt < 3 && got is null; attempt++)
                    {
                        try
                        {
                            got = await FetchAsync(relative, file, ct);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                        }
                    }
                    if (got is null)
                    {
                        Interlocked.Increment(ref failed);
                        // Lost the internet altogether: stop rather than fail thousands of tiles.
                        if (Interlocked.Increment(ref consecutiveFailures) >= 40)
                            throw new InvalidOperationException("Lost the connection to the survey server");
                    }
                    else
                    {
                        Interlocked.Exchange(ref consecutiveFailures, 0);
                        Interlocked.Add(ref bytes, got.Length);
                    }
                }
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0) Report("Running", $"Level {tile.Item1}");
            });
            Report("Done", failed == 0 ? "Complete" : $"{failed} tiles couldn't be fetched: run it again to retry them", finished: true);
            _log.Info($"Sky survey download finished: {done:N0} tiles, {bytes / 1_048_576.0:0} MB fetched{(failed > 0 ? $", {failed} failed" : "")}");
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled", "Stopped: run it again to carry on where it left off", finished: true);
            _log.Info("Sky survey download stopped");
        }
        catch (Exception ex)
        {
            Report("Failed", ex.Message + ": run it again to carry on where it left off", finished: true);
            _log.Warn($"Sky survey download stopped: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _download = null;
                _stored = null; // recount
            }
            cts.Dispose();
        }
    }
}
