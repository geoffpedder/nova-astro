using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Nova.Server.Services;

/// <param name="Kind">"wifi" or "wired".</param>
/// <param name="Mode">For Wi-Fi: "client" (joined a network) or "hotspot" (the mini PC is the access point).</param>
/// <param name="SignalPercent">Wi-Fi signal strength, 0–100, when known.</param>
/// <param name="Gateway">Whether it has a default route (the way to the router): the interface actually in use.</param>
public sealed record NetworkInterfaceInfo(
    string Name, string Kind, string? Mode, string? Ssid, int? SignalPercent, IReadOnlyList<string> Addresses, bool Gateway = false);

/// <param name="Primary">The connection to show in the header: a hotspot first, then the one with the route to the
/// router (Wi-Fi before wired), then any joined Wi-Fi.</param>
public sealed record NetworkStatus(NetworkInterfaceInfo? Primary, IReadOnlyList<NetworkInterfaceInfo> Interfaces, string? Hostname);

/// <summary>
/// How the mini PC is connected — which Wi-Fi network (and how strong), or the hotspot it's running, or a cable
/// — refreshed every 15 seconds. Addresses come from .NET; the Wi-Fi details from whichever tool is there:
/// NetworkManager (nmcli), iwd (iwctl) or iw (on Windows, netsh, for development).
/// </summary>
public sealed partial class NetworkService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    public NetworkStatus Status { get; private set; } = new(null, [], null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Status = await ReadAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // keep the last reading
            }
            await Task.Delay(Every, stoppingToken);
        }
    }

    /// <summary>Reads it now (the hotspot fallback deciding), and keeps it for the header too.</summary>
    public async Task<NetworkStatus> RefreshAsync(CancellationToken ct) => Status = await ReadAsync(ct);

    public static async Task<NetworkStatus> ReadAsync(CancellationToken ct)
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var properties = nic.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            var gateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
            if (addresses.Count == 0) continue;
            // Containers and VPN-ish virtual links aren't how the PC is reached.
            if (nic.Name.StartsWith("docker") || nic.Name.StartsWith("veth") || nic.Name.StartsWith("br-") || nic.Name.StartsWith("virbr")) continue;

            var wifi = OperatingSystem.IsLinux()
                ? Directory.Exists($"/sys/class/net/{nic.Name}/wireless")
                : nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var info = new NetworkInterfaceInfo(nic.Name, wifi ? "wifi" : "wired", null, null, null, addresses, gateway);
            if (wifi) info = await WithWifiDetailsAsync(info, ct);
            interfaces.Add(info);
        }

        var primary = interfaces.FirstOrDefault(i => i.Mode == "hotspot")
                      ?? interfaces.FirstOrDefault(i => i.Gateway && i.Kind == "wifi")
                      ?? interfaces.FirstOrDefault(i => i.Gateway)
                      ?? interfaces.FirstOrDefault(i => i.Kind == "wifi" && i.Ssid is not null)
                      ?? interfaces.FirstOrDefault();
        return new NetworkStatus(primary, interfaces, Environment.MachineName);
    }

    private static async Task<NetworkInterfaceInfo> WithWifiDetailsAsync(NetworkInterfaceInfo info, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await RunAsync("netsh", "wlan show interfaces", ct) is { } netsh ? Apply(info, ParseNetsh(netsh)) : info;

        // Hotspot? iw says so for every manager ("type AP").
        if (await RunAsync("iw", $"dev {info.Name} info", ct) is { } iwInfo && ParseIwInfo(iwInfo) is { Mode: "hotspot" } ap)
            return Apply(info, ap);

        if (await RunAsync("nmcli", $"-t -f IN-USE,SSID,SIGNAL dev wifi list ifname {info.Name} --rescan no", ct) is { } nm
            && ParseNmcli(nm) is { Ssid: not null } joined)
            return Apply(info, joined);
        if (await RunAsync("iwctl", $"station {info.Name} show", ct) is { } iwd && ParseIwctl(iwd) is { Ssid: not null } station)
            return Apply(info, station);
        if (await RunAsync("iw", $"dev {info.Name} link", ct) is { } link && ParseIwLink(link) is { Ssid: not null } linked)
            return Apply(info, linked);
        return info;
    }

    public sealed record WifiReading(string? Mode, string? Ssid, int? SignalPercent);

    private static NetworkInterfaceInfo Apply(NetworkInterfaceInfo info, WifiReading? r) =>
        r is null ? info : info with { Mode = r.Mode ?? info.Mode, Ssid = r.Ssid ?? info.Ssid, SignalPercent = r.SignalPercent ?? info.SignalPercent };

    /// <summary>dBm to a rough 0–100 % (-100 dBm ≈ 0, -50 dBm or better ≈ 100), as NetworkManager does.</summary>
    public static int DbmToPercent(double dbm) => (int)Math.Clamp(Math.Round((dbm + 100) * 2), 0, 100);

    /// <summary><c>nmcli -t -f IN-USE,SSID,SIGNAL dev wifi list</c>: the line marked "*" is the joined network.</summary>
    public static WifiReading? ParseNmcli(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            // Terse fields are ':'-separated, with ':' in values escaped as "\:".
            var fields = Regex.Split(line.TrimEnd('\r'), @"(?<!\\):").Select(f => f.Replace("\\:", ":")).ToArray();
            if (fields.Length >= 3 && fields[0] == "*" && fields[1].Length > 0)
                return new WifiReading("client", fields[1], int.TryParse(fields[2], out var signal) ? signal : null);
        }
        return null;
    }

    /// <summary><c>iwctl station wlan0 show</c> (iwd), colour codes and all.</summary>
    public static WifiReading? ParseIwctl(string output)
    {
        var text = Ansi().Replace(output, "");
        string? Value(string property) =>
            Regex.Match(text, $@"^\s*{Regex.Escape(property)}\s{{2,}}(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
        if (Value("State") is { } state && state != "connected") return null;
        var ssid = Value("Connected network");
        int? signal = Value("RSSI") is { } rssi && Regex.Match(rssi, @"-?\d+") is { Success: true } d ? DbmToPercent(double.Parse(d.Value)) : null;
        return ssid is null ? null : new WifiReading("client", ssid, signal);
    }

    /// <summary><c>iw dev wlan0 link</c>: "SSID: …" and "signal: -52 dBm".</summary>
    public static WifiReading? ParseIwLink(string output)
    {
        if (output.Contains("Not connected")) return null;
        var ssid = Regex.Match(output, @"^\s*SSID:\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } s ? s.Groups[1].Value : null;
        int? signal = Regex.Match(output, @"signal:\s*(-?\d+)") is { Success: true } d ? DbmToPercent(double.Parse(d.Groups[1].Value)) : null;
        return ssid is null ? null : new WifiReading("client", ssid, signal);
    }

    /// <summary><c>iw dev wlan0 info</c>: "type AP" means the mini PC is running a hotspot called "ssid …".</summary>
    public static WifiReading? ParseIwInfo(string output)
    {
        var type = Regex.Match(output, @"^\s*type\s+(\S+)", RegexOptions.Multiline) is { Success: true } t ? t.Groups[1].Value : null;
        if (type != "AP") return null;
        var ssid = Regex.Match(output, @"^\s*ssid\s+(.+?)\s*$", RegexOptions.Multiline) is { Success: true } s ? s.Groups[1].Value : null;
        return new WifiReading("hotspot", ssid, null);
    }

    /// <summary><c>netsh wlan show interfaces</c> (Windows, for trying NOVA out on a PC).</summary>
    public static WifiReading? ParseNetsh(string output)
    {
        string? Value(string key) =>
            Regex.Match(output, $@"^\s*{key}\s*:\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
        if (Value("State") is { } state && !state.Equals("connected", StringComparison.OrdinalIgnoreCase)) return null;
        var ssid = Value("SSID");
        int? signal = Value("Signal") is { } sig && int.TryParse(sig.TrimEnd('%'), out var pct) ? pct : null;
        return ssid is null ? null : new WifiReading("client", ssid, signal);
    }

    /// <summary>Runs a tool and returns its output, or null if it isn't there or fails.</summary>
    private static async Task<string?> RunAsync(string file, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null; // not installed, or stuck
        }
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();
}
