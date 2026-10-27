using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>
/// One zip with what's needed to find out what went wrong on a night: NOVA's activity log and its service log, the
/// INDI drivers' log, the kernel's USB messages (devices reset or dropped), <c>nova doctor</c>, the system and NOVA's
/// settings. Nothing private: the password file is left out, and passwords, tokens, the Bluetooth bind code and the
/// sites' coordinates are blanked.
/// </summary>
public sealed partial class DiagnosticsService(ActivityLog log, IOptions<NovaOptions> options)
{
    /// <summary>How far back the logs go.</summary>
    public const string Since = "36 hours ago";

    /// <summary>Runs a command and returns what it printed (replaceable in tests).</summary>
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> Run { get; set; } = RunAsync;

    private static readonly string[] SecretKeys = ["password", "passphrase", "token", "secret", "code", "hash", "salt", "latitude", "longitude", "elevation"];
    private static readonly string[] LeftOut = ["auth.json"];

    public string FileName => $"nova-diagnostics-{Environment.MachineName.ToLowerInvariant()}-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";

    public async Task WriteAsync(Stream output, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        Add("README.txt",
            $"NOVA {StatusReporter.AppVersion} on {Environment.MachineName}, {DateTime.Now:yyyy-MM-dd HH:mm zzz}\n\n" +
            "activity.txt        NOVA's activity log, as the Mini PC tab shows it (since NOVA last started)\n" +
            "nova.log            NOVA's own log, the last 36 hours\n" +
            "indiserver.log      the INDI drivers' log, the last 36 hours\n" +
            "kernel-usb.log      the kernel's USB messages, this boot and the one before (devices reset or dropped)\n" +
            "doctor.txt          nova doctor\n" +
            "system.txt          the system: OS, uptime, disks, memory, USB devices\n" +
            "settings/           NOVA's settings, with passwords, tokens and the sites' coordinates blanked\n");

        Add("activity.txt", string.Join('\n', log.Recent.Select(e => $"{e.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {e.Level,-5} {e.Message}")) + "\n");

        if (OperatingSystem.IsLinux())
        {
            Add("nova.log", await Try("journalctl", ["-u", "nova", "--since", Since, "--no-pager", "-o", "short-iso", "-n", "30000"], ct));
            Add("indiserver.log", await Try("journalctl", ["-u", "indiserver", "--since", Since, "--no-pager", "-o", "short-iso", "-n", "30000"], ct));
            var kernel = new StringBuilder();
            foreach (var boot in new[] { "-1", "0" })
            {
                var text = await Try("journalctl", ["-k", "-b", boot, "--no-pager", "-o", "short-iso"], ct);
                kernel.Append($"--- boot {boot} ---\n");
                foreach (var line in text.Split('\n').Where(l => UsbLine().IsMatch(l))) kernel.Append(line).Append('\n');
            }
            Add("kernel-usb.log", kernel.ToString());
            Add("doctor.txt", await Try(DoctorScript(), [], ct, TimeSpan.FromSeconds(60)));
            var system = new StringBuilder();
            foreach (var (command, args) in new (string, string[])[]
            {
                ("uname", ["-a"]), ("cat", ["/etc/os-release"]), ("uptime", []), ("df", ["-h"]), ("free", ["-m"]),
                ("lsusb", []), ("lsusb", ["-t"]),
            })
                system.Append($"$ {command} {string.Join(' ', args)}\n").Append(await Try(command, args, ct)).Append('\n');
            Add("system.txt", system.ToString());
        }

        var config = options.Value.ResolveConfigDirectory();
        if (Directory.Exists(config))
            foreach (var file in Directory.EnumerateFiles(config, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(config, file).Replace('\\', '/');
                if (LeftOut.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    Add($"settings/{name}", Redact(await File.ReadAllTextAsync(file, ct)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Add($"settings/{name}.error.txt", ex.Message);
                }
            }
    }

    /// <summary>The JSON with every secret (and every coordinate) blanked; anything unreadable is left out.</summary>
    public static string Redact(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return "(not JSON: left out)\n";
        }
        Blank(root);
        return root?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null";

        static void Blank(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        if (SecretKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase))) o[key] = "(blanked)";
                        else Blank(o[key]);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Blank(item);
                    break;
            }
        }
    }

    private static string DoctorScript()
    {
        foreach (var path in new[] { "/opt/nova/scripts/doctor.sh", Path.Combine(AppContext.BaseDirectory, "doctor.sh") })
            if (File.Exists(path)) return path;
        return "/opt/nova/scripts/doctor.sh";
    }

    private async Task<string> Try(string command, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            return await Run(command, args, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"({command} took too long)\n";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"({command}: {ex.Message})\n";
        }
    }

    private static async Task<string> RunAsync(string command, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        info.Environment["NO_COLOR"] = "1";
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("didn't start");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await output;
        var err = await errors;
        return err.Length > 0 ? text + "\n(stderr)\n" + err : text;
    }

    [GeneratedRegex(@"usb|xhci|reset|disconnect|over-current|hub ", RegexOptions.IgnoreCase)]
    private static partial Regex UsbLine();
}
