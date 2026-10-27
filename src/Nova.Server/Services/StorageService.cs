using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <param name="FreeBytes">Free space, while it's mounted.</param>
/// <param name="Selected">The drive frames go to when saving to USB.</param>
public sealed record DriveView(
    string Device, string Name, string? Model, long SizeBytes, long? FreeBytes, bool Mounted, bool Selected, string? FileSystem);

/// <param name="Night">The night's folder name (yyyy-MM-dd).</param>
public sealed record NightFolder(string Night, int Files, long Bytes);

public sealed record CopyStatus(
    string Night, bool Running, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? Error, int Skipped);

/// <summary>A folder or FITS file in the image folders (Open… → From the mini PC).</summary>
/// <param name="Kind">For a file: "fits", "image" (PNG, JPEG, TIFF) or "other"; null for a folder.</param>
public sealed record FileEntry(string Name, bool Folder, long? Bytes, DateTime? ModifiedUtc, string? Kind = null);

/// <param name="Id">"local" (the mini PC's image folder) or "usb" (the mounted USB drive).</param>
public sealed record BrowseRoot(string Id, string Label);

/// <param name="Path">Relative to the root ("" at its top), with '/' between folders.</param>
public sealed record FolderListing(string Root, string Path, IReadOnlyList<FileEntry> Entries, IReadOnlyList<BrowseRoot> Roots);

/// <param name="Target">"local" (the mini PC's disk) or "usb".</param>
/// <param name="SavingTo">The folder new frames go to now (on the drive, or the mini PC's when falling back).</param>
/// <param name="OnUsb">Whether new frames go to the USB drive right now.</param>
/// <param name="Warning">Why frames aren't going to the drive although it's chosen, or a drive problem.</param>
/// <param name="Available">Whether this system can use USB drives at all.</param>
public sealed record StorageStatus(
    string Target, bool Available, IReadOnlyList<DriveView> Drives, string SavingTo, bool OnUsb, string LocalFolder,
    string? Warning, CopyStatus? Copy, IReadOnlyList<NightFolder> Nights);

/// <summary>
/// Where FITS files go: the mini PC's own disk, or a USB drive (as an ASIAIR saves to its card or a USB stick). A
/// chosen drive is mounted as soon as it's plugged in. If it's missing, full, unwritable or pulled out mid-night,
/// frames go to the mini PC instead (each night keeps the same folder layout either way) and the log says so.
/// Eject finishes any write and unmounts it, so it can be unplugged safely. A night saved on the mini PC can be
/// copied to the drive afterwards.
/// </summary>
public sealed class StorageService : BackgroundService
{
    /// <summary>Below this much free space the drive counts as full: room for a few more frames at least.</summary>
    public const long MinimumFree = 300L * 1024 * 1024;

    private readonly IDriveTool _tool;
    private readonly ActivityLog _log;
    private readonly string _localRoot;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private Settings _settings;
    private IReadOnlyList<RemovableDrive> _drives = [];
    private readonly HashSet<string> _ejected = new(StringComparer.Ordinal);     // devices ejected: not remounted until replugged
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal); // device → why it can't be used, until replugged
    private string? _lastWarned;
    private CopyStatus? _copy;
    private CancellationTokenSource? _copyCts;
    private IReadOnlyList<NightFolder> _nights = [];
    private DateTime _nightsRead;

    private sealed record Settings(string Target = "local", string? DriveUuid = null);

    public StorageService(IOptions<NovaOptions> options, ActivityLog log, IDriveTool? tool = null)
    {
        var o = options.Value;
        _log = log;
        _localRoot = o.ResolveImageDirectory();
        _file = Path.Combine(o.ResolveConfigDirectory(), "storage.json");
        _tool = tool ?? (!string.IsNullOrWhiteSpace(o.SimulatedUsbDrive) ? new FolderDriveTool(o.SimulatedUsbDrive)
            : UdisksDriveTool.Available ? new UdisksDriveTool() : new NoDriveTool());
        _settings = Load();
    }

    /// <summary>Called when frames had to go to the mini PC although the drive is chosen (for a phone notification).</summary>
    public event EventHandler<string>? FellBack;

    public string Target => _settings.Target;

    public StorageStatus Status
    {
        get
        {
            IReadOnlyList<RemovableDrive> drives;
            lock (_sync) drives = _drives;
            var selected = Selected(drives);
            var views = drives.Select(d => new DriveView(d.Device, d.Name, d.Model, d.SizeBytes, Free(d.MountPoint), d.MountPoint is not null,
                d.Device == selected?.Device, d.FileSystem)).ToList();
            var root = Root(out var onUsb, out var why);
            lock (_sync)
                return new StorageStatus(_settings.Target, _tool is not NoDriveTool, views, root, onUsb, _localRoot,
                    _settings.Target == "usb" ? why : null, _copy, _nights);
        }
    }

    /// <summary>Where frames go right now: the chosen drive's Nova folder, or the mini PC's image folder.</summary>
    public string ImageRoot()
    {
        var root = Root(out _, out var why);
        if (_settings.Target == "usb") Warn(why);
        return root;
    }

    private string Root(out bool onUsb, out string? why)
    {
        onUsb = false;
        why = null;
        if (_settings.Target != "usb") return _localRoot;
        RemovableDrive? drive;
        bool ejected;
        string? failure;
        lock (_sync)
        {
            drive = Selected(_drives);
            ejected = drive is not null && _ejected.Contains(drive.Device);
            failure = drive is not null ? _failed.GetValueOrDefault(drive.Device) : null;
        }
        if (drive is null) why = "No USB drive is plugged in: frames are saved on the mini PC";
        else if (ejected) why = $"{drive.Name} was ejected: frames are saved on the mini PC until it's plugged in again";
        else if (failure is not null) why = $"{drive.Name}: {failure}. Frames are saved on the mini PC";
        else if (drive.MountPoint is null) why = $"{drive.Name} isn't mounted yet: frames are saved on the mini PC";
        else if (Free(drive.MountPoint) is { } free && free < MinimumFree) why = $"{drive.Name} is full: frames are saved on the mini PC";
        else
        {
            onUsb = true;
            return Path.Combine(drive.MountPoint, "Nova");
        }
        return _localRoot;
    }

    private void Warn(string? why)
    {
        bool tell;
        lock (_sync)
        {
            tell = why is not null && why != _lastWarned;
            _lastWarned = why;
        }
        if (!tell) return;
        _log.Warn(why!);
        FellBack?.Invoke(this, why!);
    }

    /// <summary>
    /// Saves a file under the image root (a path relative to it), falling back to the mini PC's disk if writing to
    /// the drive fails (pulled out, read-only, full). Returns the full path written.
    /// </summary>
    public async Task<string> WriteAsync(Func<string, string> pathUnderRoot, byte[] bytes)
    {
        await _writes.WaitAsync();
        try
        {
            var root = ImageRoot();
            var path = pathUnderRoot(root);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (root == _localRoot) await File.WriteAllBytesAsync(path, bytes);
                else await WriteDurablyAsync(path, bytes, CancellationToken.None); // on the drive before it counts as saved
                return path;
            }
            catch (Exception ex) when (root != _localRoot && ex is IOException or UnauthorizedAccessException)
            {
                RemovableDrive? drive;
                lock (_sync) drive = Selected(_drives);
                if (drive is not null) lock (_sync) _failed[drive.Device] = $"couldn't write to it ({ex.Message.TrimEnd('.')})";
                try { File.Delete(path); } catch { /* half-written, or gone with the drive */ }
                Warn($"Couldn't save to {drive?.Name ?? "the USB drive"} ({ex.Message.TrimEnd('.')}): frames are saved on the mini PC");
                var local = pathUnderRoot(_localRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await File.WriteAllBytesAsync(local, bytes);
                return local;
            }
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Save to the mini PC ("local") or a USB drive ("usb", optionally which one).</summary>
    public async Task SetTargetAsync(string target, string? device, CancellationToken ct)
    {
        if (target is not ("local" or "usb")) throw new ArgumentException("Save to 'local' or 'usb'");
        RemovableDrive? drive;
        lock (_sync)
        {
            drive = device is null ? Selected(_drives) : _drives.FirstOrDefault(d => d.Device == device)
                ?? throw new ArgumentException($"No USB drive {device}");
            _settings = _settings with { Target = target, DriveUuid = drive?.Uuid ?? _settings.DriveUuid };
            if (drive is not null)
            {
                _ejected.Remove(drive.Device);
                _failed.Remove(drive.Device);
            }
            _lastWarned = null;
        }
        Save();
        _log.Info(target == "usb" ? $"Saving frames to {(drive is null ? "a USB drive (none plugged in yet)" : drive.Name)}" : "Saving frames on the mini PC");
        await RefreshAsync(ct);
    }

    /// <summary>Waits for a frame being written, unmounts the drive and powers it off: then it can be unplugged.</summary>
    public async Task EjectAsync(CancellationToken ct)
    {
        RemovableDrive drive;
        lock (_sync) drive = Selected(_drives) ?? throw new InvalidOperationException("No USB drive is plugged in");
        if (_copy is { Running: true }) throw new InvalidOperationException("A copy to the drive is still running: wait for it or cancel it");
        await _writes.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                _ejected.Add(drive.Device);
                _lastWarned = $"{drive.Name} was ejected: frames are saved on the mini PC until it's plugged in again";
            }
            try
            {
                await _tool.EjectAsync(drive, ct);
            }
            catch
            {
                lock (_sync) _ejected.Remove(drive.Device);
                throw;
            }
        }
        finally
        {
            _writes.Release();
        }
        _log.Info($"{drive.Name} ejected: it's safe to unplug" + (_settings.Target == "usb" ? " (frames are saved on the mini PC until it's back)" : ""));
        await RefreshAsync(ct);
    }

    // A USB drive's writes sit in the mini PC's memory until Linux gets round to them, so a copy can "finish" with most
    // of it not yet on the drive; unplugged then (or disconnected), the drive keeps files of the right size full of
    // nothing. Everything written to the drive is flushed to it (fsync) before it counts as done, and an earlier copy
    // only counts as there if it's the same size, the same time and has the same ends.

    /// <summary>Writes a file and waits until it's on the drive itself, not just in memory.</summary>
    private static async Task WriteDurablyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await output.WriteAsync(bytes, ct);
        output.Flush(flushToDisk: true);
    }

    /// <summary>Copies a file to the drive, waits until it's on the drive, and gives it the original's time (the mark of a finished copy).</summary>
    public static async Task CopyDurablyAsync(FileInfo file, string target, CancellationToken ct)
    {
        var partial = target + ".part";
        await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, 1 << 20, ct);
            output.Flush(flushToDisk: true);
        }
        File.Move(partial, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
    }

    /// <summary>
    /// Whether a finished copy of the file is already at the target: same size, the original's time (set only once a
    /// copy is complete; within 2 s, as FAT and exFAT keep times to the even second), and the same first and last 64 KB.
    /// </summary>
    public static async Task<bool> SameFileAsync(FileInfo file, string target, CancellationToken ct)
    {
        var copy = new FileInfo(target);
        if (!copy.Exists || copy.Length != file.Length) return false;
        if (Math.Abs((copy.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) > 2) return false;
        var block = (int)Math.Min(64 * 1024, file.Length);
        foreach (var offset in new[] { 0L, Math.Max(0, file.Length - block) })
        {
            var (a, b) = (new byte[block], new byte[block]);
            await using (var s = File.OpenRead(file.FullName)) { s.Position = offset; await s.ReadExactlyAsync(a, ct); }
            await using (var s = File.OpenRead(target)) { s.Position = offset; await s.ReadExactlyAsync(b, ct); }
            if (!a.AsSpan().SequenceEqual(b)) return false;
        }
        return true;
    }

    /// <summary>Copies a night saved on the mini PC to the drive's Nova folder, skipping files already there.</summary>
    public void StartCopy(string night)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(night, @"^\d{4}-\d{2}-\d{2}$")) throw new ArgumentException("Not a night's folder");
        var source = Path.Combine(_localRoot, night);
        if (!Directory.Exists(source)) throw new ArgumentException($"There's no {night} on the mini PC");
        RemovableDrive drive;
        lock (_sync)
        {
            if (_copy is { Running: true }) throw new InvalidOperationException("A copy is already running");
            drive = Selected(_drives) ?? throw new InvalidOperationException("No USB drive is plugged in");
            if (drive.MountPoint is null || _ejected.Contains(drive.Device)) throw new InvalidOperationException($"{drive.Name} isn't mounted");
        }
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        var total = files.Sum(f => f.Length);
        if (Free(drive.MountPoint) is { } free && free < total)
            throw new InvalidOperationException($"{drive.Name} has {Size(free)} free; {night} needs {Size(total)}");

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _copyCts = cts;
            _copy = new CopyStatus(night, true, 0, files.Count, 0, total, null, 0);
        }
        var destination = Path.Combine(drive.MountPoint, "Nova", night);
        _log.Info($"Copying {night} ({files.Count} files, {Size(total)}) to {drive.Name}…");
        _ = Task.Run(async () =>
        {
            int done = 0, skipped = 0;
            long bytes = 0;
            try
            {
                foreach (var file in files)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var target = Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
                    if (await SameFileAsync(file, target, cts.Token)) skipped++;
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await _writes.WaitAsync(cts.Token);
                        try
                        {
                            await CopyDurablyAsync(file, target, cts.Token);
                        }
                        finally
                        {
                            _writes.Release();
                        }
                    }
                    done++;
                    bytes += file.Length;
                    lock (_sync) _copy = _copy! with { FilesDone = done, BytesDone = bytes, Skipped = skipped };
                }
                lock (_sync) _copy = _copy! with { Running = false };
                _log.Info($"Copied {night} to {drive.Name}" + (skipped > 0 ? $" ({skipped} already there)" : ""));
            }
            catch (OperationCanceledException)
            {
                lock (_sync) _copy = _copy! with { Running = false, Error = "Cancelled" };
                _log.Info($"Copying {night} cancelled");
            }
            catch (Exception ex)
            {
                lock (_sync) _copy = _copy! with { Running = false, Error = ex.Message };
                _log.Warn($"Copying {night} to {drive.Name} failed: {ex.Message}");
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    public void CancelCopy()
    {
        lock (_sync) _copyCts?.Cancel();
    }

    private static readonly string[] FitsEndings = [".fits", ".fit", ".fts", ".fits.gz", ".fit.gz", ".fts.gz"];

    /// <summary>Where frames can be opened from: the mini PC's image folder, and the USB drive while it's mounted.</summary>
    public IReadOnlyList<BrowseRoot> BrowseRoots
    {
        get
        {
            List<BrowseRoot> roots = [new("local", "Mini PC")];
            RemovableDrive? drive;
            lock (_sync) drive = Selected(_drives);
            if (drive?.MountPoint is not null && !_ejected.Contains(drive.Device)) roots.Add(new("usb", $"USB: {drive.Name}"));
            return roots;
        }
    }

    /// <summary>A root's folder, or null.</summary>
    private string? RootFolder(string root)
    {
        if (root == "local") return _localRoot;
        if (root != "usb") return null;
        RemovableDrive? drive;
        lock (_sync) drive = Selected(_drives);
        return drive?.MountPoint is { } mount && !_ejected.Contains(drive.Device) ? mount : null;
    }

    /// <summary>
    /// The full path of <paramref name="path"/> inside a root, refusing anything that leads outside it ("..", links
    /// out, absolute paths): the page can only see the image folders.
    /// </summary>
    private string Resolve(string root, string? path)
    {
        var top = RootFolder(root) ?? throw new ArgumentException(root == "usb" ? "No USB drive is mounted" : $"No such place: {root}");
        top = Path.GetFullPath(top);
        var relative = (path ?? "").Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(relative)) throw new ArgumentException("Not inside the image folders");
        var full = Path.GetFullPath(Path.Combine(top, relative));
        var inside = full == top || full.StartsWith(top.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!inside) throw new ArgumentException("Not inside the image folders");
        return full;
    }

    private static readonly string[] PictureEndings = [".png", ".jpg", ".jpeg", ".tif", ".tiff"];

    /// <summary>A file's kind, for the browser: a FITS frame, a picture (a stack's PNG, say), or anything else.</summary>
    public static string KindOf(string name) =>
        FitsEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? "fits"
        : PictureEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? "image"
        : "other";

    /// <summary>
    /// Whether something on the way from the root to <paramref name="full"/> is a link that leads out of the root
    /// (a folder or file symlinked to /etc, say). The path itself is already checked to be inside; this checks where
    /// it really goes.
    /// </summary>
    private bool EscapesByLink(string root, string full)
    {
        var top = Path.GetFullPath(RootFolder(root)!).TrimEnd(Path.DirectorySeparatorChar);
        bool Inside(string p) => p == top || p.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        for (var at = full; at.Length > top.Length && Inside(at); at = Path.GetDirectoryName(at) ?? top)
        {
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !Inside(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar))) return true;
        }
        return false;
    }

    /// <summary>
    /// The folders and files in a folder of a root: nights newest first, then names in order. Only FITS files unless
    /// <paramref name="all"/> (the Mini PC tab's file browser shows everything, to download).
    /// </summary>
    public FolderListing Browse(string root, string? path, bool all = false)
    {
        var folder = Resolve(root, path);
        if (EscapesByLink(root, folder)) throw new ArgumentException("Not inside the image folders");
        if (!Directory.Exists(folder))
        {
            // The mini PC's image folder before the first frame is saved.
            if (string.IsNullOrEmpty(path?.Trim('/')) && root == "local") return new FolderListing(root, "", [], BrowseRoots);
            throw new ArgumentException("That folder isn't there");
        }
        var info = new DirectoryInfo(folder);
        static bool IsNight(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}$");
        var folders = info.EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(d => IsNight(d.Name) ? 0 : 1)
            .ThenByDescending(d => IsNight(d.Name) ? d.Name : "")
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FileEntry(d.Name, true, null, d.LastWriteTimeUtc));
        var files = info.EnumerateFiles()
            .Where(f => all ? !f.Name.StartsWith('.') && !f.Attributes.HasFlag(FileAttributes.Hidden) : KindOf(f.Name) == "fits")
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileEntry(f.Name, false, f.Length, f.LastWriteTimeUtc, KindOf(f.Name)));
        var relative = Path.GetRelativePath(Path.GetFullPath(RootFolder(root)!), folder).Replace('\\', '/');
        return new FolderListing(root, relative == "." ? "" : relative, [.. folders, .. files], BrowseRoots);
    }

    /// <summary>A FITS file inside a root, to open.</summary>
    public FileInfo FitsFile(string root, string path)
    {
        var file = AnyFile(root, path);
        return KindOf(file.Name) == "fits" ? file : throw new ArgumentException("Not a FITS file");
    }

    /// <summary>Any file inside a root (not a folder), to download: refused if it, or a folder on the way, leads out.</summary>
    public FileInfo AnyFile(string root, string path)
    {
        var full = Resolve(root, path);
        var file = new FileInfo(full);
        if (!file.Exists) throw new ArgumentException("That file isn't there");
        if (EscapesByLink(root, full)) throw new ArgumentException("Not inside the image folders");
        return file;
    }

    /// <summary>
    /// Deletes a file, or a folder with everything in it, inside a root (FileBrowserService checks the password
    /// first). Never a root's top, and never through a link that leads out of the image folders.
    /// </summary>
    public DeleteResult Delete(string root, string path)
    {
        var relative = (path ?? "").Replace('\\', '/').Trim('/');
        if (relative.Length == 0) throw new ArgumentException("Choose a night, folder or file to delete, not the whole image folder");
        var full = Resolve(root, relative);
        if (EscapesByLink(root, full)) throw new ArgumentException("Not inside the image folders");
        if (File.Exists(full))
        {
            var file = new FileInfo(full);
            var bytes = file.Length;
            file.Delete();
            return new DeleteResult(relative, 1, bytes);
        }
        if (!Directory.Exists(full)) throw new ArgumentException("That isn't there any more");
        var dir = new DirectoryInfo(full);
        // A link inside is removed as a link (its target is left alone), so only real files count.
        var files = dir.LinkTarget is null
            ? dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
            : [];
        dir.Delete(recursive: true);
        return new DeleteResult(relative, files.Count, files.Sum(f => f.Length));
    }

    /// <summary>The content type a downloaded file is sent with.</summary>
    public static string ContentTypeOf(string name)
    {
        var lower = name.ToLowerInvariant();
        if (KindOf(lower) == "fits") return lower.EndsWith(".gz") ? "application/gzip" : "application/fits";
        if (lower.EndsWith(".png")) return "image/png";
        if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) return "image/jpeg";
        if (lower.EndsWith(".tif") || lower.EndsWith(".tiff")) return "image/tiff";
        if (lower.EndsWith(".txt") || lower.EndsWith(".log") || lower.EndsWith(".csv")) return "text/plain; charset=utf-8";
        if (lower.EndsWith(".json")) return "application/json";
        return "application/octet-stream";
    }

    /// <summary>A warning if the frames still to take won't fit on the drive (null when they will, or saving locally).</summary>
    public string? SpaceWarning(long bytesNeeded)
    {
        if (_settings.Target != "usb") return null;
        RemovableDrive? drive;
        lock (_sync) drive = Selected(_drives);
        if (drive?.MountPoint is null || Free(drive.MountPoint) is not { } free) return null;
        return free - MinimumFree < bytesNeeded
            ? $"{drive.Name} has {Size(free)} free but the sequence needs about {Size(bytesNeeded)}: once it's full, frames go to the mini PC"
            : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // try again next time
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    /// <summary>Looks for drives, forgets ones unplugged, and mounts the chosen one if it isn't yet.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _nightsRead > TimeSpan.FromSeconds(30) || _copy is { Running: true })
        {
            _nights = Nights();
            _nightsRead = DateTime.UtcNow;
        }
        var drives = await _tool.ListAsync(ct);
        RemovableDrive? mount = null;
        lock (_sync)
        {
            var present = drives.Select(d => d.Device).ToHashSet();
            _ejected.RemoveWhere(d => !present.Contains(d));
            foreach (var gone in _failed.Keys.Where(d => !present.Contains(d)).ToList()) _failed.Remove(gone);
            _drives = drives;
            var selected = Selected(drives);
            if (_settings.Target == "usb" && selected is { MountPoint: null } && !_ejected.Contains(selected.Device) && !_failed.ContainsKey(selected.Device))
                mount = selected;
            // First drive seen while saving to USB: remember it.
            if (_settings.Target == "usb" && selected is not null && _settings.DriveUuid is null && selected.Uuid is not null)
            {
                _settings = _settings with { DriveUuid = selected.Uuid };
                Save();
            }
        }
        if (mount is null) return;
        try
        {
            var at = await _tool.MountAsync(mount, ct);
            lock (_sync) _drives = _drives.Select(d => d.Device == mount.Device ? d with { MountPoint = at } : d).ToList();
            _log.Info($"{mount.Name} mounted: frames are saved to it");
            lock (_sync) _lastWarned = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _failed[mount.Device] = $"couldn't be mounted ({ex.Message.TrimEnd('.')})";
            _log.Warn($"Couldn't mount {mount.Name}: {ex.Message}");
        }
    }

    /// <summary>The drive to save to: the one chosen before (same filesystem id), else the first.</summary>
    private RemovableDrive? Selected(IReadOnlyList<RemovableDrive> drives) =>
        drives.FirstOrDefault(d => d.Uuid is not null && d.Uuid == _settings.DriveUuid) ?? drives.FirstOrDefault();

    private IReadOnlyList<NightFolder> Nights()
    {
        try
        {
            if (!Directory.Exists(_localRoot)) return [];
            return Directory.GetDirectories(_localRoot)
                .Select(Path.GetFileName)
                .Where(n => n is not null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}$"))
                .OrderDescending()
                .Take(14)
                .Select(n =>
                {
                    var files = Directory.GetFiles(Path.Combine(_localRoot, n!), "*", SearchOption.AllDirectories);
                    return new NightFolder(n!, files.Length, files.Sum(f => new FileInfo(f).Length));
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long? Free(string? mountPoint)
    {
        if (mountPoint is null) return null;
        try
        {
            return new DriveInfo(mountPoint).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private Settings Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(_file)) ?? new() : new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
            return new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_settings));
        }
        catch (Exception ex)
        {
            _log.Error($"Could not save {_file}", ex);
        }
    }
}
