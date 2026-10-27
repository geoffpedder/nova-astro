using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>When a backup was made, and by which NOVA.</summary>
public sealed record BackupInfo(string Version, DateTime CreatedUtc, string Host);

/// <summary>
/// Backs up all of NOVA's settings into one zip, and restores one: to move to another mini PC, or to go back after
/// a mistake. The backup holds the settings folder (sites, optics, equipment profiles, the guider and its darks,
/// sequences, notifications, the password...), the INDI driver list and the hotspot's settings; caches that fill
/// themselves again (sky tiles, survey images, image history, file thumbnails) are left out.
/// <para>
/// A restore doesn't swap the files while NOVA runs (its services would save over them): it puts the backup aside and
/// restarts NOVA, which swaps them in at startup, before anything reads its settings, then sets the drivers and the
/// hotspot.
/// </para>
/// </summary>
public sealed class BackupService(
    IOptions<NovaOptions> options, ActivityLog log, DriverSetupService drivers, HotspotService hotspot, IHostApplicationLifetime lifetime)
{
    private const string Manifest = "nova-backup.json";
    private const string ConfigPrefix = "config/";
    private const string DriversFile = "drivers.txt";
    private const string HotspotFile = "hotspot.json";

    /// <summary>The folder a restore waits in until NOVA restarts.</summary>
    public const string Pending = "restore-pending";

    /// <summary>Folders in the settings folder that are caches or runtime state, not settings.</summary>
    private static readonly string[] NotSettings = ["sky", "survey-cache", "image-history", "file-thumbnails", "terminal", "keys", Pending];

    /// <summary>Folders a restore replaces as a whole (top-level .json files are always replaced).</summary>
    private static readonly string[] SettingsFolders = ["sequences", "guide-darks"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string Config => options.Value.ResolveConfigDirectory();

    public string FileName => $"nova-backup-{Environment.MachineName.ToLowerInvariant()}-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";

    public async Task WriteAsync(Stream output, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        async Task AddText(string name, string text)
        {
            await using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
            await writer.WriteAsync(text);
        }

        await AddText(Manifest, JsonSerializer.Serialize(new BackupInfo(StatusReporter.AppVersion, DateTime.UtcNow, Environment.MachineName), Json));
        if (Directory.Exists(Config))
            foreach (var file in Directory.EnumerateFiles(Config, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Config, file).Replace('\\', '/');
                if (NotSettings.Contains(relative.Split('/')[0], StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    zip.CreateEntryFromFile(file, ConfigPrefix + relative, CompressionLevel.Optimal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Backup: couldn't read {relative} ({ex.Message}); left out");
                }
            }
        if (drivers.Applicable) await AddText(DriversFile, string.Join('\n', drivers.SelectedDrivers(out _)) + "\n");
        try
        {
            await AddText(HotspotFile, JsonSerializer.Serialize(hotspot.Settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // no hotspot settings here (not Linux, say)
        }
    }

    /// <summary>
    /// Checks a backup and puts it aside for NOVA to swap in when it restarts. Returns what the backup is; throws
    /// <see cref="ArgumentException"/> for anything that isn't one of NOVA's backups.
    /// </summary>
    public async Task<BackupInfo> StageAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("That isn't a zip file: choose a backup NOVA made (nova-backup-….zip)");
        }
        using (zip)
        {
            var manifest = zip.GetEntry(Manifest) ?? throw new ArgumentException("That zip isn't one of NOVA's backups (it has no nova-backup.json)");
            BackupInfo info;
            await using (var stream = manifest.Open())
                info = await JsonSerializer.DeserializeAsync<BackupInfo>(stream, cancellationToken: ct)
                       ?? throw new ArgumentException("The backup's nova-backup.json is empty");

            var staging = Path.Combine(Config, Pending);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // a folder
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                // Only into the staging folder: a crafted name ("../..") mustn't write anywhere else.
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new ArgumentException($"The backup has a file outside its folders ({entry.FullName}): not restored");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            log.Info($"Restoring the backup of {info.Host} from {info.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} (NOVA {info.Version}): NOVA restarts to finish");
            return info;
        }
    }

    /// <summary>Whether NOVA runs as a service that restarts it (systemd), so a restore can finish by itself.</summary>
    public static bool CanRestartItself => Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 };

    /// <summary>Exits so the service restarts NOVA (it restarts on a failure exit code), after the reply has gone.</summary>
    public void RestartSoon()
    {
        if (!CanRestartItself) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Environment.ExitCode = 75; // "try again": not 0, so systemd restarts it
            lifetime.StopApplication();
        });
    }

    /// <summary>
    /// At startup, before any setting is read: swaps a restored backup's settings in. The settings it replaces are
    /// removed first, so the result is the backup's, not a mixture. Returns whether there was one.
    /// </summary>
    public static bool ApplyPendingFiles(string config)
    {
        var restored = Path.Combine(config, Pending, "config");
        if (!Directory.Exists(restored)) return false;
        foreach (var file in Directory.EnumerateFiles(config, "*.json")) File.Delete(file);
        foreach (var folder in SettingsFolders)
            if (Directory.Exists(Path.Combine(config, folder))) Directory.Delete(Path.Combine(config, folder), recursive: true);
        foreach (var file in Directory.EnumerateFiles(restored, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(config, Path.GetRelativePath(restored, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        Directory.Delete(restored, recursive: true);
        return true;
    }

    /// <summary>Once NOVA is up: the restored backup's driver list and hotspot settings, then the staging folder goes.</summary>
    public async Task ApplyPendingExtrasAsync(CancellationToken ct)
    {
        var staging = Path.Combine(Config, Pending);
        if (!Directory.Exists(staging)) return;
        try
        {
            var driversFile = Path.Combine(staging, DriversFile);
            if (File.Exists(driversFile) && drivers.Applicable)
            {
                var list = (await File.ReadAllLinesAsync(driversFile, ct)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                await drivers.ApplyAsync(list, ct);
            }
            var hotspotFile = Path.Combine(staging, HotspotFile);
            if (File.Exists(hotspotFile) && JsonSerializer.Deserialize<HotspotSettings>(await File.ReadAllTextAsync(hotspotFile, ct)) is { } settings)
            {
                try
                {
                    hotspot.UpdateSettings(settings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warn($"Restore: couldn't set the hotspot ({ex.Message})");
                }
            }
            var manifest = Path.Combine(staging, Manifest);
            var info = File.Exists(manifest) ? JsonSerializer.Deserialize<BackupInfo>(await File.ReadAllTextAsync(manifest, ct)) : null;
            log.Info(info is null ? "Settings restored from a backup" : $"Settings restored from {info.Host}'s backup of {info.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"Restore: the settings are back, but the drivers or hotspot couldn't be set ({ex.Message})");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
