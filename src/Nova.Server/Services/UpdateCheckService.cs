using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <summary>A release on GitHub: its version ("0.3.23"), page and notes (the changelog's section, Markdown).</summary>
public sealed record ReleaseInfo(string Version, string Url, string Notes, DateTime? PublishedUtc);

/// <summary>
/// The installed version against the newest release. Available: a newer release is out (never for a build made from
/// source, which has no release version to compare).
/// </summary>
public sealed record UpdateStatus(string Installed, bool DevBuild, bool Enabled, ReleaseInfo? Latest, bool Available,
    DateTime? CheckedUtc, string? Problem);

/// <summary>
/// Tells the page when a newer NOVA release is out: asks GitHub for the latest release once a day (and when asked),
/// and compares it with the installed version. It only tells: updating is still "nova update" on the mini PC.
/// With no internet (a dark site, the hotspot) the check fails quietly and is tried again within the hour. The daily
/// check can be turned off (updates.json in the settings folder); "Check now" still works then.
/// </summary>
public sealed class UpdateCheckService : BackgroundService
{
    public const string Repo = "geoffpedder/nova-astro";
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient Http = CreateClient();

    private readonly ActivityLog _log;
    private readonly string _file;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly object _sync = new();
    private bool _enabled = true;
    private ReleaseInfo? _latest;
    private DateTime? _checkedUtc;
    private string? _problem;
    private string? _announced;
    private CancellationTokenSource _wake = new();

    public UpdateCheckService(IOptions<NovaOptions> options, ActivityLog log)
    {
        _log = log;
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "updates.json");
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(_file), Json) is { } saved)
                _enabled = saved.Enabled;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // an unreadable file: the default (on)
        }
    }

    private sealed record Settings(bool Enabled);

    /// <summary>The installed version. Replaceable for tests.</summary>
    public string Installed { get; set; } = StatusReporter.AppVersion;

    /// <summary>GitHub's answer for the latest release (its JSON). Replaceable for tests.</summary>
    public Func<CancellationToken, Task<string>> Fetch { get; set; } =
        ct => Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);

    public UpdateStatus Status
    {
        get
        {
            lock (_sync)
            {
                var dev = IsDevBuild(Installed);
                return new UpdateStatus(Installed, dev, _enabled, _latest,
                    !dev && _latest is { } latest && IsNewer(latest.Version, Installed), _checkedUtc, _problem);
            }
        }
    }

    /// <summary>Turns the daily check on or off; turning it on checks now.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(new Settings(enabled), Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't save the update check setting: {e.Message}");
        }
        _log.Info(enabled ? "Checking for new NOVA releases daily" : "No longer checking for new NOVA releases");
        Wake();
    }

    /// <summary>Asks GitHub now. A failure keeps the last answer and says what went wrong.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        await _checking.WaitAsync(ct);
        try
        {
            ReleaseInfo? found = null;
            string? problem = null;
            try
            {
                found = Parse(await Fetch(ct));
                if (found is null) problem = "GitHub's answer had no release in it";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                problem = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? "No release has been published yet"
                    : $"Couldn't reach GitHub (no internet?): {e.Message}";
            }

            bool announce;
            lock (_sync)
            {
                _checkedUtc = DateTime.UtcNow;
                _problem = problem;
                if (found is not null) _latest = found;
                announce = found is not null && !IsDevBuild(Installed) && IsNewer(found.Version, Installed) && _announced != found.Version;
                if (announce) _announced = found!.Version;
            }
            if (announce) _log.Info($"NOVA {found!.Version} is out (this is {Installed}). To update, on the mini PC: nova update");
            return Status;
        }
        finally
        {
            _checking.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not at the very start: the network may still be coming up.
        var wait = TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationTokenSource wake;
            lock (_sync) wake = _wake;
            using (var either = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake.Token))
            {
                try { await Task.Delay(wait, either.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* the setting changed */ }
                catch (OperationCanceledException) { return; }
            }
            bool enabled;
            lock (_sync) enabled = _enabled;
            if (!enabled)
            {
                wait = Daily;
                continue;
            }
            try
            {
                wait = (await CheckAsync(stoppingToken)).Problem is null ? Daily : AfterFailure;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Wake()
    {
        CancellationTokenSource old;
        lock (_sync)
        {
            old = _wake;
            _wake = new CancellationTokenSource();
        }
        old.Cancel();
    }

    /// <summary>The release in GitHub's "latest release" answer, or null when it isn't one.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } name)
            return null;
        if (Numbers(name) is null) return null;
        string Text(string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var notes = Text("body");
        if (notes.Length > 20_000) notes = notes[..20_000];
        var url = Text("html_url");
        // Only ever a link to the project's own releases.
        if (!url.StartsWith($"https://github.com/{Repo}/", StringComparison.OrdinalIgnoreCase)) url = $"https://github.com/{Repo}/releases";
        DateTime? published = DateTime.TryParse(Text("published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
        return new ReleaseInfo(name.TrimStart('v', 'V'), url, notes, published);
    }

    /// <summary>A build made from source ("0.0.0-dev"): there's no release version to compare.</summary>
    public static bool IsDevBuild(string version) => Numbers(version) is not { } v || v == new Version(0, 0, 0);

    public static bool IsNewer(string candidate, string installed) =>
        Numbers(candidate) is { } c && Numbers(installed) is { } i && c > i;

    /// <summary>"v0.3.22", "0.3.22-rc1", "0.3.22+abc" → 0.3.22.</summary>
    private static Version? Numbers(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) text = text[..end];
        if (!Version.TryParse(text, out var parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API wants a User-Agent; nothing about this install is sent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("nova-update-check", "1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
