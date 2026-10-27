using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Imaging;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class FileBrowserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-files-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_dir, "images");
    private string Config => Path.Combine(_dir, "config");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (FileBrowserService Files, PasswordService Password, ActivityLog Log) Create()
    {
        var options = Opts.Create(new NovaOptions { ConfigDirectory = Config, ImageDirectory = Local });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var password = new PasswordService(options, log);
        return (new FileBrowserService(new StorageService(options, log), password, log, options), password, log);
    }

    private void Write(string relative, byte[] data)
    {
        var path = Path.Combine(Local, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    [Fact]
    public async Task DeletingNeedsThePasswordEveryTime()
    {
        var (files, password, log) = Create();
        Write("2026-10-26/M 31/a.fits", new byte[1000]);
        Write("2026-10-26/M 31/b.fits", new byte[500]);
        Write("2026-10-26/M 31/Stacked/s.png", new byte[200]);
        Write("2026-10-25/c.fits", new byte[100]);

        // No password set: nothing can be deleted (anyone on the network could otherwise).
        var none = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.DeleteAsync("local", "2026-10-25/c.fits", null, CancellationToken.None));
        Assert.Contains("set one first", none.Message);

        password.Set("telescope");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.DeleteAsync("local", "2026-10-25/c.fits", "", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.DeleteAsync("local", "2026-10-25/c.fits", "wrong", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(Local, "2026-10-25/c.fits")));

        // A file, then a folder with everything in it.
        var one = await files.DeleteAsync("local", "2026-10-25/c.fits", "telescope", CancellationToken.None);
        Assert.Equal(new DeleteResult("2026-10-25/c.fits", 1, 100), one);
        var night = await files.DeleteAsync("local", "2026-10-26/M 31", "telescope", CancellationToken.None);
        Assert.Equal((3, 1700L), (night.Files, night.Bytes));
        Assert.False(Directory.Exists(Path.Combine(Local, "2026-10-26/M 31")));
        Assert.True(Directory.Exists(Path.Combine(Local, "2026-10-26"))); // only what was chosen
        Assert.Contains(log.Recent, e => e.Message.Contains("Deleted 2026-10-26/M 31 from the mini PC (3 files"));

        // Never the image folder itself, or anything outside it.
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync("local", "", "telescope", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync("local", "/", "telescope", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync("local", "../config", "telescope", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync("local", "2026-10-26/missing.fits", "telescope", CancellationToken.None));
        Assert.True(Directory.Exists(Config));
    }

    [Fact]
    public async Task MakesAThumbnailOfAFitsFileOnceAndKeepsIt()
    {
        var (files, _, _) = Create();
        var pixels = new ushort[400 * 300];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(1000 + i % 400 * 10);
        Write("2026-10-26/M 31/a.fits", Fits.WriteUInt16(400, 300, pixels));
        Write("2026-10-26/M 31/broken.fits", "not a FITS file at all"u8.ToArray());
        Write("2026-10-26/M 31/notes.txt", "x"u8.ToArray());

        var png = await files.ThumbnailAsync("local", "2026-10-26/M 31/a.fits", CancellationToken.None);
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png![..4]);
        var cached = Directory.GetFiles(Path.Combine(Config, "file-thumbnails"), "*.png");
        Assert.Single(cached);
        Assert.Equal(png, await files.ThumbnailAsync("local", "2026-10-26/M 31/a.fits", CancellationToken.None)); // from the cache
        Assert.Single(Directory.GetFiles(Path.Combine(Config, "file-thumbnails"), "*.png"));

        Assert.Null(await files.ThumbnailAsync("local", "2026-10-26/M 31/broken.fits", CancellationToken.None)); // a plain icon instead
        await Assert.ThrowsAsync<ArgumentException>(() => files.ThumbnailAsync("local", "2026-10-26/M 31/notes.txt", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ThumbnailAsync("local", "../config/auth.json", CancellationToken.None));
    }
}
