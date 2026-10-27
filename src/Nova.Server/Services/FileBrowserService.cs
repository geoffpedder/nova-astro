using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nova.Core.Imaging;

namespace Nova.Server.Services;

/// <param name="Files">How many files went (one for a file, everything inside for a folder).</param>
public sealed record DeleteResult(string Path, int Files, long Bytes);

/// <summary>
/// The Mini PC tab's file browser, beyond listing (StorageService): small previews of FITS files, and deleting, which
/// needs NOVA's password every time. Thumbnails are made once per file version and kept in the config folder's
/// file-thumbnails cache (left out of backups), two at a time at most so a long folder doesn't swamp the mini PC.
/// </summary>
public sealed class FileBrowserService
{
    public const int ThumbnailSize = 192;
    private const int CacheLimit = 4000;

    private readonly StorageService _storage;
    private readonly PasswordService _password;
    private readonly ActivityLog _log;
    private readonly string _cache;
    private readonly SemaphoreSlim _making = new(2, 2);

    public FileBrowserService(StorageService storage, PasswordService password, ActivityLog log, IOptions<NovaOptions> options)
    {
        _storage = storage;
        _password = password;
        _log = log;
        _cache = Path.Combine(options.Value.ResolveConfigDirectory(), "file-thumbnails");
    }

    /// <summary>A small stretched preview (PNG) of a FITS file in the image folders, or null if it can't be read.</summary>
    public async Task<byte[]?> ThumbnailAsync(string root, string path, CancellationToken ct)
    {
        var file = _storage.FitsFile(root, path);
        // One per version of the file: a file written again (same name) gets a new one.
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
        var cached = Path.Combine(_cache, key + ".png");
        if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct);

        await _making.WaitAsync(ct);
        try
        {
            if (File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct); // made while this one waited
            var png = await Task.Run(() => Render(file.FullName), ct);
            if (png is null) return null;
            Directory.CreateDirectory(_cache);
            await File.WriteAllBytesAsync(cached, png, ct);
            Prune();
            return png;
        }
        finally
        {
            _making.Release();
        }
    }

    private static byte[]? Render(string fullName)
    {
        try
        {
            var data = File.ReadAllBytes(fullName);
            if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B) // .fits.gz
            {
                using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var unpacked = new MemoryStream();
                gz.CopyTo(unpacked);
                data = unpacked.ToArray();
            }
            var image = Fits.Read(data);
            return (Debayer.FromHeaders(image) is { } bayer ? Preview.RenderColor(image, bayer, ThumbnailSize) : Preview.Render(image, ThumbnailSize)).Png;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null; // not an image NOVA can read: the browser shows a plain icon
        }
    }

    // The cache stays a few thousand previews: the oldest go first.
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_cache).GetFiles("*.png");
            if (files.Length <= CacheLimit) return;
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - CacheLimit)) old.Delete();
        }
        catch (IOException)
        {
            // another request pruning at the same time: fine
        }
    }

    /// <summary>
    /// Deletes a file, or a folder and everything in it, from the image folders, with NOVA's password (asked every
    /// time). Without a password set nothing can be deleted: anyone on the network could otherwise.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No password is set, or it's wrong.</exception>
    /// <exception cref="ArgumentException">Not inside the image folders, a folder's top, or not there.</exception>
    public async Task<DeleteResult> DeleteAsync(string root, string path, string? password, CancellationToken ct)
    {
        if (!_password.PasswordSet)
            throw new UnauthorizedAccessException("Deleting needs NOVA's password: set one first (the power menu → Set a password…)");
        if (string.IsNullOrEmpty(password) || !await _password.CheckAsync(password, ct))
            throw new UnauthorizedAccessException("That isn't NOVA's password");
        var result = _storage.Delete(root, path);
        _log.Info($"Deleted {result.Path} from {(root == "usb" ? "the USB drive" : "the mini PC")} ({result.Files} file{(result.Files == 1 ? "" : "s")}, {Size(result.Bytes)})");
        return result;
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
