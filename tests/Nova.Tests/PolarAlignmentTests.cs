using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Simulation;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class HorizonTests
{
    [Fact]
    public void SiderealTimeMatchesMeeus()
    {
        // GMST at J2000.0 (2000-01-01 12:00 UT) is 280.46061837°; Meeus example 12.a: 1987-04-10 0h UT → 13h10m46.3668s.
        Assert.Equal(280.46061837, Horizon.LocalSiderealDegrees(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc), 0), 6);
        Assert.Equal((13 + 10 / 60.0 + 46.3668 / 3600) * 15,
            Horizon.LocalSiderealDegrees(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc), 0), 4);
    }

    [Fact]
    public void PoleAndRoundTrip()
    {
        var site = new GeoLocation(51.5, -0.1, 0);
        var utc = new DateTime(2026, 10, 2, 22, 0, 0, DateTimeKind.Utc);
        var (alt, az) = Horizon.AltAz(Horizon.ToVector(new EquatorialCoordinates(3, 90), utc, site));
        Assert.Equal(51.5, alt, 6);
        Assert.True(az < 1e-6 || az > 360 - 1e-6);

        var position = new EquatorialCoordinates(20.69, 45.28); // Deneb
        var back = Horizon.FromVector(Horizon.ToVector(position, utc, site), utc, site);
        Assert.Equal(position.RaHours, back.RaHours, 9);
        Assert.Equal(position.DecDegrees, back.DecDegrees, 9);

        // On the meridian (hour angle 0) a star is due south of the zenith at altitude 90 - lat + dec.
        var meridian = new EquatorialCoordinates(Horizon.LocalSiderealDegrees(utc, site.Longitude) / 15, 10);
        var (a2, z2) = Horizon.AltAz(Horizon.ToVector(meridian, utc, site));
        Assert.Equal(90 - 51.5 + 10, a2, 6);
        Assert.Equal(180, z2, 6);
    }
}

public class PolarAlignmentMathTests
{
    /// <summary>A tracking mount whose axis is off the pole: where the camera really points (as a J2000 solve would say).</summary>
    private static PolarSample Observe(GeoLocation site, EquatorialCoordinates mountJNow, DateTime utc, double altArcmin, double azArcmin)
    {
        var pole = Horizon.AltAz(Horizon.VisiblePole(site));
        var turn = azArcmin / 60 / Math.Cos(pole.Altitude * Math.PI / 180) * Math.PI / 180;
        var v = Horizon.KnobRotation(Horizon.ToVector(mountJNow, utc, site), pole.Azimuth, altArcmin / 60 * Math.PI / 180, turn);
        return new PolarSample(Precession.JNowToJ2000(Horizon.FromVector(v, utc, site), utc), utc);
    }

    [Theory]
    [InlineData(51.5, -0.1, 12, -9)]
    [InlineData(51.5, -0.1, -30, 25)]
    [InlineData(-33.9, 151.2, 8, 14)]  // Sydney: the south pole
    [InlineData(10, 20, -5, -5)]       // low pole
    public void MeasuresAKnownErrorAndFollowsTheKnobs(double latitude, double longitude, double alt, double az)
    {
        var site = new GeoLocation(latitude, longitude, 0);
        var t0 = new DateTime(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);
        var lst = Horizon.LocalSiderealDegrees(t0, longitude) / 15;
        var dec = latitude >= 0 ? 40 : -40;
        // Start an hour east of the meridian and turn west 15° at a time, with the time passing as it would.
        var points = Enumerable.Range(0, 3)
            .Select(i => Observe(site, new EquatorialCoordinates((lst + 1 - i + 24) % 24, dec), t0.AddSeconds(40 * i), alt, az))
            .ToList();

        var pa = new PolarAlignment(site);
        var error = pa.Measure(points[0], points[1], points[2]);
        Assert.Equal(alt, error.AltitudeArcmin, 1);
        Assert.Equal(az, error.AzimuthArcmin, 1);

        // Tracking continues while the user turns the knobs halfway, then all the way.
        var mountNow = new EquatorialCoordinates((lst - 1 + 24) % 24, dec);
        var half = pa.Update(Observe(site, mountNow, t0.AddSeconds(200), alt / 2, az / 2));
        Assert.Equal(alt / 2, half.AltitudeArcmin, 1);
        Assert.Equal(az / 2, half.AzimuthArcmin, 1);
        var done = pa.Update(Observe(site, mountNow, t0.AddSeconds(400), 0, 0));
        Assert.True(done.TotalArcmin < 0.1, $"{done.TotalArcmin}′ left");
    }

    [Fact]
    public void WarnsWhereTheReadingsCannotBeTrusted()
    {
        Assert.Contains("due east", PolarAlignment.Warning(0.2, Horizon.FromAltAz(35, 92)));
        Assert.Contains("due west", PolarAlignment.Warning(0.2, Horizon.FromAltAz(20, 265)));
        Assert.Contains("straight up", PolarAlignment.Warning(0.2, Horizon.FromAltAz(80, 180)));
        Assert.Contains("3.5° off", PolarAlignment.Warning(3.5, Horizon.FromAltAz(45, 180)));
        Assert.Null(PolarAlignment.Warning(0.2, Horizon.FromAltAz(45, 150)));
    }

    [Fact]
    public void RejectsPointsThatDidNotMove()
    {
        var site = new GeoLocation(51.5, 0, 0);
        var t = new DateTime(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);
        var p = new PolarSample(new EquatorialCoordinates(20, 40), t);
        var error = Assert.Throws<ArgumentException>(() =>
            new PolarAlignment(site).Measure(p, p with { Utc = t.AddSeconds(5) }, p with { Utc = t.AddSeconds(10) }));
        Assert.Contains("doesn't seem to have turned", error.Message);
    }

    [Fact]
    public void RejectsPointsThatBarelyMoved()
    {
        // Near the pole a 10° RA turn moves the view only a fraction of a degree, which is fine; a few arcseconds isn't.
        var site = new GeoLocation(51.5, 0, 0);
        var t = new DateTime(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 3)
            .Select(i => new PolarSample(new EquatorialCoordinates(i * 0.0002, 89.95), t.AddSeconds(30 * i))).ToList();
        var error = Assert.Throws<ArgumentException>(() => new PolarAlignment(site).Measure(points[0], points[1], points[2]));
        Assert.Contains("doesn't seem to have turned", error.Message);
    }

    [Theory]
    [InlineData(0.5, "0° 00′ 30″")]
    [InlineData(83.4, "1° 23′ 24″")]
    [InlineData(-2.25, "0° 02′ 15″")]
    public void ShowsAnglesAsDegreesMinutesSeconds(double arcmin, string text) =>
        Assert.Equal(text, PolarAlignmentService.Dms(arcmin));
}

public class PolarAlignmentServiceTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private ActivityLog? _log;

    private async Task<(EquipmentService, PolarAlignmentService)> SetUpAsync(bool manual)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var hub = new NullHubContext();
        var log = _log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None); // the test sky can be anywhere, the Sun too
        var polar = new PolarAlignmentService(equipment, imaging, solver, sites, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        var mount = equipment.Mount!;
        await mount.SetSiteAsync(new GeoLocation(51.5, -0.1, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        await mount.UnparkAsync(CancellationToken.None);
        // Start in the east, far enough out that two 15° turns west stay east of the meridian (no flip).
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, -0.1) / 15;
        await mount.SlewAsync(new EquatorialCoordinates((lst + 2.5) % 24, 45), CancellationToken.None);
        Assert.Null(polar.UpdateSettings(new PolarAlignmentSettings(StepDegrees: 15, Direction: "West", ExposureSeconds: 0, Manual: manual)));
        return (equipment, polar);
    }

    private static async Task WaitFor(Func<bool> condition, string what, double seconds = 20)
    {
        for (var i = 0; i < seconds * 20 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task MeasuresTheSimulatorsErrorAndTracksTheKnobs()
    {
        var (equipment, polar) = await SetUpAsync(manual: false);
        var knobs = (IPolarAlignmentKnobs)equipment.Mount!;
        Assert.True(polar.Start(out var error), error);

        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the three points");
        Assert.Equal("Adjusting", polar.Status!.Phase);
        var initial = polar.Status.InitialError!;
        Assert.Equal(knobs.AltitudeErrorArcmin, initial.AltitudeArcmin, 0.3);
        Assert.Equal(knobs.AzimuthErrorArcmin, initial.AzimuthArcmin, 0.3);

        // Fix it with the knobs; the live error should follow to (nearly) zero.
        polar.TurnSimulatorKnobs(-knobs.AltitudeErrorArcmin, -knobs.AzimuthErrorArcmin);
        var frames = polar.Status.Frames;
        await WaitFor(() => polar.Status!.Frames >= frames + 2, "two more frames");
        Assert.True(polar.Status!.Error!.TotalArcmin < 0.3, $"{polar.Status.Error.TotalArcmin}′ left");

        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
        Assert.Equal("Done", polar.Status!.Phase);
    }

    [Fact]
    public async Task ManualModeWaitsForTheUserToTurnRa()
    {
        var (equipment, polar) = await SetUpAsync(manual: true);
        var mount = equipment.Mount!;
        Assert.True(polar.Start(out var error), error);

        // Point 2: turned "by hand", then Continue.
        await WaitFor(() => polar.Status?.WaitingForRotation == true, "the prompt for point 2");
        await mount.MoveRaAxisAsync(15, CancellationToken.None);
        Assert.True(polar.Continue());

        // Point 3: no Continue — once it has turned far enough and stopped, it carries on by itself.
        await WaitFor(() => polar.Status is { WaitingForRotation: true, Point: 3 }, "the prompt for point 3");
        await mount.MoveRaAxisAsync(7, CancellationToken.None);
        await WaitFor(() => polar.Status?.Message.Contains("of 15°") == true, "progress while turning");
        await mount.MoveRaAxisAsync(8, CancellationToken.None);

        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the measurement");
        Assert.Equal("Adjusting", polar.Status!.Phase);
        Assert.Equal(12, polar.Status.InitialError!.AltitudeArcmin, 0.3);
        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
    }

    [Fact]
    public async Task MeasuresRightNextToThePole()
    {
        // No more "too close to the pole": 2° from it, 10° RA turns move the view only about 0.35°, and that's plenty.
        var (equipment, polar) = await SetUpAsync(manual: false);
        var mount = equipment.Mount!;
        var knobs = (IPolarAlignmentKnobs)mount;
        await mount.SlewAsync(mount.Coordinates with { DecDegrees = 88 }, CancellationToken.None);
        polar.UpdateSettings(polar.Settings with { StartAutomatically = false, StepDegrees = 10, Direction = "East" });

        Assert.True(polar.Start(out var error), error);
        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the three points");
        Assert.Equal("Adjusting", polar.Status!.Phase);
        Assert.Equal(knobs.AltitudeErrorArcmin, polar.Status.InitialError!.AltitudeArcmin, 0.5);
        Assert.Equal(knobs.AzimuthErrorArcmin, polar.Status.InitialError.AzimuthArcmin, 0.5);
        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
    }

    [Fact]
    public async Task DrivesTheAxisAcrossTheMeridianWithoutAFlip()
    {
        // Starting just east of the meridian and turning west twice crosses it. Driving the RA axis (not GoTo)
        // keeps the mount on the same side, so this works — which is what lets polar alignment start almost anywhere.
        var (equipment, polar) = await SetUpAsync(manual: false);
        var mount = equipment.Mount!;
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, -0.1) / 15;
        await mount.SlewAsync(new EquatorialCoordinates((lst + 0.3) % 24, 30), CancellationToken.None);
        var side = mount.SideOfPier;
        polar.UpdateSettings(polar.Settings with { StartAutomatically = false }); // start right here

        Assert.True(polar.Start(out var error), error);
        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the three points");
        Assert.Equal("Adjusting", polar.Status!.Phase);
        Assert.Equal(12, polar.Status.InitialError!.AltitudeArcmin, 0.3);
        Assert.Equal(-9, polar.Status.InitialError.AzimuthArcmin, 0.3);
        Assert.Equal(side, mount.SideOfPier);
        Assert.True(Horizon.HourAngle(mount.Coordinates, DateTime.UtcNow, -0.1) > 0, "should have turned past the meridian");
        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
    }

    [Fact]
    public async Task StartsFromHomeJustAboveThePole()
    {
        // The mount at home (parked, pointing at the pole): Start does the rest.
        var (equipment, polar) = await SetUpAsync(manual: false);
        var mount = equipment.Mount!;
        await mount.ParkAsync(CancellationToken.None);
        var knobs = (IPolarAlignmentKnobs)mount;
        polar.UpdateSettings(new PolarAlignmentSettings(ExposureSeconds: 0)); // the defaults: 2° above the pole, 10° east

        Assert.True(polar.Start(out var error), error);
        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the three points");
        Assert.Equal("Adjusting", polar.Status!.Phase);
        Assert.Equal(knobs.AltitudeErrorArcmin, polar.Status.InitialError!.AltitudeArcmin, 0.3);
        Assert.Equal(knobs.AzimuthErrorArcmin, polar.Status.InitialError.AzimuthArcmin, 0.3);
        Assert.NotEqual(MountState.Parked, mount.State);
        Assert.InRange(mount.Coordinates.DecDegrees, 86.5, 89); // about 2° from the pole
        var (altitude, _) = Horizon.AltAz(Horizon.ToVector(mount.Coordinates, DateTime.UtcNow, new GeoLocation(51.5, -0.1, 0)));
        Assert.InRange(altitude, 48, 56); // nowhere near the zenith
        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
    }

    [Theory]
    [InlineData(48.52, -123.42, 2, 1)]
    [InlineData(51.5, -0.1, 5, -10)]
    [InlineData(-33.9, 151.2, 2, 1)]
    public void StartsAnOffsetFromThePole(double latitude, double longitude, double altitudeOffset, double azimuthOffset)
    {
        var site = new GeoLocation(latitude, longitude, 0);
        var settings = new PolarAlignmentSettings(AltitudeOffset: altitudeOffset, AzimuthOffset: azimuthOffset);
        var utc = new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc);
        var start = PolarAlignmentService.StartPosition(site, settings, utc);

        var (altitude, azimuth) = Horizon.AltAz(Horizon.ToVector(start, utc, site));
        Assert.Equal(Math.Abs(latitude) + altitudeOffset, altitude, 0.01);
        Assert.Equal(((latitude >= 0 ? 0 : 180) + azimuthOffset + 360) % 360, azimuth, 0.01);
    }

    [Fact]
    public void GoToOnlyMountsAreKeptOffTheMeridian()
    {
        // Mounts that can't drive the axis fall back to GoTo turns, which would flip across the meridian.
        var site = new GeoLocation(51.5, -0.1, 0);
        var lst = Horizon.LocalSiderealDegrees(DateTime.UtcNow, -0.1) / 15;
        var settings = new PolarAlignmentSettings(StepDegrees: 15, Direction: "West");
        var error = PolarAlignmentService.CrossesMeridian(new EquatorialCoordinates((lst + 1) % 24, 45), settings, site);
        Assert.Contains("cross the meridian", error);
        Assert.Contains("turn east", error);
        Assert.Null(PolarAlignmentService.CrossesMeridian(new EquatorialCoordinates((lst + 3) % 24, 45), settings, site));
    }

    [Fact]
    public void DescribesWhichWayToTurn()
    {
        var north = new GeoLocation(51.5, 0, 0);
        var south = new GeoLocation(-33.9, 151.2, 0);
        var error = new PolarError(5, 3, 5.8, 0, 0);
        Assert.Equal(("0° 05′ 00″ too high", "0° 03′ 00″ too far east"), PolarAlignmentService.Describe(error, north));
        Assert.Equal(("0° 05′ 00″ too high", "0° 03′ 00″ too far west"), PolarAlignmentService.Describe(error, south));
    }

    [Fact]
    public async Task OnThePoleFromHomeStartsWhereItIs()
    {
        // At home the mount already points at the pole: no GoTo first (it could swing RA up to half a turn round to
        // Polaris's side, and then the 90° turn on top could take the scope down by the tripod). Frame, turn, frame.
        var (equipment, polar) = await SetUpAsync(manual: false);
        var mount = equipment.Mount!;
        await mount.ParkAsync(CancellationToken.None);
        polar.UpdateSettings(new PolarAlignmentSettings(Method: "Pole", ExposureSeconds: 0));

        Assert.True(polar.Start(out var error), error);
        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the two frames", seconds: 90);
        Assert.True(polar.Status!.Phase == "Adjusting", polar.Status.Message);
        Assert.Contains(_log!.Recent, e => e.Message.Contains("already at the pole"));
        Assert.DoesNotContain(_log.Recent, e => e.Message.StartsWith("Polar alignment: starting at"));
        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
    }

    [Fact]
    public void KnowsWhenTheMountIsAtThePole()
    {
        var north = new GeoLocation(51.5, -0.1, 0);
        var south = new GeoLocation(-33.9, 151.2, 0);
        Assert.True(PolarAlignmentService.AtThePole(new EquatorialCoordinates(3, 89.5), north));
        Assert.False(PolarAlignmentService.AtThePole(new EquatorialCoordinates(3, 85), north));
        Assert.True(PolarAlignmentService.AtThePole(new EquatorialCoordinates(3, -89.5), south));
        Assert.False(PolarAlignmentService.AtThePole(new EquatorialCoordinates(3, 89.5), south));
    }

    [Fact]
    public async Task OnThePoleFindsTheAxisAndFollowsTheKnobs()
    {
        // The camera on the pole: two frames with RA turned 90° between them, then live while the knobs turn.
        var (equipment, polar) = await SetUpAsync(manual: false);
        var mount = equipment.Mount!;
        var knobs = (IPolarAlignmentKnobs)mount;
        polar.UpdateSettings(new PolarAlignmentSettings(Method: "Pole", ExposureSeconds: 0));

        Assert.True(polar.Start(out var error), error);
        await WaitFor(() => polar.Status?.Phase is "Adjusting" or "Failed", "the two frames", seconds: 90); // the simulator turns RA 4°/s
        Assert.True(polar.Status!.Phase == "Adjusting", polar.Status.Message);
        Assert.Equal(2, polar.Status.Points);
        var initial = polar.Status.InitialError!;
        Assert.Equal(knobs.AltitudeErrorArcmin, initial.AltitudeArcmin, 0.5);
        Assert.Equal(knobs.AzimuthErrorArcmin, initial.AzimuthArcmin, 0.5);

        // The markers for the image: the axis and the pole in pixels, as far apart as the error.
        var overlay = polar.Status.Overlay!;
        Assert.NotNull(overlay.Axis);
        Assert.NotNull(overlay.Pole);
        var apart = Math.Sqrt(Math.Pow(overlay.Axis![0] - overlay.Pole![0], 2) + Math.Pow(overlay.Axis[1] - overlay.Pole[1], 2))
                    * overlay.ArcsecPerPixel / 60;
        Assert.Equal(initial.TotalArcmin, apart, 0.5);

        polar.TurnSimulatorKnobs(-knobs.AltitudeErrorArcmin, -knobs.AzimuthErrorArcmin);
        var frames = polar.Status.Frames;
        await WaitFor(() => polar.Status!.Frames >= frames + 2, "two more frames");
        Assert.True(polar.Status!.Error!.TotalArcmin < 0.5, $"{polar.Status.Error.TotalArcmin}′ left");

        polar.Stop();
        await WaitFor(() => !polar.Running, "stopping");
        Assert.Equal("Done", polar.Status!.Phase);
    }
}
