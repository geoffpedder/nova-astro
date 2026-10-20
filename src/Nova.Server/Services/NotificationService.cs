using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nova.Core.Guiding;

namespace Nova.Server.Services;

/// <param name="Server">The ntfy server: https://ntfy.sh, or your own.</param>
/// <param name="Topic">The topic your phone's ntfy app subscribes to. Anyone who knows it can read the messages, so
/// it should be hard to guess (NOVA suggests a random one).</param>
/// <param name="Token">An access token, for a server or topic that needs one (tk_…); null for none.</param>
/// <param name="GuidingLostMinutes">How long PHD2 may have lost its star, during a sequence, before you're told.</param>
public sealed record NotificationSettings(
    bool Enabled = false, string Server = "https://ntfy.sh", string Topic = "", string? Token = null,
    bool SequenceDone = true, bool SequenceFailed = true, bool TargetSkipped = true, bool NeedsYou = true,
    bool GuidingLost = true, double GuidingLostMinutes = 3, bool Storage = true, bool MeridianFlip = true, bool Ready = true);

/// <summary>What the page sees of the settings: the token itself never goes back to a browser.</summary>
public sealed record NotificationSettingsView(
    bool Enabled, string Server, string Topic, bool HasToken, bool SequenceDone, bool SequenceFailed, bool TargetSkipped,
    bool NeedsYou, bool GuidingLost, double GuidingLostMinutes, string? LastError, bool Storage = true, bool MeridianFlip = true,
    bool Ready = true);

public enum NotificationKind { SequenceDone, SequenceFailed, TargetSkipped, NeedsYou, GuidingLost, Test, Storage, MeridianFlip, Ready }

/// <summary>
/// Messages to your phone through ntfy (https://ntfy.sh): a free app that shows whatever is posted to a topic you
/// subscribe to. NOVA posts when a sequence finishes or fails, when a target is skipped because something went
/// wrong, when it's waiting for you (flat panel, turning the camera), and when guiding has lost its star for a few
/// minutes during a sequence. Settings live in notifications.json; a message that can't be sent is only logged,
/// so a missing internet connection never disturbs imaging.
/// </summary>
public sealed class NotificationService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly ActivityLog _log;
    private readonly IServiceProvider _services;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private NotificationSettings _settings;
    private string? _lastError;

    public NotificationService(IOptions<NovaOptions> options, ActivityLog log, IServiceProvider services, HttpClient? http = null)
    {
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "notifications.json");
        _log = log;
        _services = services;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _settings = Load();
    }

    public NotificationSettings Settings
    {
        get { lock (_sync) return _settings; }
    }

    public NotificationSettingsView View
    {
        get
        {
            var s = Settings;
            return new(s.Enabled, s.Server, s.Topic, !string.IsNullOrEmpty(s.Token), s.SequenceDone, s.SequenceFailed,
                s.TargetSkipped, s.NeedsYou, s.GuidingLost, s.GuidingLostMinutes, _lastError, s.Storage, s.MeridianFlip, s.Ready);
        }
    }

    /// <summary>Saves new settings. <paramref name="token"/>: null keeps the current one, "" removes it.</summary>
    public void Save(NotificationSettings settings, string? token)
    {
        var server = settings.Server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The server must be a web address, like https://ntfy.sh");
        var topic = settings.Topic.Trim();
        if (settings.Enabled && (topic.Length is < 1 or > 64 || !topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("The topic can only have letters, digits, - and _ (up to 64)");
        if (settings.GuidingLostMinutes is < 1 or > 60) throw new ArgumentException("Guiding lost: between 1 and 60 minutes");

        lock (_sync)
        {
            _settings = settings with
            {
                Server = server, Topic = topic,
                Token = token is null ? _settings.Token : token.Trim() is { Length: > 0 } t ? t : null,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_settings, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _log.Info(settings.Enabled ? $"Notifications on, to {server}/{topic}" : "Notifications off");
    }

    /// <summary>Sends a message if notifications are on and that kind is wanted. Never throws.</summary>
    public void Notify(NotificationKind kind, string title, string message) => _ = SendAsync(kind, title, message, CancellationToken.None);

    /// <summary>Sends a message; for a test it reports what went wrong instead of only logging it.</summary>
    public async Task<string?> SendAsync(NotificationKind kind, string title, string message, CancellationToken ct)
    {
        var s = Settings;
        var wanted = kind switch
        {
            NotificationKind.SequenceDone => s.SequenceDone,
            NotificationKind.SequenceFailed => s.SequenceFailed,
            NotificationKind.TargetSkipped => s.TargetSkipped,
            NotificationKind.NeedsYou => s.NeedsYou,
            NotificationKind.GuidingLost => s.GuidingLost,
            NotificationKind.Storage => s.Storage,
            NotificationKind.MeridianFlip => s.MeridianFlip,
            NotificationKind.Ready => s.Ready,
            _ => true,
        };
        if ((!s.Enabled && kind != NotificationKind.Test) || !wanted || string.IsNullOrEmpty(s.Topic)) return null;

        // ntfy's JSON form (posted to the server itself) carries any text, unlike its HTTP headers.
        var (priority, tags) = kind switch
        {
            NotificationKind.SequenceFailed or NotificationKind.GuidingLost or NotificationKind.Storage => (4, new[] { "warning" }),
            NotificationKind.NeedsYou => (4, new[] { "bell" }),
            NotificationKind.TargetSkipped => (3, new[] { "fast_forward" }),
            NotificationKind.MeridianFlip => (3, new[] { "arrows_counterclockwise" }),
            NotificationKind.Ready => (3, new[] { "white_check_mark" }),
            NotificationKind.SequenceDone => (3, new[] { "telescope" }),
            _ => (3, new[] { "telescope" }),
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, s.Server + "/")
            {
                // Sent whole, with its length: some servers and proxies don't take chunked bodies.
                Content = new StringContent(JsonSerializer.Serialize(new { topic = s.Topic, title, message, priority, tags }),
                    System.Text.Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(s.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{s.Server} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            _lastError = null;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _lastError = $"{DateTime.Now:HH:mm}: {ex.Message}";
            _log.Warn($"Couldn't send a notification ({title}): {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Watches guiding during sequences: PHD2 without its star for a few minutes means clouds, or worse.</summary>
    /// <summary>NOVA started this soon after the mini PC did: a boot (later, it's NOVA restarting, after an update).</summary>
    public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the mini PC has been up (replaceable in tests).</summary>
    public Func<TimeSpan> Uptime { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// After a boot: once the network's up, tells the phone the mini PC is ready, with the address to open. Not after
    /// NOVA alone restarts (an update), nor on the hotspot (no internet to send it through).
    /// </summary>
    public async Task AnnounceReadyAsync(TimeSpan settle, CancellationToken ct)
    {
        if (Uptime() > BootWindow) return;
        await Task.Delay(settle, ct); // INDI and the equipment starting, the network coming up
        var network = _services.GetService<NetworkService>();
        for (var i = 0; i < 20; i++)
        {
            if (!Settings.Enabled || !Settings.Ready) return;
            var status = network is null ? null : await network.RefreshAsync(ct);
            if (status is null || HotspotService.Connected(status))
            {
                var host = status?.Hostname ?? Environment.MachineName;
                var address = status?.Primary?.Addresses.FirstOrDefault();
                var minutes = Math.Max(1, (int)Math.Round(Uptime().TotalMinutes));
                await SendAsync(NotificationKind.Ready, "Mini PC ready",
                    $"NOVA is up, {minutes} min after the mini PC started. Open http://{host.ToLowerInvariant()}.local:5000" +
                    (address is null ? "" : $" (or http://{address}:5000)") + ".", ct);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // no network yet (or only the hotspot): look again
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = AnnounceReadyAsync(TimeSpan.FromSeconds(20), stoppingToken);
        DateTime? lostSince = null;
        var told = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var sequencer = _services.GetService<SequencerService>();
            var guider = _services.GetService<GuiderService>();
            if (sequencer is null || guider is null) continue;
            var lost = sequencer.Running && guider.Status.State == GuiderState.LostLock;
            if (!lost)
            {
                if (told) Notify(NotificationKind.GuidingLost, "Guiding is back", "PHD2 found its star again.");
                (lostSince, told) = (null, false);
                continue;
            }
            lostSince ??= DateTime.UtcNow;
            var minutes = Settings.GuidingLostMinutes;
            if (!told && DateTime.UtcNow - lostSince >= TimeSpan.FromMinutes(minutes))
            {
                told = true;
                Notify(NotificationKind.GuidingLost, "Guiding lost its star",
                    $"PHD2 has had no guide star for {minutes:0} minutes (clouds?). The sequence carries on and restarts guiding when it can.");
            }
        }
    }

    private NotificationSettings Load()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<NotificationSettings>(File.ReadAllText(_file), Json) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return new();
    }
}
