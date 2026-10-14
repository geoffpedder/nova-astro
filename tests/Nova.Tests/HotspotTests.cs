using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class HotspotTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-hotspot-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc);
    private NetworkStatus _network = Offline;
    private readonly SimulatedHotspot _control = new();

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly NetworkStatus Offline = new(null, [new NetworkInterfaceInfo("wlan0", "wifi", null, null, null, ["169.254.3.4"])], "astro");
    private static readonly NetworkStatus OnWifi = new(null, [new NetworkInterfaceInfo("wlan0", "wifi", "client", "Home", 70, ["192.168.1.20"], true)], "astro");
    private static readonly NetworkStatus Wired = new(null, [new NetworkInterfaceInfo("eth0", "wired", null, null, null, ["192.168.1.21"], true)], "astro");

    private HotspotService Service(bool configured = true)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        Directory.CreateDirectory(_folder);
        var hotspot = new HotspotService(options, log)
        {
            Control = _control,
            Now = () => _now,
            ReadNetwork = _ => Task.FromResult(_network),
        };
        if (configured) hotspot.UpdateSettings(new HotspotSettings("NOVA-astro", "dark skies"));
        return hotspot;
    }

    private async Task After(HotspotService hotspot, TimeSpan time)
    {
        // Ticks every 10 s, as the service does.
        for (var t = TimeSpan.Zero; t < time; t += TimeSpan.FromSeconds(10))
        {
            _now += TimeSpan.FromSeconds(10);
            await hotspot.TickAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartsWhenNoWifiIsFoundForAWhile()
    {
        var hotspot = Service();
        await hotspot.TickAsync(CancellationToken.None);
        Assert.Equal("Off", hotspot.Status.State);
        Assert.Equal(90, hotspot.Status.StartsInSeconds);

        await After(hotspot, TimeSpan.FromSeconds(60));
        Assert.False(_control.Active);
        await After(hotspot, TimeSpan.FromSeconds(40));
        Assert.True(_control.Active);
        Assert.Equal(("On", "No Wi-Fi found", false), (hotspot.Status.State, hotspot.Status.Reason, hotspot.Status.Manual));
    }

    [Fact]
    public async Task StaysOffOnWifiOrACableOrWithoutAPasswordOrTurnedOff()
    {
        var hotspot = Service();
        _network = OnWifi;
        await After(hotspot, TimeSpan.FromMinutes(5));
        _network = Wired;
        await After(hotspot, TimeSpan.FromMinutes(5));
        Assert.False(_control.Active);
        Assert.Null(hotspot.Status.StartsInSeconds);

        _network = Offline;
        hotspot.UpdateSettings(hotspot.Settings with { Fallback = false });
        await After(hotspot, TimeSpan.FromMinutes(5));
        Assert.False(_control.Active);

        File.Delete(Path.Combine(_folder, "hotspot.json")); // a fresh install: no password yet
        var unset = Service(configured: false);
        await After(unset, TimeSpan.FromMinutes(5));
        Assert.False(_control.Active);
        Assert.False(unset.Status.Configured);
    }

    [Fact]
    public async Task TriesWifiAgainOnlyWhileNobodyIsOnIt()
    {
        var hotspot = Service();
        await After(hotspot, TimeSpan.FromSeconds(100));
        Assert.True(_control.Active);

        // A phone on it: it stays up however long.
        _control.Stations = 1;
        await After(hotspot, TimeSpan.FromMinutes(20));
        Assert.True(_control.Active);
        Assert.Equal(1, hotspot.Status.Stations);

        // The phone leaves: 5 minutes later it tries Wi-Fi.
        _control.Stations = 0;
        await After(hotspot, TimeSpan.FromMinutes(4));
        Assert.True(_control.Active);
        await After(hotspot, TimeSpan.FromMinutes(1.1));
        Assert.False(_control.Active);

        // Still no Wi-Fi: back on 45 s after that's seen.
        await After(hotspot, TimeSpan.FromSeconds(40));
        Assert.False(_control.Active);
        await After(hotspot, TimeSpan.FromSeconds(20));
        Assert.True(_control.Active);
        Assert.Equal("Wi-Fi still not found", hotspot.Status.Reason);

        // Next time Wi-Fi is there: it stays on Wi-Fi.
        await After(hotspot, TimeSpan.FromMinutes(5.1));
        Assert.False(_control.Active);
        _network = OnWifi;
        await After(hotspot, TimeSpan.FromMinutes(10));
        Assert.False(_control.Active);
    }

    [Fact]
    public async Task AManualStartNobodyJoinsGoesBackToWifi()
    {
        _network = OnWifi;
        var hotspot = Service();
        await hotspot.StartHotspotAsync(CancellationToken.None);
        Assert.True(_control.Active);
        Assert.True(hotspot.Status.Manual);
        await After(hotspot, TimeSpan.FromMinutes(2.5));
        Assert.True(_control.Active);
        await After(hotspot, TimeSpan.FromMinutes(0.6));
        Assert.False(_control.Active);
        await After(hotspot, TimeSpan.FromMinutes(10));
        Assert.False(_control.Active); // Wi-Fi's there: stays off

        // Joined, then the page says Back to Wi-Fi.
        await hotspot.StartHotspotAsync(CancellationToken.None);
        _control.Stations = 1;
        await After(hotspot, TimeSpan.FromMinutes(10));
        Assert.True(_control.Active);
        await hotspot.StopHotspotAsync(CancellationToken.None);
        Assert.False(_control.Active);
        Assert.Equal("Off", hotspot.Status.State);
    }

    [Fact]
    public async Task AFailedStartIsReportedAndTriedAgainLater()
    {
        var hotspot = Service();
        _control.FailWith = "hostapd: nl80211 driver initialization failed";
        await After(hotspot, TimeSpan.FromSeconds(100));
        Assert.Equal("Error", hotspot.Status.State);
        Assert.Contains("nl80211", hotspot.Status.Error);

        _control.FailWith = null;
        await After(hotspot, TimeSpan.FromSeconds(60));
        Assert.False(_control.Active); // a pause after a failure
        await After(hotspot, TimeSpan.FromSeconds(70));
        Assert.True(_control.Active);

        // It stops by itself (hostapd crashing): reported, not straight back on.
        _control.Active = false;
        await After(hotspot, TimeSpan.FromSeconds(10));
        Assert.Equal("Error", hotspot.Status.State);
        Assert.False(_control.Active);
    }

    [Fact]
    public async Task ChecksAndSavesTheSettings()
    {
        var hotspot = Service(configured: false);
        Assert.StartsWith("NOVA-", hotspot.Settings.Ssid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hotspot.StartHotspotAsync(CancellationToken.None));

        Assert.Throws<ArgumentException>(() => hotspot.UpdateSettings(new HotspotSettings("NOVA", "short")));
        Assert.Throws<ArgumentException>(() => hotspot.UpdateSettings(new HotspotSettings("", "long enough")));
        Assert.Throws<ArgumentException>(() => hotspot.UpdateSettings(new HotspotSettings(new string('x', 33), "long enough")));
        Assert.Throws<ArgumentException>(() => hotspot.UpdateSettings(new HotspotSettings("NOVA", "line\nbreak here")));
        Assert.Throws<ArgumentException>(() => hotspot.UpdateSettings(new HotspotSettings("Stjärna", "long enough")));

        hotspot.UpdateSettings(new HotspotSettings(" Scope Wi-Fi ", "dark sky=good", Fallback: true, Retry: false));
        Assert.Equal("SSID=Scope Wi-Fi\nPASSPHRASE=dark sky=good", File.ReadAllText(hotspot.ConfFile).Split('\n', 2)[1].TrimEnd());

        var again = Service(configured: false);
        Assert.Equal(new HotspotSettings("Scope Wi-Fi", "dark sky=good", true, false), again.Settings);
    }

    [Fact]
    public void ConnectedMeansJoinedWifiOrACableWithARoute()
    {
        Assert.False(HotspotService.Connected(Offline));
        Assert.True(HotspotService.Connected(OnWifi));
        Assert.True(HotspotService.Connected(Wired));
        Assert.False(HotspotService.Connected(new NetworkStatus(null, [new NetworkInterfaceInfo("eth0", "wired", null, null, null, ["10.0.0.2"])], null)));
        Assert.False(HotspotService.Connected(new NetworkStatus(null, [new NetworkInterfaceInfo("wlan0", "wifi", "hotspot", "NOVA-astro", null, ["10.42.0.1"])], null)));
    }
}
