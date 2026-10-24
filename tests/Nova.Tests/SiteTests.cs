using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Indi;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class SiteTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task SendsLocationAndTimeToAnIndiMount()
    {
        string[] mount =
        [
            """<defTextVector device="ZWO AM3" name="DRIVER_INFO" state="Idle" perm="ro"><defText name="DRIVER_INTERFACE">5</defText></defTextVector>""",
            """<defSwitchVector device="ZWO AM3" name="CONNECTION" state="Ok" perm="rw" rule="OneOfMany"><defSwitch name="CONNECT">On</defSwitch><defSwitch name="DISCONNECT">Off</defSwitch></defSwitchVector>""",
            """<defNumberVector device="ZWO AM3" name="GEOGRAPHIC_COORD" group="Site Management" state="Ok" perm="rw"><defNumber name="LAT" format="%012.8m" min="-90" max="90" step="0">51.5</defNumber><defNumber name="LONG" format="%012.8m" min="0" max="360" step="0">358.2</defNumber><defNumber name="ELEV" format="%g" min="-200" max="10000" step="0">100</defNumber></defNumberVector>""",
            """<defTextVector device="ZWO AM3" name="TIME_UTC" group="Site Management" state="Idle" perm="rw"><defText name="UTC"></defText><defText name="OFFSET"></defText></defTextVector>""",
        ];
        using var server = new ScriptedServer(mount, _ => [], skipFirstLine: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var provider = new IndiEquipmentProvider("127.0.0.1", server.Port);
        await provider.ConnectAsync(cts.Token);
        var am3 = provider.GetMount("ZWO AM3")!;

        // INDI's 0-360° east longitude comes back as -180..180.
        Assert.Equal(new GeoLocation(51.5, -1.8, 100), am3.Location! with { Longitude = Math.Round(am3.Location!.Longitude, 6) });

        var now = new DateTimeOffset(2026, 10, 2, 21, 30, 15, TimeSpan.FromHours(1));
        await am3.SetSiteAsync(new GeoLocation(51.4779, -0.0015, 46), now, cts.Token);

        for (var i = 0; i < 40 && server.Received.Count(l => l.Contains("GEOGRAPHIC_COORD") || l.Contains("TIME_UTC")) < 2; i++)
            await Task.Delay(50);
        var location = server.Received.Single(l => l.Contains("GEOGRAPHIC_COORD"));
        Assert.Contains("\"LAT\">51.4779<", location);
        Assert.Contains("\"LONG\">359.9985<", location); // west longitude as INDI wants it
        Assert.Contains("\"ELEV\">46<", location);
        var time = server.Received.Single(l => l.Contains("TIME_UTC"));
        Assert.Contains("<oneText name=\"UTC\">2026-10-02T20:30:15</oneText>", time);
        Assert.Contains("<oneText name=\"OFFSET\">1</oneText>", time);
    }

    private (EquipmentService Equipment, SiteService Sites) Create()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        return (equipment, new SiteService(equipment, options, log));
    }

    [Fact]
    public async Task SendsTheActiveSiteOnConnectAndWhenItChanges()
    {
        var (equipment, sites) = Create();
        await sites.SaveAsync(new Site("Home", 51.4779, -0.0015, 46), null, CancellationToken.None);
        await sites.SaveAsync(new Site("Dark site", 55.2, -2.6, 300), null, CancellationToken.None);
        Assert.Equal("Dark site", sites.Status.Active); // saving makes it active

        await equipment.ConnectAsync(CancellationToken.None);
        await Task.Delay(200); // sent in the background after the connect
        Assert.Equal(new GeoLocation(55.2, -2.6, 300), equipment.Mount!.Location);
        Assert.False(sites.Status.MountMismatch);

        await sites.SelectAsync("Home", CancellationToken.None);
        Assert.Equal(51.4779, equipment.Mount!.Location!.Latitude);

        // Something else changes the mount's location: NOVA notices.
        await equipment.Mount!.SetSiteAsync(new GeoLocation(0, 0, 0), DateTimeOffset.Now, CancellationToken.None);
        Assert.True(sites.Status.MountMismatch);
    }

    [Fact]
    public async Task LetsTheMountSettleAfterEachGoTo()
    {
        var (equipment, sites) = Create();
        await sites.SaveAsync(new Site("Home", 48.5197, -123.4167, 300), null, CancellationToken.None);
        await sites.SetSunProtectionAsync(false, CancellationToken.None);
        var mount = new ForgetfulMount();
        await sites.SlewSafelyAsync(mount, new EquatorialCoordinates(18.6, 38.8), CancellationToken.None); // learns the site

        await sites.SetSettleSecondsAsync(1.5, CancellationToken.None);
        Assert.Equal(1.5, sites.Status.SettleSeconds);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await sites.SlewSafelyAsync(mount, new EquatorialCoordinates(19.0, 30.0), CancellationToken.None);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.4, 5);

        await sites.SetSettleSecondsAsync(0, CancellationToken.None);
        clock.Restart();
        await sites.SlewSafelyAsync(mount, new EquatorialCoordinates(19.5, 30.0), CancellationToken.None);
        Assert.True(clock.Elapsed.TotalSeconds < 1);

        await Assert.ThrowsAsync<ArgumentException>(() => sites.SetSettleSecondsAsync(-1, CancellationToken.None));
        GC.KeepAlive(equipment);
    }

    [Fact]
    public async Task ARefusedGoToSendsTheSiteAgainAndRetries()
    {
        var (equipment, sites) = Create();
        await sites.SaveAsync(new Site("Home", 48.5197, -123.4167, 300), null, CancellationToken.None);
        await sites.SetSunProtectionAsync(false, CancellationToken.None);
        var mount = new ForgetfulMount();

        await sites.SlewSafelyAsync(mount, new EquatorialCoordinates(18.6, 38.8), CancellationToken.None);

        Assert.Equal(2, mount.Slews);
        Assert.Equal(48.5197, mount.Location!.Latitude);
        GC.KeepAlive(equipment);
    }

    /// <summary>Like a ZWO mount just powered on: shows a location but refuses GoTos until it's sent one.</summary>
    private sealed class ForgetfulMount : IMount
    {
        private bool _told;
        public int Slews { get; private set; }
        public string Name => "ZWO AM5";
        public bool IsConnected => true;
        public MountState State => MountState.Idle;
        public EquatorialCoordinates Coordinates { get; private set; } = new(0, 89);
        public PierSide? SideOfPier => null;
        public GeoLocation? Location { get; private set; } = new(48.5197, -123.4167, 0);
        public DateTime? ClockUtc => DateTime.UtcNow;
        public IReadOnlyList<SlewRate> SlewRates => [];
        public string? CurrentSlewRate => null;
        public bool CanMoveRaAxis => false;

        public Task SetSiteAsync(GeoLocation location, DateTimeOffset now, CancellationToken ct)
        {
            (Location, _told) = (location, true);
            return Task.CompletedTask;
        }

        public Task SlewAsync(EquatorialCoordinates target, CancellationToken ct)
        {
            Slews++;
            if (!_told) throw new InvalidOperationException("ZWO AM5 refused the slew");
            Coordinates = target;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SetClockAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task SetSlewRateAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task StartMotionAsync(MotionDirection direction, CancellationToken ct) => Task.CompletedTask;
        public Task StopMotionAsync(MotionDirection? direction, CancellationToken ct) => Task.CompletedTask;
        public Task MoveRaAxisAsync(double degrees, CancellationToken ct) => Task.CompletedTask;
        public Task SyncAsync(EquatorialCoordinates actual, CancellationToken ct) => Task.CompletedTask;
        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ParkAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnparkAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ValidatesRenamesDeletesAndRemembers()
    {
        var (_, sites) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => sites.SaveAsync(new Site("X", 91, 0, 0), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => sites.SaveAsync(new Site("X", 0, 200, 0), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => sites.SaveAsync(new Site(" ", 0, 0, 0), null, CancellationToken.None));

        await sites.SaveAsync(new Site("Home", 51.5, -0.1, 40), null, CancellationToken.None);
        await sites.SaveAsync(new Site("Field", 52, -1, 120), null, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => sites.SaveAsync(new Site("home", 1, 1, 1), "Field", CancellationToken.None));
        await sites.SaveAsync(new Site("Back garden", 51.5, -0.1, 42), "Home", CancellationToken.None);
        await sites.DeleteAsync("Back garden", CancellationToken.None);

        var (_, reloaded) = Create();
        Assert.Equal(["Field"], reloaded.Status.Sites.Select(s => s.Name));
        Assert.Equal("Field", reloaded.Status.Active);
    }

    [Theory]
    [InlineData(51.4779, -0.0015, "51.4779°N 0.0015°W, 46 m")]
    [InlineData(-33.8688, 151.2093, "33.8688°S 151.2093°E, 46 m")]
    public void DescribesLocations(double lat, double lon, string expected) =>
        Assert.Equal(expected, SiteService.Describe(new GeoLocation(lat, lon, 46)));

    [Fact]
    public void ComparesAcrossTheDateLine() =>
        Assert.True(new GeoLocation(10, 179.99, 0).IsNear(new GeoLocation(10, -179.99, 0)));
}
