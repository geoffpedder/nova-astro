using System.Diagnostics;
using System.Text.Json;

namespace Nova.Server.Services;

/// <summary>A USB drive's filesystem (a partition, or a whole drive with no partition table).</summary>
/// <param name="Device">The block device, e.g. /dev/sdb1.</param>
/// <param name="Disk">The drive it's on, e.g. /dev/sdb (powered off when ejecting).</param>
/// <param name="Uuid">The filesystem's id: the same drive keeps it from one plug-in to the next.</param>
/// <param name="MountPoint">Where it's mounted, or null.</param>
public sealed record RemovableDrive(
    string Device, string Disk, string? Uuid, string? Label, string? Model, long SizeBytes, string? FileSystem, string? MountPoint)
{
    /// <summary>How to name it: its label, else its maker and model, else the device.</summary>
    public string Name => !string.IsNullOrWhiteSpace(Label) ? Label! : !string.IsNullOrWhiteSpace(Model) ? Model!.Trim() : Device;
}

/// <summary>Finds, mounts and ejects USB drives.</summary>
public interface IDriveTool
{
    Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken ct);

    /// <summary>Mounts the drive and says where.</summary>
    Task<string> MountAsync(RemovableDrive drive, CancellationToken ct);

    /// <summary>Unmounts it (writing out anything still cached) and powers the drive off, so it can be unplugged.</summary>
    Task EjectAsync(RemovableDrive drive, CancellationToken ct);
}

/// <summary>
/// Linux: lsblk lists the drives and udisks2 mounts and ejects them, as a desktop does for a logged-in user. NOVA runs
/// as a service without a desktop session, so setup adds a polkit rule letting NOVA's user do this
/// (deploy/nova-storage.rules). Drives are mounted under /run/media/&lt;user&gt; (Arch) or /media/&lt;user&gt; (Debian, Ubuntu).
/// </summary>
public sealed class UdisksDriveTool : IDriveTool
{
    private static readonly HashSet<string> FileSystems = ["vfat", "exfat", "ntfs", "ntfs3", "ext4", "ext3", "ext2", "btrfs", "xfs", "f2fs"];

    public static bool Available => OperatingSystem.IsLinux() && File.Exists("/usr/bin/udisksctl") && File.Exists("/usr/bin/lsblk");

    public async Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken ct)
    {
        var (code, output, _) = await RunAsync("lsblk", ["-J", "-b", "-l", "-o", "PATH,PKNAME,TYPE,TRAN,RM,HOTPLUG,UUID,LABEL,MODEL,SIZE,FSTYPE,MOUNTPOINT"], ct);
        return code == 0 ? ParseLsblk(output) : [];
    }

    /// <summary>The USB filesystems in lsblk's JSON (-J -b -l, the columns above).</summary>
    public static IReadOnlyList<RemovableDrive> ParseLsblk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var devices = doc.RootElement.GetProperty("blockdevices").EnumerateArray().ToList();
        string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        // Older lsblk sends "1"/"0", newer true/false.
        bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "1"));
        long Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out var n) ? n : 0 : 0;

        var byName = devices.Where(d => Text(d, "path") is not null).ToDictionary(d => Path.GetFileName(Text(d, "path")!), d => d);
        var drives = new List<RemovableDrive>();
        foreach (var d in devices)
        {
            var type = Text(d, "type");
            var fs = Text(d, "fstype");
            if (fs is null || !FileSystems.Contains(fs) || type is not ("part" or "disk")) continue;
            // A partition's transport and model are its drive's.
            var disk = type == "part" && Text(d, "pkname") is { } parent && byName.TryGetValue(parent, out var p) ? p : d;
            var usb = Text(disk, "tran") == "usb" || Flag(disk, "rm") || Flag(disk, "hotplug");
            if (!usb) continue;
            var mount = Text(d, "mountpoint");
            if (mount is "/" or "/boot" or "/boot/efi" or "/efi" or "[SWAP]") continue;
            drives.Add(new RemovableDrive(Text(d, "path")!, Text(disk, "path")!, Text(d, "uuid"), Text(d, "label"), Text(disk, "model"),
                Number(d, "size"), fs, mount));
        }
        return drives;
    }

    public async Task<string> MountAsync(RemovableDrive drive, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync("udisksctl", ["mount", "-b", drive.Device, "--no-user-interaction"], ct);
        if (code != 0) throw new IOException(Explain(error, output));
        // "Mounted /dev/sdb1 at /run/media/user/NOVA" (older versions end it with a full stop).
        var at = output.IndexOf(" at ", StringComparison.Ordinal);
        if (at < 0) throw new IOException($"udisksctl didn't say where it mounted {drive.Device}: {output.Trim()}");
        return output[(at + 4)..].Trim().TrimEnd('.');
    }

    public async Task EjectAsync(RemovableDrive drive, CancellationToken ct)
    {
        if (drive.MountPoint is not null)
        {
            var (code, output, error) = await RunAsync("udisksctl", ["unmount", "-b", drive.Device, "--no-user-interaction"], ct);
            if (code != 0) throw new IOException(Explain(error, output));
        }
        // Spins it down and tells the drive it's done; some readers refuse, which is fine once it's unmounted.
        await RunAsync("udisksctl", ["power-off", "-b", drive.Disk, "--no-user-interaction"], ct);
    }

    private static string Explain(string error, string output)
    {
        var text = (error.Length > 0 ? error : output).Trim();
        if (text.Contains("NotAuthorized", StringComparison.Ordinal))
            return "NOVA isn't allowed to mount drives yet: run nova update (it adds the permission)";
        if (text.Contains("target is busy", StringComparison.OrdinalIgnoreCase) || text.Contains("DeviceBusy", StringComparison.Ordinal))
            return "the drive is still in use";
        return text.Length > 0 ? text : "udisksctl failed";
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException($"Couldn't run {file}");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(); } catch { /* gone */ }
            return (-1, "", $"{file} didn't finish within 30 s");
        }
        return (process.ExitCode, await output, await error);
    }
}

/// <summary>
/// A folder standing in for a USB drive (development and tests): plugged in while the folder exists, "mounted" at
/// the folder itself.
/// </summary>
public sealed class FolderDriveTool(string folder, string label = "SIMULATED USB") : IDriveTool
{
    private bool _mounted;

    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            _mounted = false;
            return Task.FromResult<IReadOnlyList<RemovableDrive>>([]);
        }
        var size = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).TotalSize;
        return Task.FromResult<IReadOnlyList<RemovableDrive>>(
            [new RemovableDrive("/dev/sim1", "/dev/sim", "SIM-0001", label, "Simulated drive", size, "exfat", _mounted ? folder : null)]);
    }

    public Task<string> MountAsync(RemovableDrive drive, CancellationToken ct)
    {
        _mounted = true;
        return Task.FromResult(folder);
    }

    public Task EjectAsync(RemovableDrive drive, CancellationToken ct)
    {
        _mounted = false;
        return Task.CompletedTask;
    }
}

/// <summary>No USB drives (Windows and macOS during development, or a system without udisks2).</summary>
public sealed class NoDriveTool : IDriveTool
{
    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemovableDrive>>([]);
    public Task<string> MountAsync(RemovableDrive drive, CancellationToken ct) => throw new NotSupportedException("No USB drive support here");
    public Task EjectAsync(RemovableDrive drive, CancellationToken ct) => Task.CompletedTask;
}
