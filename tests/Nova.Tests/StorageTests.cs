using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nova-storage-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_dir, "local");
    private string Usb => Path.Combine(_dir, "usb");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A USB stick that can be plugged in and out; "mounting" points it at a folder.</summary>
    private sealed class FakeDrives(string mountAt) : IDriveTool
    {
        public bool Plugged { get; set; }
        public string? MountPoint { get; set; }
        public int Mounts { get; private set; }
        public int Ejects { get; private set; }

        public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemovableDrive>>(
            Plugged ? [new RemovableDrive("/dev/sdb1", "/dev/sdb", "AB12-CD34", "ASTRO", "SanDisk Ultra", 64_000_000_000, "exfat", MountPoint)] : []);

        public Task<string> MountAsync(RemovableDrive drive, CancellationToken ct)
        {
            Mounts++;
            Directory.CreateDirectory(mountAt);
            MountPoint = mountAt;
            return Task.FromResult(mountAt);
        }

        public Task EjectAsync(RemovableDrive drive, CancellationToken ct)
        {
            Ejects++;
            MountPoint = null;
            return Task.CompletedTask;
        }
    }

    private (StorageService Storage, FakeDrives Drives, List<string> FellBack) Create()
    {
        var options = Opts.Create(new NovaOptions { ConfigDirectory = Path.Combine(_dir, "config"), ImageDirectory = Local });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var drives = new FakeDrives(Usb);
        var storage = new StorageService(options, log, drives);
        var fellBack = new List<string>();
        storage.FellBack += (_, why) => fellBack.Add(why);
        return (storage, drives, fellBack);
    }

    private static string Frame(string root) => Path.Combine(root, "2026-10-10", "M 31", "Light_300s.fits");

    [Fact]
    public async Task SavesOnTheMiniPcUntilTheDriveIsChosen()
    {
        var (storage, drives, _) = Create();
        drives.Plugged = true;
        await storage.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, drives.Mounts); // not mounted until it's wanted
        Assert.Equal(Local, storage.ImageRoot());
        var path = await storage.WriteAsync(Frame, [1, 2, 3]);
        Assert.StartsWith(Local, path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MountsTheChosenDriveAndSavesToIt()
    {
        var (storage, drives, fellBack) = Create();
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);

        Assert.Equal(1, drives.Mounts);
        Assert.Equal(Path.Combine(Usb, "Nova"), storage.ImageRoot());
        var path = await storage.WriteAsync(Frame, [1, 2, 3]);
        Assert.Equal(Frame(Path.Combine(Usb, "Nova")), path);
        Assert.Empty(fellBack);
        var status = storage.Status;
        Assert.True(status.OnUsb);
        Assert.Equal("ASTRO", Assert.Single(status.Drives).Name);

        // Remembered: a new start (same settings file) still saves to the drive.
        var options = Opts.Create(new NovaOptions { ConfigDirectory = Path.Combine(_dir, "config"), ImageDirectory = Local });
        var again = new StorageService(options, new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance), drives);
        Assert.Equal("usb", again.Target);
    }

    [Fact]
    public async Task FallsBackToTheMiniPcWhenTheDriveIsPulledOutAndPicksUpAgainWhenItsBack()
    {
        var (storage, drives, fellBack) = Create();
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);

        drives.Plugged = false;
        drives.MountPoint = null;
        await storage.RefreshAsync(CancellationToken.None);
        Assert.StartsWith(Local, await storage.WriteAsync(Frame, [1]));
        Assert.StartsWith(Local, await storage.WriteAsync(Frame, [2]));
        Assert.Equal("No USB drive is plugged in: frames are saved on the mini PC", Assert.Single(fellBack)); // said once
        Assert.NotNull(storage.Status.Warning);

        drives.Plugged = true;
        await storage.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, drives.Mounts);
        Assert.StartsWith(Usb, await storage.WriteAsync(Frame, [3]));
    }

    [Fact]
    public async Task KeepsTheFrameWhenTheDriveCantBeWritten()
    {
        var (storage, drives, fellBack) = Create();
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);
        // Something on the drive is in the way: the night's folder can't be made (as on a read-only or failing drive).
        Directory.CreateDirectory(Path.Combine(Usb, "Nova"));
        await File.WriteAllTextAsync(Path.Combine(Usb, "Nova", "2026-10-10"), "not a folder");

        var path = await storage.WriteAsync(Frame, [7, 7]);
        Assert.Equal(Frame(Local), path);
        Assert.Equal([7, 7], await File.ReadAllBytesAsync(path));
        Assert.Contains("Couldn't save to ASTRO", Assert.Single(fellBack));
        Assert.Equal(Local, storage.ImageRoot()); // not tried again until it's plugged in again
    }

    [Fact]
    public async Task EjectsSafelyAndRemountsWhenPluggedInAgain()
    {
        var (storage, drives, _) = Create();
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);

        await storage.EjectAsync(CancellationToken.None);
        Assert.Equal(1, drives.Ejects);
        await storage.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, drives.Mounts); // still in the socket, but ejected: not mounted again
        Assert.Equal(Local, storage.ImageRoot());
        Assert.Contains("ejected", storage.Status.Warning);

        drives.Plugged = false;
        await storage.RefreshAsync(CancellationToken.None);
        drives.Plugged = true;
        await storage.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, drives.Mounts);
        Assert.StartsWith(Usb, storage.ImageRoot());
    }

    [Fact]
    public async Task CopiesANightToTheDriveSkippingWhatsAlreadyThere()
    {
        var (storage, drives, _) = Create();
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);
        await storage.SetTargetAsync("local", null, CancellationToken.None); // saving locally, drive still mounted
        var night = Path.Combine(Local, "2026-10-09");
        Directory.CreateDirectory(Path.Combine(night, "M 31"));
        await File.WriteAllBytesAsync(Path.Combine(night, "M 31", "a.fits"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(night, "M 31", "b.fits"), new byte[2000]);
        Directory.CreateDirectory(Path.Combine(Usb, "Nova", "2026-10-09", "M 31"));
        await File.WriteAllBytesAsync(Path.Combine(Usb, "Nova", "2026-10-09", "M 31", "a.fits"), new byte[1000]);
        File.SetLastWriteTimeUtc(Path.Combine(Usb, "Nova", "2026-10-09", "M 31", "a.fits"), File.GetLastWriteTimeUtc(Path.Combine(night, "M 31", "a.fits")));

        storage.StartCopy("2026-10-09");
        for (var i = 0; i < 100 && storage.Status.Copy is { Running: true }; i++) await Task.Delay(20);
        var copy = storage.Status.Copy!;
        Assert.False(copy.Running);
        Assert.Null(copy.Error);
        Assert.Equal((2, 2, 1), (copy.FilesDone, copy.FilesTotal, copy.Skipped));
        Assert.Equal(2000, new FileInfo(Path.Combine(Usb, "Nova", "2026-10-09", "M 31", "b.fits")).Length);

        Assert.Throws<ArgumentException>(() => storage.StartCopy("../etc"));
        Assert.Throws<ArgumentException>(() => storage.StartCopy("2020-01-01"));
    }

    [Fact]
    public async Task ACopyOfTheRightSizeButNotFinishedIsCopiedAgain()
    {
        // As left by a drive unplugged before Linux had written it all out: the full size, the start of the data, then
        // nothing. Even with the original's time on it, it isn't taken for a finished copy.
        var dir = Directory.CreateDirectory(Path.Combine(Local, "frames")).FullName;
        var original = new byte[300_000];
        new Random(1).NextBytes(original);
        var source = Path.Combine(dir, "a.fits");
        await File.WriteAllBytesAsync(source, original);
        var target = Path.Combine(dir, "copy.fits");
        var broken = new byte[original.Length];
        original.AsSpan(0, 30_000).CopyTo(broken);
        await File.WriteAllBytesAsync(target, broken);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));

        var file = new FileInfo(source);
        Assert.False(await StorageService.SameFileAsync(file, target, CancellationToken.None));
        await StorageService.CopyDurablyAsync(file, target, CancellationToken.None);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal(file.LastWriteTimeUtc, File.GetLastWriteTimeUtc(target)); // the mark of a finished copy
        Assert.True(await StorageService.SameFileAsync(file, target, CancellationToken.None));

        // The same bytes but a different time (another file of the same size, an older copy): copied again.
        File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc.AddMinutes(-10));
        Assert.False(await StorageService.SameFileAsync(file, target, CancellationToken.None));
    }

    [Fact]
    public async Task WarnsWhenTheSequenceWontFit()
    {
        var (storage, drives, _) = Create();
        drives.Plugged = true;
        Assert.Null(storage.SpaceWarning(long.MaxValue / 2)); // saving locally: nothing to say
        await storage.SetTargetAsync("usb", null, CancellationToken.None);
        Assert.Null(storage.SpaceWarning(1000));
        Assert.Contains("frames go to the mini PC", storage.SpaceWarning(long.MaxValue / 2));
    }

    [Fact]
    public async Task BrowsesTheImageFoldersAndNothingElse()
    {
        var (storage, drives, _) = Create();
        Directory.CreateDirectory(Path.Combine(Local, "2026-10-08"));
        Directory.CreateDirectory(Path.Combine(Local, "2026-10-09", "M 31"));
        Directory.CreateDirectory(Path.Combine(Local, "flats"));
        await File.WriteAllBytesAsync(Path.Combine(Local, "2026-10-09", "M 31", "b.fits"), new byte[300]);
        await File.WriteAllBytesAsync(Path.Combine(Local, "2026-10-09", "M 31", "a.fit"), new byte[100]);
        await File.WriteAllTextAsync(Path.Combine(Local, "2026-10-09", "M 31", "notes.txt"), "not a frame");

        var top = storage.Browse("local", null);
        Assert.Equal(["2026-10-09", "2026-10-08", "flats"], top.Entries.Select(e => e.Name)); // nights newest first
        Assert.All(top.Entries, e => Assert.True(e.Folder));
        Assert.Equal(["local"], top.Roots.Select(r => r.Id)); // no drive mounted

        var target = storage.Browse("local", "2026-10-09/M 31");
        Assert.Equal("2026-10-09/M 31", target.Path);
        Assert.Equal([("a.fit", 100L), ("b.fits", 300L)], target.Entries.Select(e => (e.Name, e.Bytes!.Value)));
        Assert.Equal(300, storage.FitsFile("local", "2026-10-09/M 31/b.fits").Length);

        // Only inside the image folders, and only FITS files.
        Assert.Throws<ArgumentException>(() => storage.Browse("local", ".."));
        Assert.Throws<ArgumentException>(() => storage.Browse("local", "2026-10-09/../../config"));
        Assert.Throws<ArgumentException>(() => storage.FitsFile("local", "2026-10-09/M 31/notes.txt"));
        Assert.Throws<ArgumentException>(() => storage.FitsFile("local", "../config/storage.fits"));
        Assert.Throws<ArgumentException>(() => storage.Browse("usb", null)); // nothing mounted
        Assert.Throws<ArgumentException>(() => storage.Browse("elsewhere", null));

        // A mounted drive can be browsed too, from its top.
        drives.Plugged = true;
        await storage.SetTargetAsync("usb", null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(Usb, "Nova", "2026-10-01"));
        var usb = storage.Browse("usb", null);
        Assert.Equal(["local", "usb"], usb.Roots.Select(r => r.Id));
        Assert.Contains(usb.Entries, e => e is { Name: "Nova", Folder: true });
    }

    [Fact]
    public async Task TheFileBrowserListsAndHandsOutEveryFileButNothingOutside()
    {
        var (storage, _, _) = Create();
        var night = Path.Combine(Local, "2026-10-09", "M 31");
        Directory.CreateDirectory(Path.Combine(night, "Stacked"));
        await File.WriteAllBytesAsync(Path.Combine(night, "a.fits"), new byte[100]);
        await File.WriteAllTextAsync(Path.Combine(night, "notes.txt"), "a note");
        await File.WriteAllBytesAsync(Path.Combine(night, "Stacked", "Stack_L_120s_10x.png"), new byte[50]);
        await File.WriteAllTextAsync(Path.Combine(night, ".hidden"), "x");

        // Every file (not hidden ones), each with its kind; the FITS-only listing is unchanged.
        var all = storage.Browse("local", "2026-10-09/M 31", all: true);
        Assert.Equal([("Stacked", null), ("a.fits", "fits"), ("notes.txt", "other")], all.Entries.Select(e => (e.Name, e.Kind)));
        Assert.Equal(["Stacked", "a.fits"], storage.Browse("local", "2026-10-09/M 31").Entries.Select(e => e.Name));
        Assert.Equal("image", storage.Browse("local", "2026-10-09/M 31/Stacked", all: true).Entries.Single().Kind);

        Assert.Equal("a note", await File.ReadAllTextAsync(storage.AnyFile("local", "2026-10-09/M 31/notes.txt").FullName));
        Assert.Equal("image/png", StorageService.ContentTypeOf("Stack_L_120s_10x.png"));
        Assert.Equal("application/fits", StorageService.ContentTypeOf("a.fits"));
        Assert.Throws<ArgumentException>(() => storage.AnyFile("local", "2026-10-09/M 31")); // a folder
        Assert.Throws<ArgumentException>(() => storage.AnyFile("local", "2026-10-09/M 31/missing.txt"));
        Assert.Throws<ArgumentException>(() => storage.AnyFile("local", "../config/storage.json"));

        // A link inside the image folders that leads out of them is refused (where the machine allows making one).
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "private");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(night, "away"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the right to make links
        }
        Assert.Throws<ArgumentException>(() => storage.AnyFile("local", "2026-10-09/M 31/away/secret.txt"));
        Assert.Throws<ArgumentException>(() => storage.Browse("local", "2026-10-09/M 31/away", all: true));
    }

    [Fact]
    public void FindsUsbDrivesInLsblksOutput()
    {
        // An internal NVMe system disk, a USB stick with one exFAT partition (mounted), and a USB card reader's
        // unpartitioned FAT card (older lsblk: "1"/"0" flags).
        const string json = """
        {"blockdevices": [
          {"path":"/dev/nvme0n1", "pkname":null, "type":"disk", "tran":"nvme", "rm":false, "hotplug":false, "uuid":null, "label":null, "model":"Samsung SSD 980", "size":500107862016, "fstype":null, "mountpoint":null},
          {"path":"/dev/nvme0n1p2", "pkname":"nvme0n1", "type":"part", "tran":null, "rm":false, "hotplug":false, "uuid":"1111", "label":null, "model":null, "size":499000000000, "fstype":"ext4", "mountpoint":"/"},
          {"path":"/dev/sda", "pkname":null, "type":"disk", "tran":"usb", "rm":true, "hotplug":true, "uuid":null, "label":null, "model":"SanDisk 3.2Gen1 ", "size":61530439680, "fstype":null, "mountpoint":null},
          {"path":"/dev/sda1", "pkname":"sda", "type":"part", "tran":null, "rm":true, "hotplug":true, "uuid":"AB12-CD34", "label":"ASTRO", "model":null, "size":61529391104, "fstype":"exfat", "mountpoint":"/run/media/user/ASTRO"},
          {"path":"/dev/sdb", "pkname":null, "type":"disk", "tran":"usb", "rm":"1", "hotplug":"1", "uuid":"5E2A-1B3C", "label":null, "model":"Card Reader", "size":31914983424, "fstype":"vfat", "mountpoint":null}
        ]}
        """;
        var drives = UdisksDriveTool.ParseLsblk(json);
        Assert.Equal(2, drives.Count);
        Assert.Equal(new RemovableDrive("/dev/sda1", "/dev/sda", "AB12-CD34", "ASTRO", "SanDisk 3.2Gen1", 61529391104, "exfat", "/run/media/user/ASTRO"), drives[0]);
        Assert.Equal(("/dev/sdb", "/dev/sdb", "Card Reader"), (drives[1].Device, drives[1].Disk, drives[1].Name));
        Assert.Null(drives[1].MountPoint);
    }
}
