using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private BackupService Service()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        return new BackupService(options, log, new DriverSetupService(equipment, options, log), new HotspotService(options, log), new Lifetime());
    }

    private void Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_folder, name));

    private static async Task<MemoryStream> Zip(BackupService backup)
    {
        var zip = new MemoryStream();
        await backup.WriteAsync(zip, CancellationToken.None);
        zip.Position = 0;
        return zip;
    }

    [Fact]
    public async Task BacksUpTheSettingsAndLeavesOutTheCaches()
    {
        Write("sites.json", """{ "Active": "Home" }""");
        Write("sequences/M31.json", "{}");
        Write("guide-darks/dark-2s.fits", "dark");
        Write("sky/tile.png", "cache");
        Write("image-history/1.fits", "cache");
        Write("keys/key.xml", "runtime");

        using var zipped = await Zip(Service());
        using var zip = new ZipArchive(zipped);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("nova-backup.json", names);
        Assert.Contains("config/sites.json", names);
        Assert.Contains("config/sequences/M31.json", names);
        Assert.Contains("config/guide-darks/dark-2s.fits", names);
        Assert.DoesNotContain(names, n => n.StartsWith("config/sky/") || n.StartsWith("config/image-history/") || n.StartsWith("config/keys/"));
    }

    [Fact]
    public async Task ARestoreGivesBackExactlyTheBackupsSettings()
    {
        Write("sites.json", """{ "Active": "Home" }""");
        Write("sequences/M31.json", """{ "Name": "M31" }""");
        var backup = Service();
        using var zipped = await Zip(backup);

        // Changed since: a setting edited, one added, a sequence swapped.
        Write("sites.json", """{ "Active": "Dark site" }""");
        Write("optics.json", "{}");
        File.Delete(Path.Combine(_folder, "sequences/M31.json"));
        Write("sequences/M42.json", "{}");
        Write("sky/tile.png", "cache");

        var info = await backup.StageAsync(zipped, CancellationToken.None);
        Assert.Equal(Environment.MachineName, info.Host);
        Assert.Contains("Dark site", Read("sites.json")); // nothing swapped while NOVA runs...

        Assert.True(BackupService.ApplyPendingFiles(_folder)); // ...only at the next start
        Assert.Contains("Home", Read("sites.json"));
        Assert.False(File.Exists(Path.Combine(_folder, "optics.json")));       // the backup's settings, not a mixture
        Assert.True(File.Exists(Path.Combine(_folder, "sequences/M31.json")));
        Assert.False(File.Exists(Path.Combine(_folder, "sequences/M42.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "sky/tile.png")));        // caches are left alone
        Assert.False(BackupService.ApplyPendingFiles(_folder));                 // done once

        await backup.ApplyPendingExtrasAsync(CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(_folder, BackupService.Pending)));
    }

    [Fact]
    public async Task RefusesAnythingButOneOfItsBackups()
    {
        var backup = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => backup.StageAsync(new MemoryStream("not a zip"u8.ToArray()), CancellationToken.None));

        var other = new MemoryStream();
        using (var zip = new ZipArchive(other, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("photo.jpg");
        other.Position = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => backup.StageAsync(other, CancellationToken.None));

        // A crafted name mustn't write outside the staging folder.
        var crafted = new MemoryStream();
        using (var zip = new ZipArchive(crafted, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("nova-backup.json").Open()))
                w.Write("""{ "Version": "1", "CreatedUtc": "2026-10-24T00:00:00Z", "Host": "x" }""");
            using (var w = new StreamWriter(zip.CreateEntry("../../escaped.txt").Open())) w.Write("no");
        }
        crafted.Position = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => backup.StageAsync(crafted, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_folder)!, "escaped.txt")));
    }
}
